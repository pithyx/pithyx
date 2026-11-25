using Pithyx.Daemon.Backups;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Tests.Backups;

public sealed class BackupTargetRulesTests
{
    private const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl";

    [Theory]
    [InlineData("U123.Your-StorageBox.de", "u123.your-storagebox.de")]
    [InlineData("192.168.1.20", "192.168.1.20")]
    [InlineData("fd00::1", "fd00::1")]
    [InlineData("[FD00::1]", "fd00::1")]
    public void HostsAreNamesOrAddresses(string input, string expected) => Assert.Equal(expected, BackupTargetRules.CheckHost(input));

    [Theory]
    [InlineData("")]
    [InlineData("-oProxyCommand=x")]
    [InlineData("host name")]
    [InlineData("300.1.1.1")]
    [InlineData("fe80::1%eth0")]
    [InlineData("user@host")]
    public void AnythingElseIsNoHost(string input) => AssertInvalid("host", () => BackupTargetRules.CheckHost(input));

    [Theory]
    [InlineData("", "")]
    [InlineData("/", "/")]
    [InlineData("backups/", "backups")]
    [InlineData(" /srv/backup ", "/srv/backup")]
    [InlineData("Box one/2026", "Box one/2026")]
    public void PathsAreRelativeOrAbsolute(string input, string expected) => Assert.Equal(expected, BackupTargetRules.CheckPath(input));

    [Theory]
    [InlineData("a/../b")]
    [InlineData("a//b")]
    [InlineData("./a")]
    [InlineData("a\"b")]
    [InlineData("a\\b")]
    [InlineData("a\nb")]
    public void PathsCannotEscapeOrBreakQuotes(string input) => AssertInvalid("path", () => BackupTargetRules.CheckPath(input));

    [Fact]
    public void AncestryListsEveryFolderFromTheOutermost()
    {
        Assert.Equal(["a", "a/b", "a/b/c"], BackupTargetRules.Ancestry("a/b/c"));
        Assert.Equal(["/srv", "/srv/x"], BackupTargetRules.Ancestry("/srv/x"));
        Assert.Empty(BackupTargetRules.Ancestry(""));
        Assert.Empty(BackupTargetRules.Ancestry("/"));
    }

    [Fact]
    public void ARequestIsCheckedFieldByField()
    {
        var target = BackupTargetRules.Check(new BackupTargetRequest(" Storage box ", "u1.example.de", "u1-sub1", null, "pithyx", "u1.example.de " + Key));

        Assert.Equal(new CheckedTarget("Storage box", "u1.example.de", 22, "u1-sub1", "pithyx", SshHostKeys.Parse(Key), BackupTargetPurpose.Target), target);
        AssertInvalid("name", () => BackupTargetRules.Check(new BackupTargetRequest(" ", "h", "u", 22, "", null)));
        AssertInvalid("port", () => BackupTargetRules.Check(new BackupTargetRequest("n", "h", "u", 0, "", null)));
        AssertInvalid("port", () => BackupTargetRules.Check(new BackupTargetRequest("n", "h", "u", 65536, "", null)));
        AssertInvalid("user", () => BackupTargetRules.Check(new BackupTargetRequest("n", "h", "-l", 22, "", null)));
        AssertInvalid("user", () => BackupTargetRules.Check(new BackupTargetRequest("n", "h", "a b", 22, "", null)));
        AssertInvalid("hostKey", () => BackupTargetRules.Check(new BackupTargetRequest("n", "h", "u", 22, "", "ssh-ed25519 nope")));
    }

    [Fact]
    public void AServerPathIsAbsoluteWithoutSpacesAndDefaultsToTheRoot()
    {
        BackupTargetRequest Server(string? path) => new("Web", "web01.lan", "backup", null, path, null, BackupTargetPurpose.Server);

        Assert.Equal("/", BackupTargetRules.Check(Server(null)).Path);
        Assert.Equal("/", BackupTargetRules.Check(Server(" ")).Path);
        Assert.Equal("/srv/www", BackupTargetRules.Check(Server("/srv/www")).Path);
        Assert.Equal(BackupTargetPurpose.Server, BackupTargetRules.Check(Server("/srv")).Purpose);
        AssertInvalid("path", () => BackupTargetRules.Check(Server("srv")));
        AssertInvalid("path", () => BackupTargetRules.Check(Server("/srv/my files")));
        AssertInvalid("path", () => BackupTargetRules.Check(Server("/srv/../etc")));
    }

    [Fact]
    public void AServersKeyMayOnlyRunReadOnlyRrsyncBelowItsPath()
    {
        Assert.Equal("restrict,command=\"rrsync -ro /srv\" ssh-ed25519 AAAA box", BackupTargetRules.AuthorizedKeys("/srv", "ssh-ed25519 AAAA box"));
    }

    private static void AssertInvalid(string field, Action check)
    {
        var error = Assert.Throws<PithyxException>(check);
        Assert.Equal(ErrorCodes.BackupTargetInvalid, error.Code);
        Assert.Equal(field, error.Parameters["field"]);
    }
}
