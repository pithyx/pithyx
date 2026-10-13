using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Pithyx.Daemon.ReverseProxy;

/// <summary>
/// The streams of the reverse proxy (D347): a TCP or UDP port of the box forwarded to a host and port, byte for byte,
/// without YARP. TCP copies both directions until either side closes; UDP keeps one socket to the target per client
/// address and forgets it after a minute of silence. Listeners start with the daemon and follow every change of the
/// streams (<see cref="ApplyAsync"/>); a port another program holds is reported as not listening.
/// </summary>
public sealed partial class StreamForwarder(ProxyService proxy, ILogger<StreamForwarder> logger) : BackgroundService
{
    public const int MaxConnections = 512;
    public const int MaxUdpClients = 1024;
    public static readonly TimeSpan UdpIdle = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private sealed class Running(ProxyStreamConfig config) : IDisposable
    {
        public ProxyStreamConfig Config { get; } = config;
        public CancellationTokenSource Stop { get; } = new();
        public Socket? Tcp { get; set; }
        public Socket? Udp { get; set; }

        public void Dispose()
        {
            Stop.Cancel();
            Tcp?.Dispose();
            Udp?.Dispose();
            Stop.Dispose();
        }
    }

    private readonly ConcurrentDictionary<long, Running> _running = new();
    private readonly SemaphoreSlim _apply = new(1, 1);

    /// <summary>Whether the stream listens now.</summary>
    public bool IsListening(long id) => _running.TryGetValue(id, out var running) && (running.Tcp is not null || running.Udp is not null);

    /// <summary>Whether a port can be taken for TCP and UDP; a stream that already listens there counts as free for itself.</summary>
    public bool PortFree(int port, bool tcp, bool udp, long? streamId)
    {
        if (streamId is { } id && _running.TryGetValue(id, out var own) && own.Config.Port == port)
        {
            return true;
        }
        try
        {
            if (tcp)
            {
                using var socket = Bind(port, SocketType.Stream, ProtocolType.Tcp);
            }
            if (udp)
            {
                using var socket = Bind(port, SocketType.Dgram, ProtocolType.Udp);
            }
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Starts, stops and restarts listeners so they match the stored streams.</summary>
    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        await _apply.WaitAsync(cancellationToken);
        try
        {
            var wanted = (await proxy.StreamsAsync(cancellationToken)).Where(stream => stream.Enabled).ToDictionary(stream => stream.Id);
            foreach (var (id, running) in _running.ToList())
            {
                if (!wanted.TryGetValue(id, out var config) || config != running.Config)
                {
                    _running.TryRemove(id, out _);
                    running.Dispose();
                }
            }
            foreach (var config in wanted.Values.Where(config => !_running.ContainsKey(config.Id)))
            {
                Start(config);
            }
        }
        finally
        {
            _apply.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ApplyAsync(stoppingToken);
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            foreach (var running in _running.Values)
            {
                running.Dispose();
            }
            _running.Clear();
        }
    }

    public override void Dispose()
    {
        _apply.Dispose();
        base.Dispose();
    }

    private void Start(ProxyStreamConfig config)
    {
        var running = new Running(config);
        try
        {
            if (config.Tcp)
            {
                running.Tcp = Bind(config.Port, SocketType.Stream, ProtocolType.Tcp);
                running.Tcp.Listen(128);
                _ = AcceptAsync(running, running.Tcp, running.Stop.Token);
            }
            if (config.Udp)
            {
                running.Udp = Bind(config.Port, SocketType.Dgram, ProtocolType.Udp);
                _ = ReceiveAsync(running, running.Udp, running.Stop.Token);
            }
            _running[config.Id] = running;
            LogStarted(config.Id, config.Port, config.TargetHost, config.TargetPort);
        }
        catch (SocketException exception)
        {
            running.Dispose();
            LogPortUnavailable(exception, config.Id, config.Port);
        }
    }

    // On every address, IPv4 and IPv6.
    private static Socket Bind(int port, SocketType type, ProtocolType protocol)
    {
        var socket = new Socket(AddressFamily.InterNetworkV6, type, protocol) { DualMode = true };
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task AcceptAsync(Running running, Socket listener, CancellationToken cancellationToken)
    {
        using var slots = new SemaphoreSlim(MaxConnections, MaxConnections);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptAsync(cancellationToken);
                if (!slots.Wait(0, CancellationToken.None))
                {
                    client.Dispose();
                    continue;
                }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await PipeAsync(running.Config, client, cancellationToken);
                    }
                    finally
                    {
                        slots.Release();
                    }
                }, CancellationToken.None);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // The stream was stopped or changed.
        }
    }

    private async Task PipeAsync(ProxyStreamConfig config, Socket client, CancellationToken cancellationToken)
    {
        using (client)
        using (var target = new Socket(SocketType.Stream, ProtocolType.Tcp))
        {
            try
            {
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    connect.CancelAfter(ConnectTimeout);
                    await target.ConnectAsync(config.TargetHost, config.TargetPort, connect.Token);
                }
                await using var clientStream = new NetworkStream(client, ownsSocket: false);
                await using var targetStream = new NetworkStream(target, ownsSocket: false);
                var up = CopyAsync(clientStream, target, targetStream, cancellationToken);
                var down = CopyAsync(targetStream, client, clientStream, cancellationToken);
                await Task.WhenAll(up, down);
            }
            catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                if (exception is SocketException socket && !cancellationToken.IsCancellationRequested)
                {
                    LogTargetFailed(config.Id, config.TargetHost, config.TargetPort, socket.SocketErrorCode);
                }
            }
        }
    }

    // One direction; when its source ends, the other side gets the end of the stream as well.
    private static async Task CopyAsync(Stream from, Socket toSocket, Stream to, CancellationToken cancellationToken)
    {
        try
        {
            await from.CopyToAsync(to, cancellationToken);
            toSocket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception exception) when (exception is SocketException or IOException or ObjectDisposedException)
        {
            toSocket.Close();
        }
    }

    private async Task ReceiveAsync(Running running, Socket listener, CancellationToken cancellationToken)
    {
        var clients = new ConcurrentDictionary<EndPoint, (Socket Socket, CancellationTokenSource Idle)>();
        var buffer = new byte[65536];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = await listener.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.IPv6Any, 0), cancellationToken);
                var from = received.RemoteEndPoint;
                if (!clients.TryGetValue(from, out var session))
                {
                    if (clients.Count >= MaxUdpClients)
                    {
                        continue;
                    }
                    try
                    {
                        var target = new Socket(SocketType.Dgram, ProtocolType.Udp);
                        await target.ConnectAsync(running.Config.TargetHost, running.Config.TargetPort, cancellationToken);
                        session = (target, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
                        clients[from] = session;
                        _ = ReplyAsync(listener, from, session.Socket, session.Idle, clients);
                    }
                    catch (SocketException exception)
                    {
                        LogTargetFailed(running.Config.Id, running.Config.TargetHost, running.Config.TargetPort, exception.SocketErrorCode);
                        continue;
                    }
                }
                try
                {
                    session.Idle.CancelAfter(UdpIdle);
                    await session.Socket.SendAsync(buffer.AsMemory(0, received.ReceivedBytes), SocketFlags.None, cancellationToken);
                }
                catch (Exception exception) when (exception is ObjectDisposedException or SocketException && !cancellationToken.IsCancellationRequested)
                {
                    // The client's session just ended; its next datagram starts a new one.
                    clients.TryRemove(from, out _);
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // The stream was stopped or changed.
        }
        finally
        {
            foreach (var session in clients.Values)
            {
                session.Idle.Cancel();
            }
        }
    }

    // The target's answers go back to the client until it stays silent for a minute.
    private static async Task ReplyAsync(Socket listener, EndPoint client, Socket target, CancellationTokenSource idle, ConcurrentDictionary<EndPoint, (Socket, CancellationTokenSource)> clients)
    {
        var buffer = new byte[65536];
        try
        {
            idle.CancelAfter(UdpIdle);
            while (true)
            {
                var length = await target.ReceiveAsync(buffer, SocketFlags.None, idle.Token);
                await listener.SendToAsync(buffer.AsMemory(0, length), SocketFlags.None, client, idle.Token);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Idle, stopped, or the target refused (an ICMP answer).
        }
        finally
        {
            clients.TryRemove(client, out _);
            target.Dispose();
            idle.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Stream {Id} listens on port {Port} for {TargetHost}:{TargetPort}")]
    private partial void LogStarted(long id, int port, string targetHost, int targetPort);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stream {Id} cannot listen on port {Port}; another program holds it")]
    private partial void LogPortUnavailable(Exception exception, long id, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stream {Id} cannot reach {TargetHost}:{TargetPort} ({Error})")]
    private partial void LogTargetFailed(long id, string targetHost, int targetPort, SocketError error);
}
