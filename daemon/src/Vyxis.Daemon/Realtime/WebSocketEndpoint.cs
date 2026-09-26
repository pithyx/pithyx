using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Vyxis.Daemon.Auth;
using Vyxis.Daemon.Errors;
using Vyxis.Daemon.Hosting;

namespace Vyxis.Daemon.Realtime;

/// <summary>The single WebSocket per client for server-pushed data. See <see cref="ClientMessage"/> and <see cref="ServerMessage"/>.</summary>
public static partial class WebSocketEndpoint
{
    public const string Path = "/ws";
    public const int MaxMessageBytes = 16 * 1024;

    /// <summary>Close status when the session ended (logout, password reset). Clients go back to the login screen.</summary>
    public const WebSocketCloseStatus SessionEnded = (WebSocketCloseStatus)4401;

    public static void MapRealtime(this IEndpointRouteBuilder app) =>
        app.Map(Path, HandleAsync).RequireAuthorization().ExcludeFromDescription();

    private static async Task HandleAsync(HttpContext context, TopicHub hub, TopicRegistry registry, IOptions<JsonOptions> json, ILoggerFactory loggers)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            throw new VyxisException(ErrorCodes.RealtimeUpgradeRequired);
        }
        if (!IsSameOrigin(context.Request))
        {
            // Browsers send cookies with cross-site WebSocket requests, so the origin is the only protection.
            throw new VyxisException(ErrorCodes.SystemOriginForbidden);
        }

        var logger = loggers.CreateLogger(typeof(WebSocketEndpoint));
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connection = hub.Connect(context.User, context.User.FindFirstValue(SessionAuthenticationHandler.SessionIdClaim) ?? "");
        LogConnected(logger, connection.Id);
        try
        {
            var writer = WriteAsync(socket, connection, context.RequestAborted);
            var reader = ReadAsync(socket, connection, hub, registry, json.Value.SerializerOptions, logger, context.RequestAborted);
            await Task.WhenAny(reader, writer);
            connection.Close("connection ended");
            await writer;
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        {
            // The client went away.
        }
        finally
        {
            hub.Disconnect(connection);
            LogDisconnected(logger, connection.Id, connection.CloseReason ?? "");
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
            var status = connection.CloseReason switch
            {
                "session ended" => SessionEnded,
                "slow consumer" => WebSocketCloseStatus.PolicyViolation,
                "message too big" => WebSocketCloseStatus.MessageTooBig,
                _ => WebSocketCloseStatus.NormalClosure,
            };
            await socket.CloseOutputAsync(status, connection.CloseReason, aborted);
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
                    connection.Close("message too big");
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
        try
        {
            ClientMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<ClientMessage>(payload, options);
            }
            catch (JsonException)
            {
                message = null;
            }
            if (message is null || message.Type is not ("sub" or "unsub"))
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
        finally
        {
            Activity.Current = connectionActivity;
        }
    }

    private static void Fail(TopicHub hub, TopicHub.Connection connection, string id, string code, IReadOnlyDictionary<string, object?>? parameters) =>
        hub.Send(connection, new ErrorMessage(id, code, parameters ?? new Dictionary<string, object?>(), Tracing.CurrentRequestId));

    [LoggerMessage(Level = LogLevel.Information, Message = "WebSocket connection {ConnectionId} opened")]
    private static partial void LogConnected(ILogger logger, Guid connectionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "WebSocket connection {ConnectionId} closed: {Reason}")]
    private static partial void LogDisconnected(ILogger logger, Guid connectionId, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WebSocket connection {ConnectionId} subscribed to {Topic}")]
    private static partial void LogSubscribed(ILogger logger, Guid connectionId, string topic);
}
