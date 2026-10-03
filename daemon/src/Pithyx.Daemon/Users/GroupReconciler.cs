using System.Globalization;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.FileServices;

namespace Pithyx.Daemon.Users;

/// <summary>A Linux group as Pithyx wants it.</summary>
/// <param name="Name">The group name.</param>
/// <param name="Gid">The gid from the Pithyx range.</param>
/// <param name="Members">The user names of its members, each an existing account.</param>
public sealed record WantedGroup(string Name, long Gid, IReadOnlyList<string> Members);

/// <summary>One step that brings <c>/etc/group</c> in line with the database.</summary>
public abstract record GroupAction(string Name);

public sealed record CreateGroup(WantedGroup Group) : GroupAction(Group.Name);

/// <summary>The group exists with its gid under another name that nobody wants any more.</summary>
public sealed record RenameGroup(string OldName, WantedGroup Group) : GroupAction(Group.Name);

public sealed record SetGroupMembers(WantedGroup Group) : GroupAction(Group.Name);

/// <summary>Removes a group of the managed range that Pithyx does not have any more; files keep the gid.</summary>
public sealed record RemoveGroup(GroupEntry Entry) : GroupAction(Entry.Name);

/// <param name="Name">The group name.</param>
/// <param name="Reason">
/// <c>name_taken</c> (a group outside the Pithyx range has the name), <c>gid_taken</c> (another wanted group has the
/// gid), <c>gid_changed</c> (the group exists with another gid; gids never change, files depend on them) or
/// <c>gid_outside_range</c>.
/// </param>
public sealed record RefuseGroup(string Name, string Reason) : GroupAction(Name);

/// <summary>
/// The pure difference between the groups Pithyx wants and the ones <c>/etc/group</c> has. Only groups with a gid in
/// the managed range (30000 to 39999) are ever created, changed or removed; every other group is left alone, and a
/// wanted name that belongs to such a group is refused.
/// </summary>
public static class GroupReconciler
{
    public const long MinGid = 30000;
    public const long MaxGid = 39999;

    /// <summary>The system group <c>users</c>: the primary group of every Pithyx account and the built-in group of everybody.</summary>
    public const long UsersGid = 100;

    public static bool IsManaged(long gid) => gid is >= MinGid and <= MaxGid;

    public static IReadOnlyList<GroupAction> Plan(IReadOnlyList<WantedGroup> wanted, IReadOnlyList<GroupEntry> actual)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(actual);
        var byName = actual.GroupBy(entry => entry.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var byGid = actual.Where(entry => IsManaged(entry.Gid)).GroupBy(entry => entry.Gid).ToDictionary(group => group.Key, group => group.First());
        var wantedNames = wanted.Select(group => group.Name).ToHashSet(StringComparer.Ordinal);
        var wantedGids = wanted.Select(group => group.Gid).ToHashSet();
        var actions = new List<GroupAction>();
        // Removals first, so a name a stale group still holds is free for the group that should have it.
        actions.AddRange(actual
            .Where(entry => IsManaged(entry.Gid) && !wantedGids.Contains(entry.Gid) && !wantedNames.Contains(entry.Name))
            .Select(entry => new RemoveGroup(entry)));
        foreach (var group in wanted)
        {
            if (!IsManaged(group.Gid))
            {
                actions.Add(new RefuseGroup(group.Name, "gid_outside_range"));
            }
            else if (byName.TryGetValue(group.Name, out var existing))
            {
                if (!IsManaged(existing.Gid))
                {
                    actions.Add(new RefuseGroup(group.Name, "name_taken"));
                }
                else if (existing.Gid != group.Gid)
                {
                    actions.Add(new RefuseGroup(group.Name, "gid_changed"));
                }
                else if (!SameMembers(existing.Members, group.Members))
                {
                    actions.Add(new SetGroupMembers(group));
                }
            }
            else if (byGid.TryGetValue(group.Gid, out var holder))
            {
                if (wantedNames.Contains(holder.Name))
                {
                    actions.Add(new RefuseGroup(group.Name, "gid_taken"));
                    continue;
                }
                actions.Add(new RenameGroup(holder.Name, group));
                if (!SameMembers(holder.Members, group.Members))
                {
                    actions.Add(new SetGroupMembers(group));
                }
            }
            else
            {
                actions.Add(new CreateGroup(group));
            }
        }
        return actions;
    }

    private static bool SameMembers(IReadOnlyList<string> actual, IReadOnlyList<string> wanted) =>
        actual.ToHashSet(StringComparer.Ordinal).SetEquals(wanted);

    /// <summary>The commands of an action; refusals have none.</summary>
    public static IReadOnlyList<CommandRequest> Commands(GroupAction action) => action switch
    {
        CreateGroup { Group: var group } =>
        [
            new CommandRequest("groupadd", ["--gid", group.Gid.ToString(CultureInfo.InvariantCulture), group.Name]),
            .. group.Members.Count > 0 ? [Members(group)] : Array.Empty<CommandRequest>(),
        ],
        RenameGroup { OldName: var oldName, Group: var group } => [new CommandRequest("groupmod", ["--new-name", group.Name, oldName])],
        SetGroupMembers { Group: var group } => [Members(group)],
        RemoveGroup { Entry: var entry } => [new CommandRequest("groupdel", [entry.Name])],
        _ => [],
    };

    // An empty list removes every member.
    private static CommandRequest Members(WantedGroup group) =>
        new("gpasswd", ["-M", string.Join(',', group.Members.Order(StringComparer.Ordinal)), group.Name]);
}
