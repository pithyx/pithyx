namespace Pithyx.Daemon.Users;

/// <summary>The homes as Settings shows them. Administrators only.</summary>
/// <param name="Pool">The home pool, or null before the first pool that can take homes.</param>
/// <param name="Pools">Every pool with whether it can take the homes.</param>
/// <param name="DefaultQuotaBytes">The quota of every home without its own, or null for none.</param>
/// <param name="Homes">Every home, those of deleted users included.</param>
/// <param name="Moving">The job that moves the homes to another pool while it runs, otherwise null.</param>
public sealed record HomeOverview(string? Pool, IReadOnlyList<HomePool> Pools, long? DefaultQuotaBytes, IReadOnlyList<HomeInfo> Homes, long? Moving);

/// <param name="Name">The pool.</param>
/// <param name="AvailableBytes">Free space for the homes, or null while it cannot be read.</param>
/// <param name="Unavailable">Why it cannot take the homes (<c>missing</c>, <c>offline</c>, <c>read_only</c>, <c>not_prepared</c>, <c>locked</c>, <c>not_mounted</c>), or null when it can.</param>
public sealed record HomePool(string Name, long? AvailableBytes, string? Unavailable);

/// <summary>A home.</summary>
/// <param name="ShareId">Its id as a share: Files opens it with this id.</param>
/// <param name="Folder">Its folder below <c>homes</c>: the user name, or <c>&lt;name&gt;.deleted-&lt;uid&gt;</c> after its user was deleted.</param>
/// <param name="UserId">Its user, or null when the user was deleted.</param>
/// <param name="Username">The user's name, or null.</param>
/// <param name="DisplayName">The user's display name, or null.</param>
/// <param name="Pool">The pool it is on.</param>
/// <param name="UsedBytes">Space it uses with its snapshots, or null while it cannot be read.</param>
/// <param name="QuotaBytes">Its quota as set on the pool, or null for none.</param>
/// <param name="OwnQuotaBytes">The user's own quota: null takes the default, 0 means none.</param>
/// <param name="Unavailable">Why its files cannot be reached now, or null when they can.</param>
public sealed record HomeInfo(
    long ShareId,
    string Folder,
    long? UserId,
    string? Username,
    string? DisplayName,
    string? Pool,
    long? UsedBytes,
    long? QuotaBytes,
    long? OwnQuotaBytes,
    string? Unavailable);

/// <param name="DefaultQuotaBytes">The quota of every home without its own: at least 1 MiB, or null for none.</param>
public sealed record HomeSettingsRequest(long? DefaultQuotaBytes);

/// <param name="QuotaBytes">The user's own quota: at least 1 MiB, 0 for none, null for the default.</param>
public sealed record HomeQuotaRequest(long? QuotaBytes);

/// <param name="Pool">The pool the homes move to; it needs the Pithyx layout and room for every home.</param>
public sealed record HomeMoveRequest(string Pool);

/// <param name="ShareId">The shared folder that gets a folder with the home's files.</param>
public sealed record HomeReleaseRequest(long ShareId);

/// <summary>What happens to a user's home when the user is deleted.</summary>
public enum HomeDataChoice
{
    /// <summary>The home stays, renamed to <c>&lt;name&gt;.deleted-&lt;uid&gt;</c>, for administrators below <c>homes</c>.</summary>
    Keep,

    /// <summary>Its files move into a folder named after the user in a shared folder, then the home is deleted.</summary>
    Move,

    /// <summary>The home is deleted with every file, after the user name was typed.</summary>
    Delete,
}

/// <summary>The result of deleting a user.</summary>
/// <param name="Job">The job that moves the home's files into the shared folder (choice <c>move</c>), otherwise null.</param>
public sealed record UserDeletion(Jobs.Job? Job);
