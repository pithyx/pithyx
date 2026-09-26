using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Vyxis.Daemon.Realtime;

namespace Vyxis.Daemon.Tests.Realtime;

public sealed class TopicHubTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly TopicRegistry _registry = new([]);
    private readonly TopicHub _hub;
    private readonly ClaimsPrincipal _user = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1")], "test"));

    public TopicHubTests() => _hub = new TopicHub(_registry, _time, Options.Create(new JsonOptions()), NullLogger<TopicHub>.Instance);

    private static TopicDefinition Topic(string pattern, TimeSpan? interval = null, Func<IEnumerable<string>>? current = null) =>
        new(pattern, typeof(int), typeof(int), (_, _) => ValueTask.FromResult<object>(0), TopicDefinition.AnyUser, interval, current);

    private static List<JsonElement> Drain(TopicHub.Connection connection)
    {
        var messages = new List<JsonElement>();
        while (connection.Outbox.Reader.TryRead(out var payload))
        {
            messages.Add(JsonDocument.Parse(payload).RootElement.Clone());
        }
        return messages;
    }

    [Fact]
    public async Task ThrottledTopicSendsAtMostOncePerIntervalWithTheNewestValue()
    {
        _registry.Add(Topic("stats/cpu", TimeSpan.FromSeconds(1)));
        var connection = _hub.Connect(_user, "s");
        await _hub.SubscribeAsync(connection, "c1", "stats/cpu", TestContext.Current.CancellationToken);
        Drain(connection);

        _hub.Publish("stats/cpu", 1);
        _hub.Publish("stats/cpu", 2);
        _hub.Publish("stats/cpu", 3);
        Assert.Equal([1], Drain(connection).Select(m => m.GetProperty("data").GetInt32()));

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([3], Drain(connection).Select(m => m.GetProperty("data").GetInt32()));
    }

    [Fact]
    public async Task WildcardSubscriptionReceivesTopicsCreatedLater()
    {
        var pools = new List<string>();
        _registry.Add(Topic("storage/pools/+", current: () => pools));
        var connection = _hub.Connect(_user, "s");
        await _hub.SubscribeAsync(connection, "c1", "storage/pools/#", TestContext.Current.CancellationToken);
        Assert.Equal(["ack"], Drain(connection).Select(m => m.GetProperty("type").GetString()));

        pools.Add("storage/pools/tank");
        _hub.Publish("storage/pools/tank", 42);

        var message = Assert.Single(Drain(connection));
        Assert.Equal("event", message.GetProperty("type").GetString());
        Assert.Equal("storage/pools/tank", message.GetProperty("topic").GetString());
    }

    [Fact]
    public async Task EventsCheckPermissionsOnEveryPublish()
    {
        var allowed = true;
        _registry.Add(new TopicDefinition("admin/audit", typeof(int), typeof(int), (_, _) => ValueTask.FromResult<object>(0), (_, _) => allowed));
        var connection = _hub.Connect(_user, "s");
        await _hub.SubscribeAsync(connection, "c1", "admin/audit", TestContext.Current.CancellationToken);
        Drain(connection);

        allowed = false;
        _hub.Publish("admin/audit", 1);

        Assert.Empty(Drain(connection));
    }

    [Fact]
    public async Task SlowConsumerIsClosedInsteadOfBuffered()
    {
        _registry.Add(Topic("flood"));
        var connection = _hub.Connect(_user, "s");
        await _hub.SubscribeAsync(connection, "c1", "flood", TestContext.Current.CancellationToken);

        for (var i = 0; i <= TopicHub.OutboxCapacity; i++)
        {
            _hub.Publish("flood", i);
        }

        Assert.True(connection.Closing.IsCancellationRequested);
        Assert.Equal("slow consumer", connection.CloseReason);
    }

    [Fact]
    public async Task WaitingForSubscribersCompletesOnMatchingSubscription()
    {
        _registry.Add(Topic("system/info"));
        var waiting = _hub.WaitForSubscribersAsync("system/info", TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);

        await _hub.SubscribeAsync(_hub.Connect(_user, "s"), "c1", "system/#", TestContext.Current.CancellationToken);

        await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}
