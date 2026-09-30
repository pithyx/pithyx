using Pithyx.Daemon.Users;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Users;

public sealed class AccountReconcilerTests
{
    private static IReadOnlyList<PasswdEntry> Passwd(string name) =>
        PasswdParser.Parse(File.ReadAllText(Path.Combine(RepositoryFiles.Root, "daemon", "tests", "fixtures", "etc-passwd", name)));

    private static WantedAccount Wanted(string name, long uid, string comment = "", string home = AccountReconciler.NoHome) =>
        new(name, uid, comment, home, AccountReconciler.NoLogin);

    [Fact]
    public void ParsesRealAndHandwrittenPasswdFiles()
    {
        var dev01 = Passwd("dev01");
        Assert.Contains(dev01, entry => entry is { Name: "root", Uid: 0, Gid: 0, Home: "/root" });
        Assert.Contains(dev01, entry => entry is { Name: "nobody", Uid: 65534 });
        Assert.DoesNotContain(dev01, entry => AccountReconciler.IsManaged(entry.Uid));

        var managed = Passwd("handwritten-managed");
        Assert.Equal(["root", "daemon", "owner", "admin", "rita", "gone", "backup", "appuser"], managed.Select(entry => entry.Name));
        Assert.Equal(new PasswdEntry("admin", 20000, 100, "Ada Admin", "/tank/homes/admin", "/usr/sbin/nologin"), managed[3]);
    }

    [Fact]
    public void CreatesChangesAndRemovesOnlyAccountsOfTheManagedRange()
    {
        var actions = AccountReconciler.Plan(
            [
                Wanted("admin", 20000, "Ada Admin", "/tank/homes/admin"),
                Wanted("rita", 20001, "Rita Meyer"),
                Wanted("new", 20003),
            ],
            Passwd("handwritten-managed"));

        Assert.Equal(
            [
                new RemoveAccount(new PasswdEntry("gone", 20002, 100, "", "/nonexistent", "/usr/sbin/nologin")),
                (AccountAction)new CreateAccount(Wanted("new", 20003)),
            ],
            actions.Where(action => action is not UpdateAccount));
        var update = Assert.Single(actions.OfType<UpdateAccount>());
        Assert.Equal("rita", update.Name);
        Assert.Equal(["comment"], update.Changed);
        Assert.DoesNotContain(actions, action => action.Name is "root" or "daemon" or "owner" or "backup" or "appuser");
    }

    [Theory]
    [InlineData("backup", 20005, "name_taken")]
    [InlineData("owner", 20005, "name_taken")]
    [InlineData("admin", 20009, "uid_changed")]
    [InlineData("someone", 20000, "uid_taken")]
    [InlineData("someone", 1001, "uid_outside_range")]
    [InlineData("someone", 40001, "uid_outside_range")]
    public void RefusesWhatWouldTouchAnotherAccount(string name, long uid, string reason)
    {
        var actions = AccountReconciler.Plan([Wanted("admin", 20000, "Ada Admin", "/tank/homes/admin"), Wanted("rita", 20001, "Rita"), Wanted(name, uid)], Passwd("handwritten-managed"));

        Assert.Contains(new RefuseAccount(name, reason), actions);
    }

    [Fact]
    public void AUidAStaleAccountHoldsIsFreedFirst()
    {
        var actions = AccountReconciler.Plan([Wanted("admin", 20000, "Ada Admin", "/tank/homes/admin"), Wanted("rita", 20001, "Rita"), Wanted("newcomer", 20002)], Passwd("handwritten-managed"));

        Assert.Equal(["gone", "newcomer"], actions.Select(action => action.Name));
        Assert.IsType<RemoveAccount>(actions[0]);
        Assert.IsType<CreateAccount>(actions[1]);
    }

    [Fact]
    public void CommandsNeverRemoveHomesAndUseTheUsersGroup()
    {
        var create = AccountReconciler.Command(new CreateAccount(Wanted("rita", 20001, "Rita", "/tank/homes/rita")))!;
        Assert.Equal("useradd", create.FileName);
        Assert.Equal(["--uid", "20001", "--gid", "users", "--no-user-group", "--no-create-home", "--home-dir", "/tank/homes/rita", "--shell", "/usr/sbin/nologin", "--comment", "Rita", "rita"], create.Arguments);

        var remove = AccountReconciler.Command(new RemoveAccount(new PasswdEntry("gone", 20002, 100, "", "/x", "/y")))!;
        Assert.Equal(["gone"], remove.Arguments);
        Assert.Equal(["--home", "/tank/homes/rita", "rita"], AccountReconciler.Command(new UpdateAccount(Wanted("rita", 20001, home: "/tank/homes/rita"), ["home"]))!.Arguments);
        Assert.Null(AccountReconciler.Command(new RefuseAccount("x", "name_taken")));
    }

    [Theory]
    [InlineData("Ada: Admin", "Ada  Admin")]
    [InlineData("Müller, Jörg", "Müller  Jörg")]
    [InlineData("a\nb", "a b")]
    [InlineData(null, "")]
    public void DisplayNamesBecomeSafeComments(string? displayName, string comment) => Assert.Equal(comment, AccountReconciler.Comment(displayName));

    [Fact]
    public void LoginDefsThatReachIntoThePithyxRangesAreReported()
    {
        Assert.Equal(["UID_MIN..UID_MAX", "GID_MIN..GID_MAX"], LoginDefs.Overlaps("# Debian default\nUID_MIN\t\t\t 1000\nUID_MAX\t\t\t60000\nGID_MIN 1000\nGID_MAX 60000\n"));
        Assert.Empty(LoginDefs.Overlaps("UID_MIN 1000\nUID_MAX 19999\nGID_MIN 1000\nGID_MAX 19999\nSYS_UID_MAX 999\n"));
        Assert.Equal(["SYS_UID_MIN..SYS_UID_MAX"], LoginDefs.Overlaps("UID_MAX 19999\nGID_MAX 19999\nSYS_UID_MIN 100\nSYS_UID_MAX 25000\n"));
    }
}
