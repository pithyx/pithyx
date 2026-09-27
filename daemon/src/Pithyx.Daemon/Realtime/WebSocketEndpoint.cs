using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Hosting;

namespace Pithyx.Daemon.Realtime;

/// <summary>The single WebSocket per client for server-pushed data. See <see cref="ClientMessage"/> and <see cref="ServerMessage"/>.</summary>
public static partial class WebSocketEndpoint
{
    public const string Path = "/ws";
    public const int MaxMessageBytes = 16 * 1024;
    private static readonly TimeSpan _sessionCheckInterval = TimeSpan.FromHours(1);

    public static void MapRealtime(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Map(Path, HandleAsync).ExcludeFromDescription();
        var lifetime = app.ServiceProvider.GetRequiredService<IHostApplicationLifetime>();
        var hub = app.ServiceProvider.GetRequiredService<TopicHub>();
        lifetime.ApplicationStopping.Register(hub.CloseAll);
    }

    private static async Task HandleAsync(
        HttpContext context,
        TopicHub hub,
        TopicRegistry registry,
        SessionService sessions,
        TimeProvider time,
        IOptions<JsonOptions> json,
        ILoggerFactory loggers)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            throw new PithyxException(ErrorCodes.RealtimeUpgradeRequired);
        }

        // Rejections are sent as close codes after the handshake: a browser reports a rejected handshake only as
        // 1006, which the desktop could not tell apart from a network error. Nothing else is sent before closing.
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        if (!IsSameOrigin(context.Request))
        {
            // Browsers send cookies with cross-site WebSocket requests, so the origin is the only protection.
            await RejectAsync(socket, WebSocketCloseCode.Forbidden, context.RequestAborted);
            return;
        }
        var sessionId = context.User.FindFirstValue(SessionAuthenticationHandler.SessionIdClaim);
        if (context.User.Identity?.IsAuthenticated != true || sessionId is null)
        {
            await RejectAsync(socket, WebSocketCloseCode.SessionEnded, context.RequestAborted);
            return;
        }

        var logger = loggers.CreateLogger(typeof(WebSocketEndpoint));
        var connection = hub.Connect(context.User, sessionId);
        LogConnected(logger, connection.Id);
        try
        {
            var writer = WriteAsync(socket, connection, context.RequestAborted);
            var reader = ReadAsync(socket, connection, hub, registry, json.Value.SerializerOptions, logger, context.RequestAborted);
            var session = WatchSessionAsync(connection, sessions, time, connection.Closing.Token);
            await Task.WhenAny(reader, writer);
            connection.Close(WebSocketCloseCode.NormalClosure);
            await writer;
            await session;
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        {
            // The client went away.
        }
        finally
        {
            hub.Disconnect(connection);
            LogDisconnected(logger, connection.Id, connection.CloseCode ?? WebSocketCloseCode.NormalClosure);
        }
    }

    private static async Task RejectAsync(WebSocket socket, WebSocketCloseCode code, CancellationToken aborted)
    {
        await socket.CloseOutputAsync((WebSocketCloseStatus)code, null, aborted);
        // Wait briefly for the client's close frame so the close handshake completes.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await socket.ReceiveAsync(new byte[128], timeout.Token);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        {
            // The client is gone already.
        }
    }

    // An open desktop keeps its session alive (sliding expiry), but the absolute lifetime still ends it; logout and
    // password reset close the connection right away through the hub.
    private static async Task WatchSessionAsync(TopicHub.Connection connection, SessionService sessions, TimeProvider time, CancellationToken closing)
    {
        try
        {
            while (!closing.IsCancellationRequested)
            {
                var absoluteExpiry = await sessions.TouchAsync(connection.SessionId, closing);
                if (absoluteExpiry is null)
                {
                    connection.Close(WebSocketCloseCode.SessionEnded);
                    return;
                }
                // The clock may have moved past the expiry since the touch (a jump, or a slow touch); the next touch
                // then ends the session. A negative delay would throw and leave the connection open for good.
                var untilExpiry = absoluteExpiry.Value - time.GetUtcNow();
                var wait = untilExpiry < TimeSpan.Zero ? TimeSpan.Zero : untilExpiry < _sessionCheckInterval ? untilExpiry : _sessionCheckInterval;
                await Task.Delay(wait, time, closing);
            }
        }
        catch (OperationCanceledException)
        {
            // The connection closed.
        }
    }

    internal static bool IsSameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        // Clients other than browsers send no Origin; they cannot be abused cross-site.
        return origin.Length == 0
            || (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task WriteAsync(WebSocket socket, TopicHub.Connection connection, CancellationToken aborted)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(aborted, connection.Closing.Token);
        try
        {
            await foreach (var payload in connection.Outbox.Reader.ReadAllAsync(linked.Token))
            {
                await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, aborted);
            }
        }
        catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
        {
            // Closing on the server side: fall through to the close handshake below.
        }

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            var code = connection.CloseCode ?? WebSocketCloseCode.NormalClosure;
            await socket.CloseOutputAsync((WebSocketCloseStatus)code, null, aborted);
        }
    }

    private static async Task ReadAsync(
        WebSocket socket,
        TopicHub.Connection connection,
        TopicHub hub,
        TopicRegistry registry,
        JsonSerializerOptions options,
        ILogger logger,
        CancellationToken aborted)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        while (socket.State == WebSocketState.Open)
        {
            buffer.ResetWrittenCount();
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer.GetMemory(4096), aborted);
                buffer.Advance(result.Count);
                if (buffer.WrittenCount > MaxMessageBytes)
                {
                    connection.Close(WebSocketCloseCode.MessageTooBig);
                    return;
                }
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }
            await HandleMessageAsync(buffer.WrittenSpan.ToArray(), connection, hub, registry, options, logger, aborted);
        }
    }

    private static async Task HandleMessageAsync(
        byte[] payload,
        TopicHub.Connection connection,
        TopicHub hub,
        TopicRegistry registry,
        JsonSerializerOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Every message gets its own request id, linked to the connection it arrived on.
        var connectionActivity = Activity.Current;
        Activity.Current = null;
        using var activity = Tracing.Source.StartActivity(
            "ws.message",
            ActivityKind.Server,
            parentContext: default,
            links: connectionActivity is null ? null : [new ActivityLink(connectionActivity.Context)]);
        ClientMessage? message = null;
        try
        {
            try
            {
                message = JsonSerializer.Deserialize<ClientMessage>(payload, options);
            }
            catch (JsonException)
            {
                message = null;
            }
            if (message is not { Type: "sub" or "unsub", Topic: not null })
            {
                Fail(hub, connection, message?.Id ?? "", ErrorCodes.RealtimeMessageInvalid, null);
                return;
            }

            var topicParams = new Dictionary<string, object?> { ["topic"] = message.Topic };
            if (!TopicPattern.IsValid(message.Topic, allowWildcards: true))
            {
                Fail(hub, connection, message.Id, ErrorCodes.RealtimeTopicInvalid, topicParams);
                return;
            }
            if (message.Type == "unsub")
            {
                hub.Unsubscribe(connection, message.Id, message.Topic);
                return;
            }
            if (!TopicPattern.HasWildcards(message.Topic))
            {
                var definition = registry.Find(message.Topic);
                if (definition is null)
                {
                    Fail(hub, connection, message.Id, ErrorCodes.RealtimeTopicUnknown, topicParams);
                    return;
                }
                if (!definition.IsAllowed(connection.User, message.Topic))
                {
                    Fail(hub, connection, message.Id, ErrorCodes.AuthPermissionDenied, topicParams);
                    return;
                }
            }
            await hub.SubscribeAsync(connection, message.Id, message.Topic, cancellationToken);
            LogSubscribed(logger, connection.Id, message.Topic);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or WebSocketException))
        {
            // A failing message (for example a busy database) must not end the connection:
            // the client would see a normal close and stop receiving live data without knowing why.
            LogMessageFailed(logger, exception, connection.Id);
            Fail(hub, connection, message?.Id ?? "", ErrorCodes.SystemInternalUnexpected, null);
        }
        finally
        {
            Activity.Current = connectionActivity;
        }
    }

    private static void Fail(TopicHub hub, TopicHub.Connection connection, string id, string code, IReadOnlyDictionary<string, object?>? parameters) =>
        hub.Send(connection, new ErrorMessage(id, code, parameters ?? new Dictionary<string, object?>(), Tracing.CurrentRequestId));

    [LoggerMessage(Level = LogLevel.Information, Message = "WebSocket connection {ConnectionId} opened")]
    private static partial void LogConnected(ILogger logger, Guid connectionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "WebSocket connection {ConnectionId} closed with {CloseCode}")]
    private static partial void LogDisconnected(ILogger logger, Guid connectionId, WebSocketCloseCode closeCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "WebSocket connection {ConnectionId} failed to handle a message")]
    private static partial void LogMessageFailed(ILogger logger, Exception exception, Guid connectionId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WebSocket connection {ConnectionId} subscribed to {Topic}")]
    private static partial void LogSubscribed(ILogger logger, Guid connectionId, string topic);
}
