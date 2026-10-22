using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Monitoring;
using Pithyx.Daemon.Realtime;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Monitoring;

/// <summary>
/// The monitor samples only while an administrator is subscribed: nobody watching costs nothing, and a subscriber who
/// may not see the topics neither gets data nor makes the daemon sample.
/// </summary>
public sealed class ResourceMonitorTests : IAsyncDisposable
{
    private static readonly string _fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 1, 0, 0, TimeSpan.Zero));
    private readonly ResourceSampler _resources;
    private readonly ProcessSampler _processes;
    private readonly ResourceRing _ring = new();
    private readonly TopicHub _hub;
    private readonly ResourceMonitor _monitor;
    private readonly ClaimsPrincipal _admin = User("1", admin: true);
    private readonly ClaimsPrincipal _user = User("2", admin: false);

    public ResourceMonitorTests()
    {
        var files = new ResourceFiles(Path.Combine(_fixtures, "proc"), Path.Combine(_fixtures, "sys-handwritten"), Path.Combine(_fixtures, "etc", "passwd"));
        _resources = new ResourceSampler(files, _time);
        _processes = new ProcessSampler(files, _time);
        var registry = new TopicRegistry([ResourceMonitor.ResourcesDefinition(_resources, _ring), ResourceMonitor.ProcessesDefinition(_processes)]);
        _hub = new TopicHub(registry, _time, Options.Create(new JsonOptions()), NullLogger<TopicHub>.Instance);
        _monitor = new ResourceMonitor(_hub, _resources, _processes, _ring, _time);
    }

    public async ValueTask DisposeAsync()
    {
        await _monitor.StopAsync(CancellationToken.None);
        _monitor.Dispose();
    }

    private static ClaimsPrincipal User(string id, bool admin) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, id), .. admin ? [new Claim(ClaimTypes.Role, SessionAuthenticationHandler.AdminRole)] : Array.Empty<Claim>()],
        "test"));

    private static List<JsonElement> Drain(TopicHub.Connection connection)
    {
        var messages = new List<JsonElement>();
        while (connection.Outbox.Reader.TryRead(out var payload))
        {
            messages.Add(JsonDocument.Parse(payload).RootElement.Clone());
        }
        return messages;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "The condition never held.");
            await Task.Delay(10, Token);
        }
    }

    /// <summary>Moves the clock by whole seconds, giving the monitor's loops time to run after each tick.</summary>
    private async Task AdvanceAsync(int seconds)
    {
        for (var second = 0; second < seconds; second++)
        {
            _time.Advance(ResourceMonitor.Interval);
            await Task.Delay(30, Token);
        }
    }

    /// <summary>
    /// Moves the clock a second at a time until the condition holds. The sampler arms its timer only after the first
    /// sample, and a periodic timer folds ticks it is late for into one, so a fixed number of seconds can fall short.
    /// </summary>
    private async Task AdvanceUntilAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "The condition never held.");
            await AdvanceAsync(1);
        }
    }

    [Fact]
    public async Task NobodyWatchingCostsNothing()
    {
        await _monitor.StartAsync(Token);

        await AdvanceAsync(5);

        Assert.Equal(0, _resources.SampleCount);
        Assert.Equal(0, _processes.SampleCount);
        Assert.Null(_ring.Latest());
    }

    [Fact]
    public async Task AnAdministratorGetsTheSnapshotAndOneSamplePerSecondUntilUnsubscribing()
    {
        await _monitor.StartAsync(Token);
        var connection = _hub.Connect(_admin, "s");

        await _hub.SubscribeAsync(connection, "c1", ResourceMonitor.ResourcesTopic, Token);
        var snapshot = Drain(connection).Single(message => message.GetProperty("type").GetString() == "snap");
        Assert.Equal(4, snapshot.GetProperty("data").GetProperty("cpuCount").GetInt32());
        await WaitForAsync(() => _resources.SampleCount == 1);
        await AdvanceUntilAsync(() => _resources.SampleCount >= 4);
        var events = Drain(connection).Where(message => message.GetProperty("type").GetString() == "event").ToList();
        Assert.NotEmpty(events);
        Assert.All(events, message => Assert.Equal(ResourceMonitor.ResourcesTopic, message.GetProperty("topic").GetString()));
        Assert.NotNull(_ring.Latest());

        _hub.Unsubscribe(connection, "c2", ResourceMonitor.ResourcesTopic);
        var count = _resources.SampleCount;
        await AdvanceAsync(5);

        Assert.Equal(count, _resources.SampleCount);
        Assert.Equal(0, _processes.SampleCount);
    }

    [Fact]
    public async Task ProcessesAreReadOnlyWhileTheirTopicIsWatched()
    {
        await _monitor.StartAsync(Token);
        var connection = _hub.Connect(_admin, "s");

        await _hub.SubscribeAsync(connection, "c1", ResourceMonitor.ProcessesTopic, Token);
        await WaitForAsync(() => _processes.SampleCount >= 1);
        await AdvanceUntilAsync(() => _processes.SampleCount >= 3);
        _hub.Disconnect(connection);
        var count = _processes.SampleCount;
        await AdvanceAsync(3);

        Assert.Equal(count, _processes.SampleCount);
        Assert.Equal(0, _resources.SampleCount);
    }

    [Theory]
    [InlineData(ResourceMonitor.ResourcesTopic)]
    [InlineData(ResourceMonitor.ProcessesTopic)]
    [InlineData("system/#")]
    [InlineData("#")]
    public async Task AUserWhoIsNoAdministratorGetsNothingAndStartsNothing(string pattern)
    {
        await _monitor.StartAsync(Token);
        var connection = _hub.Connect(_user, "s");

        await _hub.SubscribeAsync(connection, "c1", pattern, Token);
        await AdvanceAsync(3);

        Assert.Equal(["ack"], Drain(connection).Select(message => message.GetProperty("type").GetString()));
        Assert.Equal(0, _resources.SampleCount);
        Assert.Equal(0, _processes.SampleCount);
    }
}
