using System.Collections.Concurrent;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Storage;

/// <summary>
/// Plans that were shown to a user and may be run once, exactly as shown, until they expire. In memory: a restart
/// forgets them, and the user is shown the plan again.
/// </summary>
public sealed class CommandPlanStore(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, (long UserId, CommandPlan Plan)> _plans = new(StringComparer.Ordinal);

    public string Save(long userId, CommandPlan plan)
    {
        foreach (var expired in _plans.Where(entry => entry.Value.Plan.ExpiresAt < time.GetUtcNow()).Select(entry => entry.Key).ToList())
        {
            _plans.TryRemove(expired, out _);
        }
        var id = Guid.NewGuid().ToString("N");
        _plans[id] = (userId, plan);
        return id;
    }

    /// <summary>Hands out a plan once; another user's plan is as unknown as a missing one.</summary>
    public CommandPlan Take(long userId, string id)
    {
        if (!_plans.TryGetValue(id, out var entry) || entry.UserId != userId || !_plans.TryRemove(id, out _))
        {
            throw new PithyxException(ErrorCodes.StoragePlanNotFound);
        }
        return entry.Plan.ExpiresAt < time.GetUtcNow() ? throw new PithyxException(ErrorCodes.StoragePlanExpired) : entry.Plan;
    }
}
