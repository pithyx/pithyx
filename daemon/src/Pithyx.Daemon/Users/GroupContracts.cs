namespace Pithyx.Daemon.Users;

/// <summary>A group as the Users page shows it. Administrators only.</summary>
/// <param name="Id">Stable id, and the Linux gid: 30000 to 39999 and never reused, 100 for the built-in group.</param>
/// <param name="Name">The Linux group name, lower case.</param>
/// <param name="Description">Free text, may be empty.</param>
/// <param name="Builtin">The built-in group <c>users</c>: every user is a member, and it can neither be renamed nor deleted.</param>
/// <param name="MemberIds">The ids of its members; every user for the built-in group.</param>
/// <param name="CreatedAt">When the group was created, UTC.</param>
public sealed record GroupSummary(long Id, string Name, string Description, bool Builtin, IReadOnlyList<long> MemberIds, DateTimeOffset CreatedAt);

/// <param name="Name">Lowercase letters, digits, underscores and hyphens, starting with a letter or underscore, at most 32 characters; not a system name.</param>
/// <param name="Description">Optional, at most 200 characters.</param>
/// <param name="MemberIds">The ids of the first members.</param>
public sealed record NewGroup(string Name, string? Description = null, IReadOnlyList<long>? MemberIds = null);

/// <summary>Fields left out stay as they are. The built-in group takes none of them.</summary>
/// <param name="Name">A new name; the gid stays.</param>
/// <param name="Description">An empty text removes the description.</param>
/// <param name="MemberIds">Every member the group should have; the others leave it.</param>
public sealed record GroupUpdate(string? Name = null, string? Description = null, IReadOnlyList<long>? MemberIds = null);
