namespace Pithyx.Daemon.Network;

/// <summary>
/// The confirmation timer of a network change (D110): one change at a time; it waits <see cref="Window"/> for its
/// confirmation and is rolled back when the time runs out or when it is reverted. A pure state machine over a
/// <see cref="TimeProvider"/>, so the tests run it with a fake clock.
/// </summary>
public sealed class NetworkConfirmation(TimeProvider time) : IDisposable
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private readonly Lock _lock = new();
    private (Guid Id, DateTimeOffset Deadline, Func<string, Task> Rollback, ITimer Timer)? _pending;

    /// <summary>The change waiting for its confirmation, if any.</summary>
    public (Guid Id, DateTimeOffset Deadline)? Pending
    {
        get
        {
            lock (_lock)
            {
                return _pending is { } pending ? (pending.Id, pending.Deadline) : null;
            }
        }
    }

    /// <summary>Starts the timer of a change that was applied; false when another change waits already. The rollback gets <c>timeout</c> or <c>undo</c>.</summary>
    public bool TryStart(Guid id, Func<string, Task> rollback, DateTimeOffset? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(rollback);
        lock (_lock)
        {
            if (_pending is not null)
            {
                return false;
            }
            var until = deadline ?? time.GetUtcNow() + Window;
            var due = until - time.GetUtcNow();
            // Never zero: a timer due at once may fire before the change is recorded here.
            var timer = time.CreateTimer(_ => _ = ExpireAsync(id), null, due < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : due, Timeout.InfiniteTimeSpan);
            _pending = (id, until, rollback, timer);
            return true;
        }
    }

    /// <summary>Keeps the change; false when no change with this id waits.</summary>
    public bool Confirm(Guid id) => Take(id) is not null;

    /// <summary>Rolls the change back at once; false when no change with this id waits.</summary>
    public async Task<bool> RevertAsync(Guid id)
    {
        if (Take(id) is not { } rollback)
        {
            return false;
        }
        await rollback("undo");
        return true;
    }

    private async Task ExpireAsync(Guid id)
    {
        if (Take(id) is { } rollback)
        {
            await rollback("timeout");
        }
    }

    private Func<string, Task>? Take(Guid id)
    {
        lock (_lock)
        {
            if (_pending is not { } pending || pending.Id != id)
            {
                return null;
            }
            pending.Timer.Dispose();
            _pending = null;
            return pending.Rollback;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _pending?.Timer.Dispose();
            _pending = null;
        }
    }
}
