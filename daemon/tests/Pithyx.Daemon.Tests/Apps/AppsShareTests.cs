using Pithyx.Daemon.Apps;
using Pithyx.Daemon.FileServices;
using Pithyx.Daemon.Permissions;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Apps;

/// <summary>The decisions behind the special folder Apps (D409), as pure functions.</summary>
public sealed class AppsShareTests
{
    private const string Root = "/run/pithyx/apps";

    private static string Fixture(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepositoryFiles.Root, "daemon", "tests", "fixtures", .. parts]));

    private static readonly AppsFolder _chat = new("com.example.chat", "/tank/.pithyx/apps/com.example.chat/volumes", "tank/pithyx/apps/com.example.chat");

    private static MountEntry Dataset(string device, string name) => new(device, $"/tank/.pithyx/apps/{name["tank/pithyx/apps/".Length..]}", "zfs", name);

    [Fact]
    public void KeepsABindOfTheVolumesAndMountsWhatIsMissing()
    {
        var notes = new AppsFolder("com.example.notes", "/tank/.pithyx/apps/com.example.notes/volumes", "tank/pithyx/apps/com.example.notes");
        IReadOnlyList<MountEntry> mounts =
        [
            Dataset("0:51", _chat.Dataset!),
            Dataset("0:52", notes.Dataset!),
            new("0:60", Root, "tmpfs", "pithyx-apps"),
            new("0:51", $"{Root}/com.example.chat", "zfs", _chat.Dataset!, "/volumes"),
        ];

        Assert.Equal([new MountAppsFolder(notes.Source, $"{Root}/com.example.notes")], AppsShare.Plan(Root, [_chat, notes], mounts));
    }

    [Fact]
    public void UnmountsWhatShowsAnythingButTheAppsVolumes()
    {
        IReadOnlyList<MountEntry> mounts =
        [
            Dataset("0:51", _chat.Dataset!),
            new("0:60", Root, "tmpfs", "pithyx-apps"),
            // The whole dataset with its packages, not the volumes.
            new("0:51", $"{Root}/com.example.chat", "zfs", _chat.Dataset!, "/"),
            // A dataset that was replaced since: another device.
            new("0:49", $"{Root}/com.example.gone", "zfs", "tank/pithyx/apps/com.example.gone", "/volumes"),
        ];

        Assert.Equal(
            [new UnmountAppsFolder($"{Root}/com.example.chat"), new UnmountAppsFolder($"{Root}/com.example.gone"), new MountAppsFolder(_chat.Source, $"{Root}/com.example.chat")],
            AppsShare.Plan(Root, [_chat], mounts));
        Assert.Equal([new UnmountAppsFolder($"{Root}/com.example.chat")], AppsShare.Plan(Root, [], [new("0:51", $"{Root}/com.example.chat", "zfs", _chat.Dataset!, "/volumes")]));
    }

    [Fact]
    public void ReadsTheBindRootFromTheMountTable()
    {
        var mounts = MountTable.ParseMountInfo("""
            1 0 0:51 / /tank/.pithyx/apps/com.example.chat rw - zfs tank/pithyx/apps/com.example.chat rw
            2 1 0:51 /volumes /run/pithyx/apps/com.example.chat rw,nosuid,nodev,noexec,nosymfollow - zfs tank/pithyx/apps/com.example.chat rw
            """);

        Assert.Equal("/", mounts[0].Root);
        Assert.Equal("/volumes", mounts[1].Root);
        Assert.Empty(AppsShare.Plan(Root, [_chat], mounts));
    }

    [Fact]
    public void FollowsTheAppPoolForWhetherAppsCanBeUsed()
    {
        var pool = new Pool("tank", "1", "ONLINE", 1, 0, 1, 0, 0, false, 0, null, []);
        var apps = new Dataset("tank/pithyx/apps", "tank", "filesystem", 1, 1, 1, "/tank/.pithyx/apps", true, "off", null, null, null, null, "lz4", 131072, null, null, 0);
        var locked = apps with { Mounted = false, KeyStatus = "unavailable" };

        Assert.Null(AppsShare.Unavailable("tank", new PoolList([pool], [apps]), poolsRead: true, useZfs: true));
        Assert.Equal(ShareAvailability.Locked, AppsShare.Unavailable("tank", new PoolList([pool], [locked]), poolsRead: true, useZfs: true));
        Assert.Equal(ShareAvailability.PoolMissing, AppsShare.Unavailable("tank", new PoolList([], []), poolsRead: true, useZfs: true));
        Assert.Equal(ShareAvailability.PoolMissing, AppsShare.Unavailable(null, new PoolList([pool], [apps]), poolsRead: true, useZfs: true));
        Assert.Null(AppsShare.Unavailable("tank", new PoolList([pool], []), poolsRead: true, useZfs: false));
    }

    [Fact]
    public void CountsTheSmbConnectionsToApps()
    {
        Assert.Equal(0, AppsShare.CountConnections(Fixture("samba", "smbstatus-json")));
        Assert.Equal(2, AppsShare.CountConnections(Fixture("samba", "smbstatus-json-apps")));
        Assert.Equal(0, AppsShare.CountConnections("""{"sessions": {}, "tcons": {}}"""));
    }

    [Fact]
    public void AGrantWidensAppsWhenItIsNewOrRaisesReadToWrite()
    {
        IReadOnlyList<SubjectGrant> before = [new(SubjectKind.User, 1, GrantLevel.Read), new(SubjectKind.Group, 2, GrantLevel.ReadWrite)];

        Assert.Empty(AppsAccess.Widened(before, before));
        Assert.Empty(AppsAccess.Widened(before, [new(SubjectKind.Group, 2, GrantLevel.Read)]));
        Assert.Empty(AppsAccess.Widened(before, []));
        Assert.Equal([new SubjectGrant(SubjectKind.User, 1, GrantLevel.ReadWrite)], AppsAccess.Widened(before, [new(SubjectKind.User, 1, GrantLevel.ReadWrite)]));
        // A user and a group with the same id are different subjects.
        Assert.Equal([new SubjectGrant(SubjectKind.User, 2, GrantLevel.Read)], AppsAccess.Widened(before, [new(SubjectKind.User, 2, GrantLevel.Read), .. before]));
        Assert.Throws<Pithyx.Daemon.Errors.PithyxException>(() => AppsAccess.RequireConfirmation([new(SubjectKind.User, 3, GrantLevel.Read)], confirmed: false));
        AppsAccess.RequireConfirmation([new(SubjectKind.User, 3, GrantLevel.Read)], confirmed: true);
        AppsAccess.RequireConfirmation([], confirmed: false);
    }

    private static UserPermissions User(long id, string name, bool admin = false, bool smb = true, Dictionary<long, ShareAccess>? shares = null) =>
        new(id, name, id, false, new EffectivePermissions(admin, shares ?? [], new HashSet<string>(smb ? [Resources.Smb] : [], StringComparer.Ordinal)));

    private static FileServicePlan Plan(IReadOnlyList<UserPermissions> users, AppsSmbShare? apps) =>
        FileServiceSync.Plan(true, "WORKGROUP", false, false, users, [], new PoolList([], []), true, apps: apps);

    [Fact]
    public void OffersAppsOverSmbToTheUsersWithAccessAndTheSmbRight()
    {
        var apps = new AppsSmbShare(9, Root, Encrypt: false);
        IReadOnlyList<UserPermissions> users =
        [
            User(20000, "ada", admin: true),
            User(20001, "rita", shares: new() { [9] = ShareAccess.Read }),
            User(20002, "tom"),
            User(20003, "nosmb", smb: false, shares: new() { [9] = ShareAccess.ReadWrite }),
        ];

        var share = Assert.Single(Plan(users, apps).SmbShares);
        Assert.Equal((SmbShareKind.Apps, "Apps", Root), (share.Kind, share.Name, share.Path));
        Assert.Equal(["rita"], share.Readers);
        Assert.Equal(["ada"], share.Writers);
        Assert.Empty(Plan(users, null).SmbShares);
        Assert.Empty(Plan([User(20002, "tom")], apps).SmbShares);
    }

    [Fact]
    public void EndsTheSmbSessionsOfWhoLostAppsOrItsWriting()
    {
        var apps = new AppsSmbShare(9, Root, Encrypt: false);
        var before = Plan([User(20000, "ada", admin: true), User(20001, "rita", shares: new() { [9] = ShareAccess.ReadWrite }), User(20002, "tom", shares: new() { [9] = ShareAccess.Read })], apps);
        var after = Plan([User(20000, "ada", admin: true), User(20001, "rita", shares: new() { [9] = ShareAccess.Read }), User(20002, "tom")], apps);

        Assert.Equal(["rita", "tom"], FileServiceSync.AppsAccessLost(before, after).Order());
        Assert.Empty(FileServiceSync.AppsAccessLost(after, before));
        Assert.Empty(FileServiceSync.AppsAccessLost(null, after));
        Assert.Equal(["ada", "rita"], FileServiceSync.AppsAccessLost(after, Plan([], null)).Order());
    }

    [Fact]
    public void RendersAppsWithoutRecycleBinFollowingNoLinkAndGivingNewFilesToTheApp()
    {
        var config = SmbConfig.Render("WORKGROUP", [new SmbShare("Apps", Root, ["rita"], ["ada"], Encrypt: false, SmbShareKind.Apps)]);
        var global = config[..config.IndexOf("[Apps]", StringComparison.Ordinal)];
        var apps = config[config.IndexOf("[Apps]", StringComparison.Ordinal)..];

        Assert.Contains("nt acl support = no", global, StringComparison.Ordinal);
        Assert.Contains("smb1 unix extensions = no", global, StringComparison.Ordinal);
        Assert.Contains("smb3 unix extensions = no", global, StringComparison.Ordinal);
        Assert.Contains("admin users = \"ada\" \"rita\"", apps, StringComparison.Ordinal);
        Assert.Contains("write list = \"ada\"", apps, StringComparison.Ordinal);
        Assert.Contains("inherit owner = unix only", apps, StringComparison.Ordinal);
        Assert.Contains("follow symlinks = no", apps, StringComparison.Ordinal);
        Assert.Contains("wide links = no", apps, StringComparison.Ordinal);
        Assert.Contains("full_audit:success = connect disconnect", apps, StringComparison.Ordinal);
        Assert.DoesNotContain("recycle", apps, StringComparison.Ordinal);
        Assert.DoesNotContain("shadow_copy2", apps, StringComparison.Ordinal);
        // A shared folder named like Apps is never offered.
        Assert.DoesNotContain("/tank/apps", SmbConfig.Render("WORKGROUP", [new SmbShare("apps", "/tank/apps", [], ["ada"], Encrypt: false)]), StringComparison.Ordinal);
    }

    [Fact]
    public void TellsConnectsAndDisconnectsOfAppsApartFromWrites()
    {
        Assert.Equal(("ada", "Apps", "connect"), FileServiceActivity.Audit("ada|Apps|connect|ok|Apps")!.Value);
        Assert.Equal(("ada", "Apps", "disconnect"), FileServiceActivity.Audit("ada|Apps|disconnect|ok|")!.Value);
        Assert.Null(FileServiceActivity.Audit("ada|Apps|connect|fail|Apps"));
    }
}
