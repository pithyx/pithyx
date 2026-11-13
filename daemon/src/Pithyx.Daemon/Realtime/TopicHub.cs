using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Pithyx.Daemon.Hosting;

namespace Pithyx.Daemon.Realtime;

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
        private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
        private readonly List<(TopicDefinition? Definition, string Topic, ReadOnlyMemory<byte> Payload)> _held = [];

        public Guid Id { get; } = Guid.NewGuid();
        /// <summary>The user as of the last change of rights; every snapshot and event is checked against it.</summary>
        public ClaimsPrincipal User { get; internal set; } = user;
        public string SessionId { get; } = sessionId;
        public Channel<ReadOnlyMemory<byte>> Outbox { get; } = Channel.CreateBounded<ReadOnlyMemory<byte>>(
            new BoundedChannelOptions(OutboxCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        public CancellationTokenSource Closing { get; } = new();

        public WebSocketCloseCode? CloseCode { get; private set; }

        /// <summary>
        /// Adds a pattern that waits for its snapshots: until <see cref="Release"/>, events only it takes are held, so
        /// the client never gets an event for a topic before that topic's snapshot.
        /// </summary>
        internal void AddPendingPattern(string pattern)
        {
            lock (_lock)
            {
                if (_patterns.Add(pattern))
                {
                    _pending.Add(pattern);
                }
            }
        }

        /// <summary>Drops the held events of a topic whose snapshot is about to be read; the snapshot includes them.</summary>
        internal void Forget(string topic)
        {
            lock (_lock)
            {
                _held.RemoveAll(e => e.Topic == topic);
            }
        }

        /// <summary>Holds an event while every pattern that takes it still waits for snapshots; false to send it now.</summary>
        internal bool Hold(TopicDefinition? definition, string topic, ReadOnlyMemory<byte> payload)
        {
            lock (_lock)
            {
                if (_pending.Count == 0 || MatchingPatterns(definition, topic).Any(p => !_pending.Contains(p)))
                {
                    return false;
                }
                if (_held.Count < OutboxCapacity)
                {
                    _held.Add((definition, topic, payload));
                    return true;
                }
            }
            // As with a full outbox: dropping the client is safer than holding without limit.
            Close(WebSocketCloseCode.ClientTooSlow);
            return true;
        }

        /// <summary>Ends the wait of a pattern and passes the held events it now takes to <paramref name="send"/>, in order.</summary>
        internal void Release(string pattern, Action<ReadOnlyMemory<byte>> send)
        {
            lock (_lock)
            {
                _pending.Remove(pattern);
                FlushHeld(send);
            }
        }

        internal bool RemovePattern(string pattern, Action<ReadOnlyMemory<byte>> send)
        {
            lock (_lock)
            {
                _pending.Remove(pattern);
                var removed = _patterns.Remove(pattern);
                FlushHeld(send);
                return removed;
            }
        }

        private IEnumerable<string> MatchingPatterns(TopicDefinition? definition, string topic) =>
            definition is { ExactOnly: true }
                ? _patterns.Contains(topic) ? [topic] : []
                : _patterns.Where(pattern => TopicPattern.Matches(pattern, topic));

        private void FlushHeld(Action<ReadOnlyMemory<byte>> send)
        {
            _held.RemoveAll(held =>
            {
                var matching = MatchingPatterns(held.Definition, held.Topic).ToList();
                if (matching.Any(p => !_pending.Contains(p)))
                {
                    send(held.Payload);
                    return true;
                }
                return matching.Count == 0;
            });
        }

        /// <summary>The topics and patterns this connection is subscribed to.</summary>
        public IReadOnlyList<string> Patterns()
        {
            lock (_lock)
            {
                return [.. _patterns];
            }
        }

        /// <summary>
        /// Whether the connection takes the topic: through any matching pattern, or only through the topic itself for
        /// a definition that refuses wildcards (<see cref="TopicDefinition.ExactOnly"/>).
        /// </summary>
        internal bool Takes(TopicDefinition? definition, string topic)
        {
            if (definition is { ExactOnly: true })
            {
                lock (_lock)
                {
                    return _patterns.Contains(topic);
                }
            }
            return IsSubscribed(topic);
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
        Disconnected?.Invoke(connection);
        SubscribersRemoved?.Invoke();
    }

    /// <summary>Raised after a subscription or a connection ended, for publishers that stop work nobody watches.</summary>
    public event Action? SubscribersRemoved;

    /// <summary>Raised after a subscription was added and its snapshots were sent.</summary>
    public event Action? SubscribersAdded;

    /// <summary>Raised after a connection ended, with the subscriptions it had.</summary>
    public event Action<Connection>? Disconnected;

    /// <summary>The connections subscribed to a concrete topic that may see it.</summary>
    public IReadOnlyList<Connection> Subscribers(string topic)
    {
        var definition = _registry.Find(topic);
        return [.. _connections.Values.Where(c => c.Takes(definition, topic) && (definition is null || definition.IsAllowed(c.User, topic)))];
    }

    /// <summary>Raised by <see cref="CloseSession"/> with the session id, so the session's other sockets (the app proxy's) end too.</summary>
    public event Action<string>? SessionClosed;

    /// <summary>Raised by <see cref="CloseUser"/> with the user id, so the user's other sockets (the app proxy's) end too.</summary>
    public event Action<string>? UserClosed;

    /// <summary>Closes every connection of a session, for example after logout.</summary>
    public void CloseSession(string sessionId)
    {
        foreach (var connection in _connections.Values.Where(c => c.SessionId == sessionId))
        {
            connection.Close(WebSocketCloseCode.SessionEnded);
        }
        SessionClosed?.Invoke(sessionId);
    }

    /// <summary>Closes every connection of a user, for example after a password reset.</summary>
    public void CloseUser(string userId)
    {
        foreach (var connection in _connections.Values.Where(c => c.User.FindFirstValue(ClaimTypes.NameIdentifier) == userId))
        {
            connection.Close(WebSocketCloseCode.SessionEnded);
        }
        UserClosed?.Invoke(userId);
    }

    /// <summary>
    /// Gives every connection its user's current rights: <paramref name="refresh"/> returns the new principal, or null
    /// to close the connection because the user may no longer be signed in.
    /// </summary>
    public void Reauthorize(Func<ClaimsPrincipal, ClaimsPrincipal?> refresh)
    {
        ArgumentNullException.ThrowIfNull(refresh);
        foreach (var connection in _connections.Values)
        {
            if (refresh(connection.User) is { } user)
            {
                connection.User = user;
            }
            else
            {
                connection.Close(WebSocketCloseCode.SessionEnded);
            }
        }
        SubscribersRemoved?.Invoke();
    }

    /// <summary>Closes every connection because the daemon stops.</summary>
    public void CloseAll()
    {
        foreach (var connection in _connections.Values)
        {
            connection.Close(WebSocketCloseCode.DaemonRestart);
        }
    }

    /// <summary>
    /// Whether a connection that may see the topic is subscribed to it. Subscribers the topic refuses do not count,
    /// so nobody can make the daemon work for events it would never send them.
    /// </summary>
    public bool HasSubscribers(string topic)
    {
        var definition = _registry.Find(topic);
        return _connections.Values.Any(c => c.Takes(definition, topic) && (definition is null || definition.IsAllowed(c.User, topic)));
    }

    /// <summary>Completes when at least one connection that may see the topic subscribes to a pattern matching it.</summary>
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
        connection.AddPendingPattern(pattern);
        lock (_waitersLock)
        {
            foreach (var waiter in _waiters.Where(w => TopicPattern.Matches(pattern, w.Topic) && Allowed(connection.User, w.Topic)).ToList())
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
                // A definition that refuses wildcards answers only a subscription to one of its concrete topics.
                var matches = !definition.ExactOnly ? definition.ExistingTopics()
                    : TopicPattern.HasWildcards(pattern) || !TopicPattern.Matches(definition.Pattern, pattern) ? Enumerable.Empty<string>() : [pattern];
                foreach (var topic in matches.Where(t => TopicPattern.Matches(pattern, t) && definition.IsAllowed(connection.User, t)))
                {
                    connection.Forget(topic);
                    var data = await definition.Snapshot(topic, cancellationToken);
                    if (definition.ForUser is { } forUser)
                    {
                        if (forUser(connection.User, data) is not { } shaped)
                        {
                            continue;
                        }
                        data = shaped;
                    }
                    Send(connection, new SnapshotMessage(id, topic, data, Tracing.CurrentRequestId));
                }
            }
        }
        catch
        {
            // Without its snapshot the subscription would deliver changes to a state the client never got.
            connection.RemovePattern(pattern, payload => Enqueue(connection, payload));
            throw;
        }
        connection.Release(pattern, payload => Enqueue(connection, payload));
        SubscribersAdded?.Invoke();
    }

    private bool Allowed(ClaimsPrincipal user, string topic) => _registry.Find(topic)?.IsAllowed(user, topic) ?? true;

    public void Unsubscribe(Connection connection, string id, string pattern)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.RemovePattern(pattern, payload => Enqueue(connection, payload));
        // Publishers stop their work first, so it has stopped by the time the client reads the ack.
        SubscribersRemoved?.Invoke();
        Send(connection, new AckMessage(id, Tracing.CurrentRequestId));
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
            // A throttle retires once a whole interval passed without an event; offer to its successor then.
            while (!_throttles.GetOrAdd(topic, _ => new Throttle(this, topic, interval)).Offer(data, requestId))
            {
            }
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
            if (connection.Takes(definition, topic) && definition.IsAllowed(connection.User, topic))
            {
                if (definition.ForUser is { } forUser)
                {
                    if (forUser(connection.User, data) is { } shaped)
                    {
                        Route(connection, definition, topic, JsonSerializer.SerializeToUtf8Bytes<object>(new EventMessage(topic, shaped, requestId), _serializer));
                    }
                    continue;
                }
                payload ??= JsonSerializer.SerializeToUtf8Bytes<object>(new EventMessage(topic, data, requestId), _serializer);
                Route(connection, definition, topic, payload.Value);
            }
        }
    }

    private void Route(Connection connection, TopicDefinition definition, string topic, ReadOnlyMemory<byte> payload)
    {
        if (!connection.Hold(definition, topic, payload))
        {
            Enqueue(connection, payload);
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

    /// <summary>The topics with a throttle; a quiet topic loses its throttle after one interval.</summary>
    internal int ThrottleCount => _throttles.Count;

    /// <summary>
    /// At most one event per interval for a topic; the newest value wins. After every event a timer runs to the end of
    /// the interval, and when it finds nothing pending the throttle removes itself, so topics that went quiet (a
    /// finished job, a closed folder) keep nothing.
    /// </summary>
    private sealed class Throttle(TopicHub hub, string topic, TimeSpan interval)
    {
        private readonly Lock _lock = new();
        private (object Data, string RequestId)? _pending;
        private ITimer? _timer;
        private bool _retired;

        /// <summary>Sends or keeps the event; false when this throttle already retired.</summary>
        public bool Offer(object data, string requestId)
        {
            lock (_lock)
            {
                if (_retired)
                {
                    return false;
                }
                if (_timer is null)
                {
                    Send(data, requestId);
                    _timer = hub._time.CreateTimer(_ => Flush(), null, interval, Timeout.InfiniteTimeSpan);
                    return true;
                }
                _pending = (data, requestId);
                return true;
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
                    Send(pending.Data, pending.RequestId);
                    _timer = hub._time.CreateTimer(_ => Flush(), null, interval, Timeout.InfiniteTimeSpan);
                    return;
                }
                _retired = true;
                hub._throttles.TryRemove(new KeyValuePair<string, Throttle>(topic, this));
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
