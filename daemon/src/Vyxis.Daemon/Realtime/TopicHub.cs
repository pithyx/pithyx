using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Vyxis.Daemon.Hosting;

namespace Vyxis.Daemon.Realtime;

/// <summary>
/// Fans topic events out to WebSocket connections. Subscriptions are stored as patterns and matched on every publish,
/// so topics that appear later (a new pool, a new app) reach existing wildcard subscribers.
/// </summary>
public sealed partial class TopicHub(TopicRegistry registry, TimeProvider time, IOptions<JsonOptions> json, ILogger<TopicHub> logger)
{
    public const int OutboxCapacity = 256;

    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();
    private readonly ConcurrentDictionary<string, Throttle> _throttles = new(StringComparer.Ordinal);
    private readonly Lock _waitersLock = new();
    private readonly List<(string Topic, TaskCompletionSource Signal)> _waiters = [];
    private readonly JsonSerializerOptions _serializer = json.Value.SerializerOptions;
    private readonly TopicRegistry _registry = registry;
    private readonly TimeProvider _time = time;

    /// <summary>One WebSocket client.</summary>
    public sealed class Connection(ClaimsPrincipal user, string sessionId)
    {
        private readonly Lock _lock = new();
        private readonly HashSet<string> _patterns = new(StringComparer.Ordinal);

        public Guid Id { get; } = Guid.NewGuid();
        public ClaimsPrincipal User { get; } = user;
        public string SessionId { get; } = sessionId;
        public Channel<ReadOnlyMemory<byte>> Outbox { get; } = Channel.CreateBounded<ReadOnlyMemory<byte>>(
            new BoundedChannelOptions(OutboxCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        public CancellationTokenSource Closing { get; } = new();

        public WebSocketCloseCode? CloseCode { get; private set; }

        internal bool AddPattern(string pattern)
        {
            lock (_lock)
            {
                return _patterns.Add(pattern);
            }
        }

        internal bool RemovePattern(string pattern)
        {
            lock (_lock)
            {
                return _patterns.Remove(pattern);
            }
        }

        internal bool IsSubscribed(string topic)
        {
            lock (_lock)
            {
                return _patterns.Any(pattern => TopicPattern.Matches(pattern, topic));
            }
        }

        public void Close(WebSocketCloseCode code)
        {
            CloseCode ??= code;
            Closing.Cancel();
        }
    }

    public Connection Connect(ClaimsPrincipal user, string sessionId)
    {
        var connection = new Connection(user, sessionId);
        _connections[connection.Id] = connection;
        return connection;
    }

    public void Disconnect(Connection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connections.TryRemove(connection.Id, out _);
        connection.Outbox.Writer.TryComplete();
        SubscribersRemoved?.Invoke();
    }

    /// <summary>Raised after a subscription or a connection ended, for publishers that stop work nobody watches.</summary>
    public event Action? SubscribersRemoved;

    /// <summary>Closes every connection of a session, for example after logout.</summary>
    public void CloseSession(string sessionId)
    {
        foreach (var connection in _connections.Values.Where(c => c.SessionId == sessionId))
        {
            connection.Close(WebSocketCloseCode.SessionEnded);
        }
    }

    /// <summary>Closes every connection of a user, for example after a password reset.</summary>
    public void CloseUser(string userId)
    {
        foreach (var connection in _connections.Values.Where(c => c.User.FindFirstValue(ClaimTypes.NameIdentifier) == userId))
        {
            connection.Close(WebSocketCloseCode.SessionEnded);
        }
    }

    /// <summary>Closes every connection because the daemon stops.</summary>
    public void CloseAll()
    {
        foreach (var connection in _connections.Values)
        {
            connection.Close(WebSocketCloseCode.DaemonRestart);
        }
    }

    public bool HasSubscribers(string topic) => _connections.Values.Any(c => c.IsSubscribed(topic));

    /// <summary>Completes when at least one connection subscribes to a pattern matching the topic.</summary>
    public Task WaitForSubscribersAsync(string topic, CancellationToken cancellationToken)
    {
        lock (_waitersLock)
        {
            if (HasSubscribers(topic))
            {
                return Task.CompletedTask;
            }
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((topic, signal));
            return signal.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Adds a subscription and sends the ack plus the snapshots of all matching topics the user may see.</summary>
    public async Task SubscribeAsync(Connection connection, string id, string pattern, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.AddPattern(pattern);
        lock (_waitersLock)
        {
            foreach (var waiter in _waiters.Where(w => TopicPattern.Matches(pattern, w.Topic)).ToList())
            {
                waiter.Signal.TrySetResult();
                _waiters.Remove(waiter);
            }
        }

        Send(connection, new AckMessage(id, Tracing.CurrentRequestId));
        try
        {
            foreach (var definition in _registry.All)
            {
                foreach (var topic in definition.ExistingTopics().Where(t => TopicPattern.Matches(pattern, t) && definition.IsAllowed(connection.User, t)))
                {
                    var data = await definition.Snapshot(topic, cancellationToken);
                    Send(connection, new SnapshotMessage(id, topic, data, Tracing.CurrentRequestId));
                }
            }
        }
        catch
        {
            // Without its snapshot the subscription would deliver changes to a state the client never got.
            connection.RemovePattern(pattern);
            throw;
        }
    }

    public void Unsubscribe(Connection connection, string id, string pattern)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.RemovePattern(pattern);
        Send(connection, new AckMessage(id, Tracing.CurrentRequestId));
        SubscribersRemoved?.Invoke();
    }

    public void Send(Connection connection, ServerMessage message)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Enqueue(connection, JsonSerializer.SerializeToUtf8Bytes<object>(message, _serializer));
    }

    /// <summary>
    /// Publishes a change of a concrete topic to every subscriber allowed to see it. The request id of the current
    /// activity (the action that caused the change) travels with the event.
    /// </summary>
    public void Publish(string topic, object data)
    {
        ArgumentNullException.ThrowIfNull(topic);
        var definition = _registry.Find(topic) ?? throw new InvalidOperationException($"Topic {topic} is not registered.");
        var requestId = Tracing.CurrentRequestId is { Length: > 0 } current ? current : ActivityTraceId.CreateRandom().ToHexString();
        if (definition.MinInterval is { } interval)
        {
            _throttles.GetOrAdd(topic, _ => new Throttle(this, topic, interval)).Offer(data, requestId);
        }
        else
        {
            Deliver(definition, topic, data, requestId);
        }
    }

    private void Deliver(TopicDefinition definition, string topic, object data, string requestId)
    {
        ReadOnlyMemory<byte>? payload = null;
        foreach (var connection in _connections.Values)
        {
            if (connection.IsSubscribed(topic) && definition.IsAllowed(connection.User, topic))
            {
                payload ??= JsonSerializer.SerializeToUtf8Bytes<object>(new EventMessage(topic, data, requestId), _serializer);
                Enqueue(connection, payload.Value);
            }
        }
    }

    private void Enqueue(Connection connection, ReadOnlyMemory<byte> payload)
    {
        if (!connection.Outbox.Writer.TryWrite(payload))
        {
            // The client does not read fast enough. Dropping it is safer than buffering without limit.
            LogSlowConsumer(connection.Id);
            connection.Close(WebSocketCloseCode.ClientTooSlow);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Closing WebSocket connection {ConnectionId}: outbox full")]
    private partial void LogSlowConsumer(Guid connectionId);

    /// <summary>At most one event per interval for a topic; the newest value wins.</summary>
    private sealed class Throttle(TopicHub hub, string topic, TimeSpan interval)
    {
        private readonly Lock _lock = new();
        private DateTimeOffset _lastSent = DateTimeOffset.MinValue;
        private (object Data, string RequestId)? _pending;
        private ITimer? _timer;

        public void Offer(object data, string requestId)
        {
            lock (_lock)
            {
                var now = hub._time.GetUtcNow();
                if (_timer is null && now - _lastSent >= interval)
                {
                    _lastSent = now;
                    Send(data, requestId);
                    return;
                }
                _pending = (data, requestId);
                _timer ??= hub._time.CreateTimer(_ => Flush(), null, _lastSent + interval - now, Timeout.InfiniteTimeSpan);
            }
        }

        private void Flush()
        {
            lock (_lock)
            {
                _timer?.Dispose();
                _timer = null;
                if (_pending is { } pending)
                {
                    _pending = null;
                    _lastSent = hub._time.GetUtcNow();
                    Send(pending.Data, pending.RequestId);
                }
            }
        }

        private void Send(object data, string requestId)
        {
            if (hub._registry.Find(topic) is { } definition)
            {
                hub.Deliver(definition, topic, data, requestId);
            }
        }
    }
}
