using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Pithyx.Daemon.Apps.Docker;

/// <summary>An app's socket of the Docker proxy: who may connect, what the filter knows, and whether it filters at all.</summary>
/// <param name="App">The app and what the filter needs about it.</param>
/// <param name="Uid">The app's uid; a process of its containers connects with it, or with the remapped root of <c>backend.root</c>.</param>
/// <param name="Root">The app runs as root in its containers, which the box sees as the remapped uids above <see cref="DockerHost.RemapBase"/>.</param>
/// <param name="Raw">The app holds <c>docker.socket.raw</c>: every request goes to the Engine as it is.</param>
public sealed record DockerProxySocket(DockerProxyApp App, long Uid, bool Root, bool Raw);

/// <summary>
/// The Docker socket of apps with <c>docker.socket</c> or <c>docker.socket.raw</c> (D387): one Unix socket per app at
/// <c>/run/pithyx/docker-proxy/&lt;id&gt;.sock</c>, mounted into its containers where the Compose file mounts the Docker
/// socket. A connection is taken only from the app's own uid (its remapped root for <c>backend.root</c>) or root, checked
/// with <c>SO_PEERCRED</c>. With <c>docker.socket</c> every request goes through <see cref="DockerApiFilter"/>: one request
/// per connection (<c>Connection: close</c> to the Engine), objects of other apps answer 404 as if they did not exist, and
/// only attach and exec start keep the connection open both ways. With <c>docker.socket.raw</c> the connection is piped to
/// the Engine unchanged. Only real mode opens sockets.
/// </summary>
public sealed partial class DockerSocketProxy(DockerOptions options, DockerEngine engine, ILogger<DockerSocketProxy> logger) : IDisposable
{
    public const string Directory = "/run/pithyx/docker-proxy";

    /// <summary>The most connections one app may hold open at once.</summary>
    public const int MaxConnections = 64;

    private const int MaxHead = 64 * 1024;
    private static readonly TimeSpan _headTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, Listener> _listeners = new(StringComparer.Ordinal);

    private sealed class Listener(DockerProxySocket socket, Socket server, CancellationTokenSource stop)
    {
        public DockerProxySocket Socket { get; } = socket;
        public Socket Server { get; } = server;
        public CancellationTokenSource Stop { get; } = stop;
        public int Connections;
    }

    /// <summary>The path of an app's socket.</summary>
    public static string PathOf(string appId) => System.IO.Path.Combine(Directory, appId + ".sock");

    /// <summary>Opens (or opens again) an app's socket before its backend starts and returns its path.</summary>
    public string Open(DockerProxySocket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var path = PathOf(socket.App.AppId);
        Close(socket.App.AppId);
        if (!options.Apply)
        {
            return path;
        }
        System.IO.Directory.CreateDirectory(Directory);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        File.Delete(path);
        var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        server.Bind(new UnixDomainSocketEndPoint(path));
        // Who may talk is decided by SO_PEERCRED; the mode only lets the container's process reach the check.
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        }
        server.Listen(16);
        var listener = new Listener(socket, server, new CancellationTokenSource());
        _listeners[socket.App.AppId] = listener;
        _ = AcceptAsync(listener);
        LogOpened(socket.App.AppId, socket.Raw);
        return path;
    }

    /// <summary>Closes an app's socket when its backend stops; open connections end.</summary>
    public void Close(string appId)
    {
        if (_listeners.TryRemove(appId, out var listener))
        {
            listener.Stop.Cancel();
            listener.Server.Dispose();
            listener.Stop.Dispose();
            try
            {
                File.Delete(PathOf(appId));
            }
            catch (IOException)
            {
                // Gone already.
            }
        }
    }

    public bool IsOpen(string appId) => _listeners.ContainsKey(appId);

    public void Dispose()
    {
        foreach (var appId in _listeners.Keys)
        {
            Close(appId);
        }
    }

    /// <summary>Whether a peer may use the socket: root, the app's uid, or for a root app the remapped ids of its containers.</summary>
    public static bool MayConnect(DockerProxySocket socket, long peerUid)
    {
        ArgumentNullException.ThrowIfNull(socket);
        return peerUid == 0
            || peerUid == socket.Uid
            || (socket.Root && peerUid >= DockerHost.RemapBase && peerUid < DockerHost.RemapBase + 65536);
    }

    private async Task AcceptAsync(Listener listener)
    {
        var token = listener.Stop.Token;
        while (!token.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.Server.AcceptAsync(token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = ServeAsync(listener, client, token);
        }
    }

    private async Task ServeAsync(Listener listener, Socket client, CancellationToken token)
    {
        using (client)
        {
            if (Interlocked.Increment(ref listener.Connections) > MaxConnections)
            {
                Interlocked.Decrement(ref listener.Connections);
                return;
            }
            try
            {
                if (PeerUid(client) is not { } uid || !MayConnect(listener.Socket, uid))
                {
                    LogPeerRefused(listener.Socket.App.AppId, PeerUid(client));
                    return;
                }
                using var stream = new NetworkStream(client, ownsSocket: false);
                if (listener.Socket.Raw)
                {
                    using var engineSocket = await ConnectEngineAsync(token);
                    using var engineStream = new NetworkStream(engineSocket, ownsSocket: false);
                    await PipeAsync(stream, engineStream, token);
                }
                else
                {
                    await FilteredAsync(listener.Socket.App, stream, token);
                }
            }
#pragma warning disable CA1031 // One broken connection must not end the socket.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogConnectionFailed(exception, listener.Socket.App.AppId);
            }
            finally
            {
                Interlocked.Decrement(ref listener.Connections);
            }
        }
    }

    /// <summary>One filtered request: read, decide, forward with <c>Connection: close</c>, copy the answer back.</summary>
    private async Task FilteredAsync(DockerProxyApp app, NetworkStream client, CancellationToken token)
    {
        using var headTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        headTimeout.CancelAfter(_headTimeout);
        var request = await HttpHead.ReadAsync(client, MaxHead, headTimeout.Token);
        if (request is null)
        {
            await ErrorAsync(client, 400, "the request could not be read", token);
            return;
        }
        if (request.Header("Transfer-Encoding") is not null)
        {
            await ErrorAsync(client, 411, "send the body with Content-Length", token);
            return;
        }
        var length = request.Header("Content-Length") is { } text && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        if (length > DockerApiFilter.MaxBody)
        {
            await ErrorAsync(client, 413, "the body is too large", token);
            return;
        }
        var body = new byte[length];
        var have = Math.Min(request.Rest.Length, body.Length);
        request.Rest.AsSpan(0, have).CopyTo(body);
        if (have < length)
        {
            await client.ReadExactlyAsync(body.AsMemory(have), headTimeout.Token);
        }

        var route = DockerApiFilter.Route(request.Method, request.Path, request.Query);
        var target = request.Target;
        var bodyText = Encoding.UTF8.GetString(body);
        var forwarded = body;
        var reason = route.Reason;
        switch (route.Kind)
        {
            case DockerRouteKind.Filtered:
                target = WithQuery(request, "filters", DockerApiFilter.LabelFilter(request.Query.GetValueOrDefault("filters"), app.AppId));
                break;
            case DockerRouteKind.CreateContainer:
                var volumes = new List<string>();
                (var created, reason) = DockerApiFilter.CreateContainer(bodyText, app, volumes);
                if (created is not null)
                {
                    foreach (var volume in volumes)
                    {
                        if (!await OwnsAsync($"volumes/{Uri.EscapeDataString(volume)}", app.AppId, token))
                        {
                            reason = $"the volume {volume} is not one this app created";
                            break;
                        }
                    }
                    forwarded = Encoding.UTF8.GetBytes(created);
                }
                break;
            case DockerRouteKind.Container:
            case DockerRouteKind.CreateExec:
                if (await ContainerIdAsync(route.Id!, app.AppId, token) is not { } containerId)
                {
                    await ErrorAsync(client, 404, $"No such container: {route.Id}", token);
                    return;
                }
                target = ReplaceSegment(request, route.Id!, containerId);
                if (route.Kind == DockerRouteKind.CreateExec)
                {
                    reason = DockerApiFilter.CreateExecProblem(bodyText);
                }
                break;
            case DockerRouteKind.Exec:
                using (var exec = await engine.InspectAsync($"exec/{Uri.EscapeDataString(route.Id!)}/json", token))
                {
                    if (exec is null
                        || !exec.RootElement.TryGetProperty("ContainerID", out var owner)
                        || await ContainerIdAsync(owner.GetString()!, app.AppId, token) is null)
                    {
                        await ErrorAsync(client, 404, $"No such exec instance: {route.Id}", token);
                        return;
                    }
                }
                break;
            case DockerRouteKind.PullImage:
                if (!app.Internet)
                {
                    reason = "pulling images needs the permission network.internet";
                }
                break;
            case DockerRouteKind.CreateVolume:
                (var volumeBody, reason) = DockerApiFilter.CreateVolume(bodyText, app.AppId);
                forwarded = volumeBody is null ? forwarded : Encoding.UTF8.GetBytes(volumeBody);
                break;
            case DockerRouteKind.Volume:
                if (!await OwnsAsync($"volumes/{Uri.EscapeDataString(route.Id!)}", app.AppId, token))
                {
                    await ErrorAsync(client, 404, $"get {route.Id}: no such volume", token);
                    return;
                }
                break;
        }
        if (route.Kind == DockerRouteKind.Deny || reason is not null)
        {
            LogRefused(app.AppId, request.Method, DockerApiFilter.Unversioned(request.Path), reason ?? "");
            await ErrorAsync(client, 403, reason ?? "refused", token);
            return;
        }

        using var engineSocket = await ConnectEngineAsync(token);
        using var engineStream = new NetworkStream(engineSocket, ownsSocket: false);
        await engineStream.WriteAsync(request.Forward(target, forwarded.Length, route.Hijack), token);
        await engineStream.WriteAsync(forwarded, token);
        if (route.Hijack)
        {
            await PipeAsync(client, engineStream, token);
        }
        else
        {
            await engineStream.CopyToAsync(client, token);
        }
    }

    /// <summary>The full id of a container of the app by id or name, or null when it is not the app's or not there.</summary>
    private async Task<string?> ContainerIdAsync(string idOrName, string appId, CancellationToken token)
    {
        using var container = await engine.InspectAsync($"containers/{Uri.EscapeDataString(idOrName)}/json", token);
        return container is not null && DockerApiFilter.Owns(container.RootElement, appId) ? container.RootElement.GetProperty("Id").GetString() : null;
    }

    private async Task<bool> OwnsAsync(string path, string appId, CancellationToken token)
    {
        using var inspected = await engine.InspectAsync(path, token);
        return inspected is not null && DockerApiFilter.Owns(inspected.RootElement, appId);
    }

    private static string WithQuery(HttpHead request, string name, string value)
    {
        var query = new Dictionary<string, string>(request.Query, StringComparer.Ordinal) { [name] = value };
        return request.Path + "?" + string.Join('&', query.Select(entry => Uri.EscapeDataString(entry.Key) + "=" + Uri.EscapeDataString(entry.Value)));
    }

    private static string ReplaceSegment(HttpHead request, string id, string replacement)
    {
        var escaped = "/" + Uri.EscapeDataString(id) + "/";
        var path = (request.Path + "/").Replace("/" + id + "/", "/" + replacement + "/", StringComparison.Ordinal)
            .Replace(escaped, "/" + replacement + "/", StringComparison.Ordinal).TrimEnd('/');
        var queryStart = request.Target.IndexOf('?', StringComparison.Ordinal);
        return queryStart < 0 ? path : path + request.Target[queryStart..];
    }

    private async Task<Socket> ConnectEngineAsync(CancellationToken token)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(options.SocketPath), token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Copies both ways until either side closes.</summary>
    private static async Task PipeAsync(Stream client, Stream engineStream, CancellationToken token)
    {
        using var done = CancellationTokenSource.CreateLinkedTokenSource(token);
        var up = client.CopyToAsync(engineStream, done.Token);
        var down = engineStream.CopyToAsync(client, done.Token);
        await Task.WhenAny(up, down);
        await done.CancelAsync();
        try
        {
            await Task.WhenAll(up, down);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException)
        {
            // One side closed; the other ends with it.
        }
    }

    /// <summary>An answer as the Engine words its errors: JSON with <c>message</c>.</summary>
    private static async Task ErrorAsync(Stream client, int status, string message, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["message"] = "pithyx: " + message });
        var head = $"HTTP/1.1 {status.ToString(CultureInfo.InvariantCulture)} {Phrase(status)}\r\nContent-Type: application/json\r\nContent-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\nConnection: close\r\n\r\n";
        await client.WriteAsync(Encoding.ASCII.GetBytes(head), token);
        await client.WriteAsync(body, token);
    }

    private static string Phrase(int status) => status switch
    {
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        411 => "Length Required",
        413 => "Payload Too Large",
        _ => "Error",
    };

    /// <summary>The uid of the process at the other end of a Unix socket (<c>SO_PEERCRED</c>), or null off Linux.</summary>
    private static long? PeerUid(Socket socket)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }
        const int solSocket = 1;
        const int soPeerCred = 17;
        Span<byte> credentials = stackalloc byte[12];
        var read = socket.GetRawSocketOption(solSocket, soPeerCred, credentials);
        return read < 12 ? null : MemoryMarshal.Read<uint>(credentials[4..8]);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Opened the Docker socket of app {AppId} (raw: {Raw})")]
    private partial void LogOpened(string appId, bool raw);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused a connection to the Docker socket of app {AppId} from uid {Uid}")]
    private partial void LogPeerRefused(string appId, long? uid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused {Method} {Path} on the Docker socket of app {AppId}: {Reason}")]
    private partial void LogRefused(string appId, string method, string path, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "A connection to the Docker socket of app {AppId} failed")]
    private partial void LogConnectionFailed(Exception exception, string appId);
}

/// <summary>The head of an HTTP/1.1 request as the proxy reads it, with what was read past it.</summary>
public sealed class HttpHead
{
    private HttpHead(string method, string target, List<(string Name, string Value)> headers, byte[] rest)
    {
        Method = method;
        Target = target;
        Headers = headers;
        Rest = rest;
        var question = target.IndexOf('?', StringComparison.Ordinal);
        Path = question < 0 ? target : target[..question];
        Query = question < 0 ? new Dictionary<string, string>(StringComparer.Ordinal) : ParseQuery(target[(question + 1)..]);
    }

    public string Method { get; }

    /// <summary>The request target as sent: path and query.</summary>
    public string Target { get; }

    public string Path { get; }

    public IReadOnlyDictionary<string, string> Query { get; }

    public IReadOnlyList<(string Name, string Value)> Headers { get; }

    /// <summary>Bytes read after the head, the start of the body.</summary>
    public byte[] Rest { get; }

    public string? Header(string name) =>
        Headers.Where(header => header.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(header => header.Value).FirstOrDefault();

    /// <summary>Parses a request head; null when it is not one.</summary>
    public static HttpHead? Parse(ReadOnlySpan<byte> head, byte[] rest)
    {
        var text = Encoding.ASCII.GetString(head);
        var lines = text.Split("\r\n");
        var start = lines[0].Split(' ');
        if (start.Length != 3 || !start[2].StartsWith("HTTP/1.", StringComparison.Ordinal) || !start[1].StartsWith('/') || start[0].Any(character => !char.IsAsciiLetterUpper(character)))
        {
            return null;
        }
        var headers = new List<(string, string)>();
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || line.Any(character => character is '\r' or '\n' or '\0'))
            {
                return null;
            }
            headers.Add((line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }
        return new HttpHead(start[0], start[1], headers, rest);
    }

    /// <summary>Reads up to the end of the head; null for a head that is too long, cut off or malformed.</summary>
    public static async Task<HttpHead?> ReadAsync(Stream stream, int maxHead, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = ArrayPool<byte>.Shared.Rent(maxHead);
        try
        {
            var filled = 0;
            while (filled < maxHead)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(filled, maxHead - filled), token);
                if (read == 0)
                {
                    return null;
                }
                filled += read;
                var end = buffer.AsSpan(0, filled).IndexOf("\r\n\r\n"u8);
                if (end >= 0)
                {
                    return Parse(buffer.AsSpan(0, end), buffer.AsSpan(end + 4, filled - end - 4).ToArray());
                }
            }
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// The head to send to the Engine: the same headers without the hop ones, the new target and length, and
    /// <c>Connection: close</c> so the Engine answers one request; a hijacked stream keeps its <c>Upgrade</c>.
    /// </summary>
    public byte[] Forward(string target, int contentLength, bool hijack)
    {
        var head = new StringBuilder();
        head.Append(CultureInfo.InvariantCulture, $"{Method} {target} HTTP/1.1\r\n");
        foreach (var (name, value) in Headers)
        {
            if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
                || (!hijack && name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            head.Append(CultureInfo.InvariantCulture, $"{name}: {value}\r\n");
        }
        if (contentLength > 0 || Method is "POST" or "PUT")
        {
            head.Append(CultureInfo.InvariantCulture, $"Content-Length: {contentLength}\r\n");
        }
        head.Append(hijack && Header("Upgrade") is not null ? "Connection: Upgrade\r\n" : "Connection: close\r\n");
        head.Append("\r\n");
        return Encoding.ASCII.GetBytes(head.ToString());
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var name = Uri.UnescapeDataString((equals < 0 ? pair : pair[..equals]).Replace('+', ' '));
            values[name] = equals < 0 ? "" : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
        }
        return values;
    }
}
