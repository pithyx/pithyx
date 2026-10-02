using Pithyx.Daemon.FileServices;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Tests.Support;
using Pithyx.Daemon.Users;

namespace Pithyx.Daemon.Tests.FileServices;

public sealed class FileServiceConfigTests
{
    private static string Fixture(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepositoryFiles.Root, "daemon", "tests", "fixtures", .. parts]));

    /// <summary>Compares with a golden file; <c>PITHYX_UPDATE_GOLDEN=1</c> writes it instead.</summary>
    private static void AssertGolden(string actual, params string[] parts)
    {
        var path = Path.Combine([RepositoryFiles.Root, "daemon", "tests", "fixtures", .. parts]);
        if (Environment.GetEnvironmentVariable("PITHYX_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(path, actual);
        }
        Assert.Equal(File.ReadAllText(path), actual);
    }

    [Fact]
    public void ParsesTheSambaAccountsWithoutTheirHashes()
    {
        var real = PassdbParser.Parse(Fixture("samba", "pdbedit-L-w"));
        Assert.Equal([new PassdbEntry("fx-sys", 1900, true, true), new PassdbEntry("fx-ada", 20900, true, false)], real);

        var handwritten = PassdbParser.Parse(Fixture("samba", "pdbedit-L-w-handwritten"));
        Assert.Equal([new PassdbEntry("nopass", 20003, false, false)], handwritten);
    }

    [Fact]
    public void ParsesGroupsWithTheirMembers()
    {
        var dev01 = GroupParser.Parse(Fixture("etc-group", "dev01"));
        Assert.Contains(dev01, group => group.Name == "users" && group.Gid == 100 && group.Members.SequenceEqual(["gabriel"]));

        var handwritten = GroupParser.Parse(Fixture("etc-group", "handwritten-pithyx"));
        Assert.Equal(["root", "users", "pithyx-users", "pithyx-sftp"], handwritten.Select(group => group.Name));
        Assert.Equal(["ada", "rita", "tom"], handwritten[2].Members);
        Assert.Empty(handwritten[3].Members);
    }

    [Fact]
    public void TellsAccountsWithAPasswordFromLockedOnes()
    {
        var withPassword = ShadowParser.WithPassword(Fixture("etc-passwd", "shadow-handwritten"));
        Assert.Equal(["ada"], withPassword.Order());
    }

    [Fact]
    public void RendersSmbConfWithEveryRuleOfTheShare()
    {
        var config = SmbConfig.Render("WORKGROUP",
        [
            new SmbShare("Media", "/tank/pithyx/shares/media", ["tom"], ["ada", "rita"], Encrypt: false),
            new SmbShare("Private Files", "/tank/pithyx/shares/private_files", [], [], Encrypt: true),
            new SmbShare("homes", "/tank/pithyx/shares/homes", [], ["ada"], Encrypt: false),
        ]);

        Assert.StartsWith(SmbConfig.Header, config, StringComparison.Ordinal);
        Assert.DoesNotContain("[homes]", config, StringComparison.Ordinal);
        AssertGolden(config, "samba", "smb.conf.golden");
    }

    [Theory]
    [InlineData("WORKGROUP", true)]
    [InlineData("home-lab.1", true)]
    [InlineData("ABCDEFGHIJKLMNOP", false)]
    [InlineData("", false)]
    [InlineData("-lead", false)]
    [InlineData("two words", false)]
    [InlineData("büro", false)]
    public void ChecksTheWorkgroup(string workgroup, bool valid) => Assert.Equal(valid, SmbConfig.IsValidWorkgroup(workgroup));

    [Fact]
    public void RendersTheSshdDropIn() => AssertGolden(SshdConfig.Render("/run/pithyx/sftp"), "samba", "sshd-pithyx.conf.golden");

    private static MountEntry Mount(string device, string target, string source = "tank/x") => new(device, target, "zfs", source);

    [Fact]
    public void MountsMissingViewsAndRemovesStaleOnes()
    {
        IReadOnlyList<MountEntry> mounts =
        [
            Mount("0:20", "/", "/dev/sda2"),
            Mount("0:42", "/tank/pithyx/shares/media"),
            Mount("0:43", "/tank/pithyx/shares/docs"),
            // Right: rita sees media.
            Mount("0:42", "/run/pithyx/sftp/rita/Media"),
            // Stale: tom lost his access.
            Mount("0:42", "/run/pithyx/sftp/tom/Media"),
            // Replaced: docs was destroyed and created again with another device.
            Mount("0:99", "/run/pithyx/sftp/rita/Docs"),
        ];
        var actions = SftpViews.Plan("/run/pithyx/sftp",
        [
            new SftpViewEntry("rita", "Media", "/tank/pithyx/shares/media"),
            new SftpViewEntry("rita", "Docs", "/tank/pithyx/shares/docs"),
            new SftpViewEntry("ada", "Media", "/tank/pithyx/shares/media"),
            // Not mounted: never bound, it would show the empty folder below.
            new SftpViewEntry("ada", "Gone", "/tank/pithyx/shares/gone"),
        ], mounts);

        Assert.Equal<SftpViewAction>(
        [
            new UnmountView("/run/pithyx/sftp/rita/Docs"),
            new UnmountView("/run/pithyx/sftp/tom/Media"),
            new MountView("/tank/pithyx/shares/media", "/run/pithyx/sftp/ada/Media"),
            new MountView("/tank/pithyx/shares/docs", "/run/pithyx/sftp/rita/Docs"),
        ], actions);
    }

    private static Dataset Share(string name) =>
        new($"tank/pithyx/shares/{name}", "tank", "filesystem", 1, 1, 1, $"/tank/pithyx/shares/{name}", true, "off", null, null, null, null, "lz4", 131072, null, null, 0);

    private static readonly PoolList _pools = new([new Pool("tank", "1", "ONLINE", 1, 0, 1, 0, 0, false, 0, null, [])], [Share("media"), Share("docs")]);

    [Fact]
    public void GivesEachUserTheSharesOfTheirRightsOverTheirProtocols()
    {
        var plan = FileServiceSync.Plan(
            smbEnabled: true,
            "WORKGROUP",
            sftpEnabled: true,
            [
                (20000, "ada", 20000, 1, null, 0, 0),
                (20001, "rita", 20001, 0, null, 1, 1),
                (20002, "tom", 20002, 0, null, 1, 0),
                (20003, "gone", 20003, 0, "2026-10-01", 1, 1),
                (20004, "nosmb", 20004, 0, null, 0, 1),
            ],
            [
                (1, "Media", "/tank/pithyx/shares/media", "tank/pithyx/shares/media", 1, 1, 0),
                (2, "Docs", "/tank/pithyx/shares/docs", "tank/pithyx/shares/docs", 0, 1, 1),
                (3, "Lost", "/lost/pithyx/shares/lost", "lost/pithyx/shares/lost", 1, 1, 0),
            ],
            new Dictionary<(long, long), string>
            {
                [(1, 20001)] = "readWrite",
                [(1, 20002)] = "read",
                [(1, 20003)] = "readWrite",
                [(1, 20004)] = "read",
                [(2, 20001)] = "read",
            },
            _pools,
            poolsAvailable: true);

        var media = Assert.Single(plan.SmbShares);
        Assert.Equal(new SmbShare("Media", "/tank/pithyx/shares/media", ["tom"], ["ada", "rita"], false), media with { Readers = [.. media.Readers], Writers = [.. media.Writers] }, new SmbShareComparer());
        Assert.Equal(["ada", "nosmb", "rita"], plan.SftpUsers.Order());
        Assert.Equal(
            ["ada/Docs", "ada/Media", "nosmb/Media", "rita/Docs", "rita/Media"],
            plan.SftpEntries.Select(entry => $"{entry.User}/{entry.Share}").Order());
        Assert.Contains(("gone", true), plan.Accounts);
        Assert.Equal([1L, 2L], plan.SharePaths.Keys.Order());

        var off = FileServiceSync.Plan(true, "WORKGROUP", sftpEnabled: false, [(20000, "ada", 20000, 1, null, 1, 1)], [(1, "Media", "/tank/pithyx/shares/media", "tank/pithyx/shares/media", 1, 1, 0)], new Dictionary<(long, long), string>(), _pools, true);
        Assert.Empty(off.SftpUsers);
        Assert.Empty(off.SftpEntries);
    }

    private sealed class SmbShareComparer : IEqualityComparer<SmbShare>
    {
        public bool Equals(SmbShare? x, SmbShare? y) =>
            x is not null && y is not null && x.Name == y.Name && x.Path == y.Path && x.Encrypt == y.Encrypt
            && x.Readers.Order().SequenceEqual(y.Readers.Order()) && x.Writers.Order().SequenceEqual(y.Writers.Order());

        public int GetHashCode(SmbShare obj) => obj.Name.GetHashCode(StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedAccountsAreTheOnlyOnesInThePlan()
    {
        var plan = FileServiceSync.Plan(true, "WORKGROUP", true, [(1, "owner", 1000, 1, null, 1, 1)], [], new Dictionary<(long, long), string>(), _pools, true);
        Assert.False(AccountReconciler.IsManaged(1000));
        Assert.Empty(plan.Accounts);
        Assert.Empty(plan.SftpUsers);
    }
}
