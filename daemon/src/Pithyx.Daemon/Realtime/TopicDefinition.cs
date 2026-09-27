using System.Security.Claims;

namespace Pithyx.Daemon.Realtime;

/// <summary>
/// A topic or a family of topics the daemon publishes. <see cref="Pattern"/> may contain <c>+</c> for families whose
/// members come and go (for example one topic per pool); <see cref="CurrentTopics"/> lists the members that exist now.
/// </summary>
/// <param name="Pattern">Topic name, or a pattern with <c>+</c> for a family.</param>
/// <param name="SnapshotType">Type of the snapshot data, exported to the TypeScript client.</param>
/// <param name="EventType">Type of the event data, exported to the TypeScript client.</param>
/// <param name="Snapshot">Returns the current state of a concrete topic.</param>
/// <param name="IsAllowed">Checked for every snapshot and every event, so permission changes apply immediately.</param>
/// <param name="MinInterval">At most one event per interval; newer values replace older unsent ones.</param>
/// <param name="CurrentTopics">Concrete topics of a family; defaults to the pattern itself for single topics.</param>
public sealed record TopicDefinition(
    string Pattern,
    Type SnapshotType,
    Type EventType,
    Func<string, CancellationToken, ValueTask<object>> Snapshot,
    Func<ClaimsPrincipal, string, bool> IsAllowed,
    TimeSpan? MinInterval = null,
    Func<IEnumerable<string>>? CurrentTopics = null)
{
    public IEnumerable<string> ExistingTopics() => CurrentTopics?.Invoke() ?? [Pattern];

    public static bool AnyUser(ClaimsPrincipal user, string topic) => user?.Identity?.IsAuthenticated == true;
}
