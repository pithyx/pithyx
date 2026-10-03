using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Dapper;
using Pithyx.Daemon.Database;

namespace Pithyx.Daemon.FileServices.Ftp;

/// <summary>
/// FTP for software without SFTP (step 12, D291), inside the daemon: with TLS (explicit FTPS, <c>AUTH TLS</c> and
/// <c>PROT P</c>), and plain FTP only while an administrator allows it at the users' own risk; only passive data
/// connections, the same sign-in as WebDAV and the same file tree, so rights, owners and the recycle bin are those of
/// Files. Off until switched on in Settings > File services; it listens while it is on and closes every session when
/// it is switched off, and every plain session when plain FTP is.
/// </summary>
public sealed partial class FtpServer(
    SqliteConnectionFactory connections,
    ProtocolSignIn signIn,
    ProtocolFiles files,
    FileServiceChanges changes,
    FileServiceOptions options,
    TimeProvider time,
    ILoggerFactory loggers) : BackgroundService
{
    /// <summary>Passive ports when none are configured.</summary>
    public static readonly (int First, int Last) DefaultPassivePorts = (50000, 50099);

    private readonly ILogger<FtpServer> _logger = loggers.CreateLogger<FtpServer>();
    private readonly SemaphoreSlim _switching = new(1, 1);
    private readonly ConcurrentDictionary<Guid, FtpSession> _sessions = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _listening;
    private X509Certificate2? _certificate;
    private volatile bool _plainAllowed;

    public FileServiceState State { get; private set; } = FileServiceState.Off;

    /// <summary>The port it listens on while it runs; the configured one otherwise.</summary>
    public int Port { get; private set; } = options.FtpPort;

    /// <summary>The certificate's fingerprint once it was loaded or made.</summary>
    public string? Fingerprint => _certificate is null ? null : TlsCertificate.Fingerprint(_certificate);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.TlsDirectory is null)
        {
            return;
        }
        void Changed() => _ = ReconcileAsync(stoppingToken);
        changes.Changed += Changed;
        try
        {
            await ReconcileAsync(stoppingToken);
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            changes.Changed -= Changed;
            Stop();
        }
    }

    /// <summary>Starts or stops listening as the setting says. Never throws but for cancellation.</summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        if (options.TlsDirectory is null)
        {
            return;
        }
        await _switching.WaitAsync(cancellationToken);
        try
        {
            (long Enabled, long Plain) row;
            await using (var connection = await connections.OpenAsync(cancellationToken))
            {
                row = await connection.QuerySingleAsync<(long, long)>("SELECT ftp_enabled, ftp_plain FROM file_services WHERE id = 1");
            }
            var enabled = row.Enabled == 1;
            var plain = row.Plain == 1;
            if (plain != _plainAllowed)
            {
                _plainAllowed = plain;
                LogPlainChanged(plain);
                if (!plain)
                {
                    foreach (var session in _sessions.Values.Where(session => session.Plain))
                    {
                        session.Close();
                    }
                }
            }
            if (enabled && _listener is null)
            {
                Start();
            }
            else if (!enabled && _listener is not null)
            {
                Stop();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            State = FileServiceState.Failed;
            LogStartFailed(exception);
        }
        finally
        {
            _switching.Release();
        }
    }

    private void Start()
    {
        _certificate ??= TlsCertificate.LoadOrCreate(options.TlsDirectory!, Dns.GetHostName(), time);
        var listener = new TcpListener(IPAddress.IPv6Any, options.FtpPort);
        listener.Server.DualMode = true;
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _listening = new CancellationTokenSource();
        State = FileServiceState.Running;
        LogStarted(Port);
        _ = AcceptAsync(listener, _listening.Token);
    }

    private void Stop()
    {
        _listening?.Cancel();
        _listener?.Stop();
        _listener = null;
        _listening?.Dispose();
        _listening = null;
        foreach (var session in _sessions.Values)
        {
            session.Close();
        }
        State = FileServiceState.Off;
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            var session = new FtpSession(client, _certificate!, signIn, files, options.FtpPassivePorts ?? (options.FtpPort == 0 ? (0, 0) : DefaultPassivePorts), () => _plainAllowed, time, loggers.CreateLogger<FtpSession>());
            _sessions[session.Id] = session;
            _ = Task.Run(async () =>
            {
                try
                {
                    await session.RunAsync(cancellationToken);
                }
                finally
                {
                    _sessions.TryRemove(session.Id, out _);
                    session.Dispose();
                }
            }, CancellationToken.None);
        }
    }

    public override void Dispose()
    {
        Stop();
        _switching.Dispose();
        _certificate?.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "FTPS listens on port {Port}")]
    private partial void LogStarted(int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plain FTP without TLS allowed: {Allowed}")]
    private partial void LogPlainChanged(bool allowed);

    [LoggerMessage(Level = LogLevel.Error, Message = "FTPS could not start")]
    private partial void LogStartFailed(Exception exception);
}
