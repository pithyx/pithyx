using Pithyx.Daemon.Apps.Docker;

namespace Pithyx.Daemon.Tests.Apps.Docker;

/// <summary>The lifecycle of a backend as a pure decision (A15, D327).</summary>
public sealed class BackendLifecycleTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _idle = TimeSpan.FromMinutes(10);

    private static LifecycleInput Input(
        bool always = false, bool runnable = true, bool running = false, bool inUse = false, bool held = false,
        TimeSpan? idleFor = null, TimeSpan? failedAgo = null) =>
        new(always, _idle, runnable, running, inUse, held, idleFor is { } idle ? _now - idle : null, failedAgo is { } failed ? _now - failed : null, _now);

    [Fact]
    public void AnOnDemandBackendStartsForAWindowOrARequestAndNotOtherwise()
    {
        Assert.Equal(new LifecycleDecision(LifecycleAction.Start, null), BackendLifecycle.Decide(Input(inUse: true)));
        Assert.Equal(new LifecycleDecision(LifecycleAction.Start, null), BackendLifecycle.Decide(Input(held: true)));
        Assert.Equal(new LifecycleDecision(LifecycleAction.None, null), BackendLifecycle.Decide(Input()));
    }

    [Fact]
    public void AnIdleBackendStopsOnlyAfterItsIdleTimeout()
    {
        Assert.Equal(new LifecycleDecision(LifecycleAction.None, _now + TimeSpan.FromMinutes(1)), BackendLifecycle.Decide(Input(running: true, idleFor: TimeSpan.FromMinutes(9))));
        Assert.Equal(new LifecycleDecision(LifecycleAction.Stop, null), BackendLifecycle.Decide(Input(running: true, idleFor: _idle)));
        Assert.Equal(new LifecycleDecision(LifecycleAction.None, null), BackendLifecycle.Decide(Input(running: true, inUse: true, idleFor: TimeSpan.FromHours(3))));
        // Without a known idle start the countdown begins now.
        Assert.Equal(new LifecycleDecision(LifecycleAction.None, _now + _idle), BackendLifecycle.Decide(Input(running: true)));
    }

    [Fact]
    public void AnAlwaysBackendRunsWheneverItCanAndNeverIdles()
    {
        Assert.Equal(LifecycleAction.Start, BackendLifecycle.Decide(Input(always: true)).Action);
        Assert.Equal(new LifecycleDecision(LifecycleAction.None, null), BackendLifecycle.Decide(Input(always: true, running: true, idleFor: TimeSpan.FromDays(2))));
    }

    [Fact]
    public void ABackendThatCannotRunStopsAndNeverStarts()
    {
        Assert.Equal(new LifecycleDecision(LifecycleAction.Stop, null), BackendLifecycle.Decide(Input(always: true, runnable: false, running: true)));
        Assert.Equal(new LifecycleDecision(LifecycleAction.None, null), BackendLifecycle.Decide(Input(runnable: false, inUse: true, held: true)));
    }

    [Fact]
    public void AFailedStartWaitsUnlessARequestAsks()
    {
        Assert.Equal(new LifecycleDecision(LifecycleAction.None, _now + TimeSpan.FromSeconds(30)), BackendLifecycle.Decide(Input(inUse: true, failedAgo: TimeSpan.FromSeconds(30))));
        Assert.Equal(LifecycleAction.Start, BackendLifecycle.Decide(Input(inUse: true, failedAgo: BackendLifecycle.RetryAfter)).Action);
        Assert.Equal(LifecycleAction.Start, BackendLifecycle.Decide(Input(held: true, failedAgo: TimeSpan.FromSeconds(5))).Action);
    }
}
