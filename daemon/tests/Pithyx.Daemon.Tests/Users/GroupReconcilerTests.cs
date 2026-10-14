using Pithyx.Daemon.FileServices;
using Pithyx.Daemon.Tests.Support;
using Pithyx.Daemon.Users;

namespace Pithyx.Daemon.Tests.Users;

public sealed class GroupReconcilerTests
{
    private static IReadOnlyList<GroupEntry> Groups(string name) =>
        GroupParser.Parse(File.ReadAllText(Path.Combine(RepositoryFiles.Root, "daemon", "tests", "fixtures", "etc-group", name)));

    [Fact]
    public void CreatesRenamesChangesMembersAndRemovesOnlyGroupsOfTheManagedRange()
    {
        var actions = GroupReconciler.Plan(
            [
                new WantedGroup("engineering", 30000, ["ada"]),
                new WantedGroup("new-name", 30002, ["rita"]),
                new WantedGroup("media", 30003, []),
            ],
            Groups("handwritten-managed"));

        Assert.Equal(
            [
                new RemoveGroup(new GroupEntry("stale", 30001, ["tom"])),
                (GroupAction)new SetGroupMembers(new WantedGroup("engineering", 30000, ["ada"])),
                new RenameGroup("old-name", new WantedGroup("new-name", 30002, ["rita"])),
                new SetGroupMembers(new WantedGroup("new-name", 30002, ["rita"])),
                new CreateGroup(new WantedGroup("media", 30003, [])),
            ],
            actions,
            new ActionComparer());
        Assert.DoesNotContain(actions, action => action.Name is "root" or "users" or "staff" or "backup" or "pithyx-users");
    }

    [Fact]
    public void NothingHappensWhenEverythingIsInLine()
    {
        Assert.Empty(GroupReconciler.Plan([new WantedGroup("engineering", 30000, ["rita", "ada"]), new WantedGroup("stale", 30001, ["tom"]), new WantedGroup("old-name", 30002, [])], Groups("handwritten-managed")));
    }

    [Theory]
    [InlineData("backup", 30005, "name_taken")]
    [InlineData("users", 30005, "name_taken")]
    [InlineData("engineering", 30009, "gid_changed")]
    [InlineData("someone", 100, "gid_outside_range")]
    [InlineData("someone", 40000, "gid_outside_range")]
    [InlineData("someone", 29999, "gid_outside_range")]
    public void RefusesWhatWouldTouchAnotherGroup(string name, long gid, string reason)
    {
        var actions = GroupReconciler.Plan([new WantedGroup("engineering", 30000, ["ada", "rita"]), new WantedGroup(name, gid, [])], Groups("handwritten-managed"));

        Assert.Contains(new RefuseGroup(name, reason), actions);
    }

    [Fact]
    public void AGidTheWantedGroupOfAnotherNameHoldsIsRefused()
    {
        var actions = GroupReconciler.Plan([new WantedGroup("engineering", 30000, ["ada", "rita"]), new WantedGroup("other", 30000, [])], Groups("handwritten-managed"));

        Assert.Contains(new RefuseGroup("other", "gid_taken"), actions);
    }

    [Fact]
    public void CommandsUseFixedGidsAndSetEveryMemberAtOnce()
    {
        var create = GroupReconciler.Commands(new CreateGroup(new WantedGroup("media", 30003, ["tom", "ada"])));
        Assert.Equal("groupadd", create[0].FileName);
        Assert.Equal(["--gid", "30003", "media"], create[0].Arguments);
        Assert.Equal("gpasswd", create[1].FileName);
        Assert.Equal(["-M", "ada,tom", "media"], create[1].Arguments);
        Assert.Single(GroupReconciler.Commands(new CreateGroup(new WantedGroup("empty", 30004, []))));

        var empty = Assert.Single(GroupReconciler.Commands(new SetGroupMembers(new WantedGroup("media", 30003, []))));
        Assert.Equal(["-M", "", "media"], empty.Arguments);
        var rename = Assert.Single(GroupReconciler.Commands(new RenameGroup("old", new WantedGroup("new", 30003, []))));
        Assert.Equal("groupmod", rename.FileName);
        Assert.Equal(["--new-name", "new", "old"], rename.Arguments);
        var remove = Assert.Single(GroupReconciler.Commands(new RemoveGroup(new GroupEntry("stale", 30001, []))));
        Assert.Equal("groupdel", remove.FileName);
        Assert.Equal(["stale"], remove.Arguments);
        Assert.Empty(GroupReconciler.Commands(new RefuseGroup("x", "name_taken")));
    }

    // Records with lists compare the lists by reference.
    private sealed class ActionComparer : IEqualityComparer<GroupAction>
    {
        public bool Equals(GroupAction? x, GroupAction? y) => Describe(x) == Describe(y);

        public int GetHashCode(GroupAction obj) => Describe(obj).GetHashCode(StringComparison.Ordinal);

        private static string Describe(GroupAction? action) => action switch
        {
            CreateGroup { Group: var group } => $"create {Describe(group)}",
            RenameGroup { OldName: var old, Group: var group } => $"rename {old} {Describe(group)}",
            SetGroupMembers { Group: var group } => $"members {Describe(group)}",
            RemoveGroup { Entry: var entry } => $"remove {entry.Name} {entry.Gid}",
            RefuseGroup refusal => $"refuse {refusal.Name} {refusal.Reason}",
            _ => "",
        };

        private static string Describe(WantedGroup group) => $"{group.Name}:{group.Gid}:{string.Join(',', group.Members.Order(StringComparer.Ordinal))}";
    }

    [Fact]
    public void TheUsersWhoseGroupsChangeAreTheOnesAddedDroppedCreatedOrRemoved()
    {
        IReadOnlyList<GroupEntry> actual =
        [
            new("pithyx-s3-r", 30001, ["ada", "bob"]),
            new("pithyx-s3-rw", 30002, ["cleo"]),
            new("old", 30005, ["dan"]),
        ];
        var actions = GroupReconciler.Plan(
            [
                new WantedGroup("pithyx-s3-r", 30001, ["ada", "eve"]),
                new WantedGroup("pithyx-s3-rw", 30002, ["cleo"]),
                new WantedGroup("pithyx-s4-r", 30003, ["fay"]),
            ],
            actual);

        Assert.Equal(["bob", "dan", "eve", "fay"], GroupReconciler.ChangedMembers(actions, actual).Order(StringComparer.Ordinal));
    }
}
