using Pithyx.Daemon.FileServices;
using Pithyx.Daemon.Permissions;
using Pithyx.Daemon.Shares;
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
        var devbox = GroupParser.Parse(Fixture("etc-group", "devbox"));
        Assert.Contains(devbox, group => group.Name == "users" && group.Gid == 100 && group.Members.SequenceEqual(["alice"]));

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
    public void HandsTheHashesOfPithyxAccountsToTheBackupOnly()
    {
        var passdb = PassdbParser.Lines(Fixture("samba", "pdbedit-L-w"), AccountReconciler.IsManaged);
        Assert.Equal(["fx-ada"], passdb.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(':')[0]));
        Assert.Equal(PassdbParser.Parse(passdb), [new PassdbEntry("fx-ada", 20900, true, false)]);

        var hashes = ShadowParser.Hashes(Fixture("etc-passwd", "shadow-handwritten"), new HashSet<string>(["ada", "rita", "tom", "empty"]));
        Assert.Equal(["ada"], hashes.Keys);
        Assert.StartsWith("$y$", hashes["ada"], StringComparison.Ordinal);
    }

    [Fact]
    public void RendersSmbConfWithEveryRuleOfTheShare()
    {
        var config = SmbConfig.Render("WORKGROUP",
        [
            new SmbShare("Media", "/tank/pithyx/shares/media", ["tom"], ["ada", "rita"], Encrypt: false),
            new SmbShare("Private Files", "/tank/pithyx/shares/private_files", [], [], Encrypt: true),
            new SmbShare("homes", "/tank/pithyx/shares/homes", [], ["ada"], Encrypt: false),
            new SmbShare("homes", "/tank/homes", [], ["ada"], Encrypt: false, SmbShareKind.Homes),
            new SmbShare("home", "%H", [], ["ada", "rita"], Encrypt: false, SmbShareKind.Home),
        ], SmbConfig.NetbiosName("nas-in-the-basement"));

        Assert.StartsWith(SmbConfig.Header, config, StringComparison.Ordinal);
        Assert.Equal("NAS-IN-THE-BASE", SmbConfig.NetbiosName("nas-in-the-basement"));
        Assert.Equal("NAS-01", SmbConfig.NetbiosName("nas-01"));
        // A shared folder named like the homes is never offered; the homes are, home first.
        Assert.DoesNotContain("/tank/pithyx/shares/homes", config, StringComparison.Ordinal);
        Assert.True(config.IndexOf("[home]", StringComparison.Ordinal) < config.IndexOf("[homes]", StringComparison.Ordinal));
        var homes = config[config.IndexOf("[homes]", StringComparison.Ordinal)..config.IndexOf("[Media]", StringComparison.Ordinal)];
        Assert.DoesNotContain("recycle", homes, StringComparison.Ordinal);
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

    private static UserPermissions User(long id, string name, bool admin = false, bool disabled = false, string[]? allowed = null, Dictionary<long, ShareAccess>? shares = null) =>
        new(id, name, id, disabled, disabled
            ? EffectivePermissions.None
            : new EffectivePermissions(admin, shares ?? [], new HashSet<string>(allowed ?? [], StringComparer.Ordinal)));

    private static readonly IReadOnlyList<(long Id, string Name, string Path, string Dataset, long Smb, long Sftp, long Encrypt)> _shares =
    [
        (1, "Media", "/tank/pithyx/shares/media", "tank/pithyx/shares/media", 1, 1, 0),
        (2, "Docs", "/tank/pithyx/shares/docs", "tank/pithyx/shares/docs", 0, 1, 1),
        (3, "Lost", "/lost/pithyx/shares/lost", "lost/pithyx/shares/lost", 1, 1, 0),
    ];

    [Fact]
    public void GivesEachUserTheSharesOfTheirRightsOverTheirProtocols()
    {
        IReadOnlyList<UserPermissions> users =
        [
            User(20000, "ada", admin: true),
            User(20001, "rita", allowed: [Resources.Smb, Resources.Sftp], shares: new() { [1] = ShareAccess.ReadWrite, [2] = ShareAccess.Read }),
            User(20002, "tom", allowed: [Resources.Smb], shares: new() { [1] = ShareAccess.Read }),
            User(20003, "gone", disabled: true),
            User(20004, "nosmb", allowed: [Resources.Sftp], shares: new() { [1] = ShareAccess.Read }),
        ];
        var plan = FileServiceSync.Plan(smbEnabled: true, "WORKGROUP", sftpEnabled: true, sshEnabled: false, users, _shares, _pools, poolsAvailable: true);

        var media = Assert.Single(plan.SmbShares);
        Assert.Equal(new SmbShare("Media", "/tank/pithyx/shares/media", ["tom"], ["ada", "rita"], false), media with { Readers = [.. media.Readers], Writers = [.. media.Writers] }, new SmbShareComparer());
        Assert.Equal(["ada", "nosmb", "rita"], plan.SftpUsers.Order());
        Assert.Equal(
            ["ada/Docs", "ada/Media", "nosmb/Media", "rita/Docs", "rita/Media"],
            plan.SftpEntries.Select(entry => $"{entry.User}/{entry.Share}").Order());
        Assert.Contains(("gone", true), plan.Accounts);
        Assert.Equal([1L, 2L], plan.SharePaths.Keys.Order());
        Assert.Empty(plan.SshUsers);

        var off = FileServiceSync.Plan(true, "WORKGROUP", sftpEnabled: false, sshEnabled: false, [User(20000, "ada", admin: true)], _shares, _pools, true);
        Assert.Empty(off.SftpUsers);
        Assert.Empty(off.SftpEntries);
    }

    [Fact]
    public void EveryUserGetsTheOwnHomeAndAdministratorsEveryHome()
    {
        IReadOnlyList<UserPermissions> users =
        [
            User(20000, "ada", admin: true),
            User(20001, "rita", allowed: [Resources.Smb, Resources.Sftp]),
            User(20002, "tom", allowed: [Resources.Smb]),
            User(20003, "eve"),
        ];
        Dataset Home(string folder) =>
            new($"tank/pithyx/homes/{folder}", "tank", "filesystem", 1, 1, 1, $"/tank/homes/{folder}", true, "off", null, null, null, null, "lz4", 131072, null, null, 0);
        var pools = _pools with { Datasets = [.. _pools.Datasets, Home("ada"), Home("rita"), Home("tom"), Home("eve"), Home("old.deleted-20009")] };
        IReadOnlyList<HomeRow> homes =
        [
            new(10, "ada", "/tank/homes/ada", "tank/pithyx/homes/ada", 20000),
            new(11, "rita", "/tank/homes/rita", "tank/pithyx/homes/rita", 20001),
            new(12, "tom", "/tank/homes/tom", "tank/pithyx/homes/tom", 20002),
            new(13, "eve", "/tank/homes/eve", "tank/pithyx/homes/eve", 20003),
            new(14, "old.deleted-20009", "/tank/homes/old.deleted-20009", "tank/pithyx/homes/old.deleted-20009", null),
            // On a pool that is gone: offered nowhere.
            new(15, "lost", "/lost/homes/lost", "lost/pithyx/homes/lost", null),
        ];

        var plan = FileServiceSync.Plan(true, "WORKGROUP", sftpEnabled: true, sshEnabled: false, users, _shares, pools, true, homes, "/tank/homes");

        var home = Assert.Single(plan.SmbShares, share => share.Kind == SmbShareKind.Home);
        Assert.Equal(("home", "%H"), (home.Name, home.Path));
        Assert.Equal(["ada", "rita", "tom"], home.Writers.Order());
        var all = Assert.Single(plan.SmbShares, share => share.Kind == SmbShareKind.Homes);
        Assert.Equal(("homes", "/tank/homes", "ada"), (all.Name, all.Path, Assert.Single(all.Writers)));
        Assert.Equal(
            ["ada/Docs", "ada/Media", "ada/home", "ada/homes/eve", "ada/homes/old.deleted-20009", "ada/homes/rita", "ada/homes/tom", "rita/home"],
            plan.SftpEntries.Select(entry => $"{entry.User}/{entry.Share}").Order(StringComparer.Ordinal));
        Assert.Equal([1L, 2L, 10L, 11L, 12L, 13L, 14L], plan.SharePaths.Keys.Order());
    }

    [Fact]
    public void UsersWithAnSshLoginGetAShellInsteadOfAnSftpView()
    {
        IReadOnlyList<UserPermissions> users =
        [
            User(20001, "rita", allowed: [Resources.Sftp, Resources.SshLogin], shares: new() { [1] = ShareAccess.ReadWrite }),
            User(20002, "tom", allowed: [Resources.Sftp], shares: new() { [1] = ShareAccess.Read }),
            User(20003, "eve", allowed: [Resources.SshLogin]),
        ];

        var on = FileServiceSync.Plan(true, "WORKGROUP", sftpEnabled: true, sshEnabled: true, users, _shares, _pools, true);
        Assert.Equal(["eve", "rita"], on.SshUsers.Order());
        Assert.Equal(["tom"], on.SftpUsers);
        Assert.Equal(["tom/Media"], on.SftpEntries.Select(entry => $"{entry.User}/{entry.Share}"));

        // Without SSH logins the right does nothing, and SFTP users keep their views.
        var off = FileServiceSync.Plan(true, "WORKGROUP", sftpEnabled: true, sshEnabled: false, users, _shares, _pools, true);
        Assert.Empty(off.SshUsers);
        Assert.Equal(["rita", "tom"], off.SftpUsers.Order());
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
        var plan = FileServiceSync.Plan(true, "WORKGROUP", true, true, [new UserPermissions(1, "owner", 1000, false, new EffectivePermissions(true, new Dictionary<long, ShareAccess>(), new HashSet<string>()))], [], _pools, true);
        Assert.False(AccountReconciler.IsManaged(1000));
        Assert.Empty(plan.Accounts);
        Assert.Empty(plan.SftpUsers);
    }
}
