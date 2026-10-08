namespace Pithyx.Daemon.Apps.Docker;

/// <summary>What the supervisor does with an app's backend now.</summary>
public enum LifecycleAction
{
    None,
    Start,
    Stop,
}

/// <summary>Everything the lifecycle of one backend depends on at one moment.</summary>
/// <param name="Always">The manifest's <c>lifecycle</c> is <c>always</c>, not <c>onDemand</c>.</param>
/// <param name="IdleTimeout">How long an <c>onDemand</c> backend runs on after its last window closed.</param>
/// <param name="Runnable">The app is enabled, its pool reachable and its required folders mapped.</param>
/// <param name="Running">The backend runs (the proxy reaches it).</param>
/// <param name="InUse">A user holds a window lease on the app, the 2 minutes of grace after a lost connection included.</param>
/// <param name="Held">A request or a schedule wants the backend now.</param>
/// <param name="IdleSince">When the app was last in use or held, or null when it never was since the backend started.</param>
/// <param name="FailedAt">When the last start failed, or null.</param>
/// <param name="Now">The moment of the decision.</param>
public sealed record LifecycleInput(
    bool Always,
    TimeSpan IdleTimeout,
    bool Runnable,
    bool Running,
    bool InUse,
    bool Held,
    DateTimeOffset? IdleSince,
    DateTimeOffset? FailedAt,
    DateTimeOffset Now);

/// <summary>The decision and when to look again without any event (null: only on the next event).</summary>
public sealed record LifecycleDecision(LifecycleAction Action, DateTimeOffset? RecheckAt);

/// <summary>
/// The lifecycle of an app backend as a pure function (A15, D327). An app that cannot run (disabled, its pool locked or
/// gone, a required folder unmapped) stops. An <c>always</c> backend starts whenever it can. An <c>onDemand</c> backend
/// starts while a user has a window of the app open (the window lease) or a request or schedule holds it, and stops once
/// <see cref="LifecycleInput.IdleTimeout"/> has passed since the last of them. A failed start waits
/// <see cref="RetryAfter"/> before the next try on its own, so a broken backend does not restart in a loop; a request or
/// a newly opened window tries again at once.
/// </summary>
public static class BackendLifecycle
{
    /// <summary>The pause after a failed start before the supervisor tries again on its own.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    /// <summary>The manifest's default idle timeout.</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(10);

    public static LifecycleDecision Decide(LifecycleInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.Runnable)
        {
            return new(input.Running ? LifecycleAction.Stop : LifecycleAction.None, null);
        }
        var wanted = input.Always || input.InUse || input.Held;
        if (input.Running)
        {
            if (wanted)
            {
                return new(LifecycleAction.None, null);
            }
            var idleUntil = (input.IdleSince ?? input.Now) + input.IdleTimeout;
            return idleUntil <= input.Now ? new(LifecycleAction.Stop, null) : new(LifecycleAction.None, idleUntil);
        }
        if (!wanted)
        {
            return new(LifecycleAction.None, null);
        }
        // A request or a lease that came after the failure tries again; otherwise the backend waits out the pause.
        if (input.FailedAt is { } failed && !input.Held && failed + RetryAfter > input.Now)
        {
            return new(LifecycleAction.None, failed + RetryAfter);
        }
        return new(LifecycleAction.Start, null);
    }
}
