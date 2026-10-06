using Pithyx.Daemon.Permissions;
using Pithyx.Daemon.Shares;

namespace Pithyx.Daemon.Tests.Permissions;

public sealed class PermissionResolverTests
{
    private const long Rita = 20001;
    private const long Tom = 20002;
    private const long Media = 30000;
    private const long Office = 30001;
    private const long Everybody = 100;

    private static Grant User(long id, string resource, ShareAccess level = ShareAccess.ReadWrite) => new(SubjectKind.User, id, resource, level);

    private static Grant Group(long id, string resource, ShareAccess level = ShareAccess.ReadWrite) => new(SubjectKind.Group, id, resource, level);

    private static Principal Member(long id, params long[] groups) => new(SubjectKind.User, id, [Everybody, .. groups]);

    public static TheoryData<string, Grant[], long[], ShareAccess> ShareCases => new()
    {
        { "nothing granted", [], [], ShareAccess.None },
        { "own read", [User(Rita, "share:1", ShareAccess.Read)], [], ShareAccess.Read },
        { "own read and a group's write", [User(Rita, "share:1", ShareAccess.Read), Group(Media, "share:1")], [Media], ShareAccess.ReadWrite },
        { "a group's read and own write", [Group(Media, "share:1", ShareAccess.Read), User(Rita, "share:1")], [Media], ShareAccess.ReadWrite },
        { "two groups, the higher wins", [Group(Media, "share:1", ShareAccess.Read), Group(Office, "share:1")], [Media, Office], ShareAccess.ReadWrite },
        { "a group the user is not in", [Group(Office, "share:1")], [Media], ShareAccess.None },
        { "another user's grant", [User(Tom, "share:1")], [], ShareAccess.None },
        { "another share", [User(Rita, "share:2")], [], ShareAccess.None },
        { "the built-in group", [Group(Everybody, "share:1", ShareAccess.Read)], [], ShareAccess.Read },
        { "administrator through a group", [Group(Office, Resources.Admin)], [Office], ShareAccess.ReadWrite },
    };

    [Theory]
    [MemberData(nameof(ShareCases))]
    public void TheAccessToAShareIsTheHighestGrantOfTheUserAndItsGroups(string name, Grant[] grants, long[] groups, ShareAccess expected)
    {
        _ = name;
        Assert.Equal(expected, PermissionResolver.Resolve(Member(Rita, groups), grants).Share(1));
    }

    [Fact]
    public void RightsProtocolsAndAppsComeFromTheUserAndItsGroups()
    {
        var rights = PermissionResolver.Resolve(Member(Rita, Media), [User(Rita, Resources.Smb), Group(Media, Resources.Sftp), Group(Office, Resources.Ftp), Group(Everybody, Resources.App("org.pithyx.files"))]);

        Assert.True(rights.Has(Resources.Smb));
        Assert.True(rights.Has(Resources.Sftp));
        Assert.True(rights.Has(Resources.App("org.pithyx.files")));
        Assert.False(rights.Has(Resources.Ftp));
        Assert.False(rights.Has(Resources.Admin));
        Assert.False(rights.IsAdmin);
    }

    [Fact]
    public void AdministratorsHaveEverythingAndDeactivatedUsersNothing()
    {
        Grant[] grants = [User(Rita, Resources.Admin)];
        var admin = PermissionResolver.Resolve(Member(Rita), grants);
        Assert.True(admin.IsAdmin);
        Assert.True(admin.Has(Resources.Power));
        Assert.Equal(ShareAccess.ReadWrite, admin.Share(4711));

        var off = PermissionResolver.Resolve(Member(Rita) with { Active = false }, [.. grants, User(Rita, Resources.DesktopLogin), User(Rita, "share:1")]);
        Assert.False(off.IsAdmin);
        Assert.False(off.Has(Resources.DesktopLogin));
        Assert.Equal(ShareAccess.None, off.Share(1));
    }

    [Fact]
    public void AnAppNeverBecomesAnAdministratorNorGetsGroupGrants()
    {
        var app = new Principal(SubjectKind.App, 40000, [Everybody]);
        var rights = PermissionResolver.Resolve(app, [new Grant(SubjectKind.App, 40000, Resources.Admin, ShareAccess.ReadWrite), new Grant(SubjectKind.App, 40000, "share:1", ShareAccess.Read), Group(Everybody, "share:2")]);

        Assert.False(rights.IsAdmin);
        Assert.Equal(ShareAccess.Read, rights.Share(1));
        Assert.Equal(ShareAccess.None, rights.Share(2));
        // A user with the app's id is someone else.
        Assert.Equal(ShareAccess.None, PermissionResolver.Resolve(Member(40000), [new Grant(SubjectKind.App, 40000, "share:1", ShareAccess.Read)]).Share(1));
    }

    private static readonly string[] _resources = [Resources.Admin, Resources.DesktopLogin, Resources.SshLogin, Resources.Power, Resources.Smb, Resources.Sftp, Resources.WebDav, Resources.Ftp, "app:org.pithyx.files", "share:1", "share:2", "share:3"];

    private static List<Grant> RandomGrants(Random random, int count) =>
    [
        .. Enumerable.Range(0, count).Select(_ =>
        {
            var resource = _resources[random.Next(_resources.Length)];
            var level = resource.StartsWith("share:", StringComparison.Ordinal) ? (random.Next(2) == 0 ? ShareAccess.Read : ShareAccess.ReadWrite) : ShareAccess.ReadWrite;
            return random.Next(3) switch
            {
                0 => User(random.Next(2) == 0 ? Rita : Tom, resource, level),
                1 => Group(random.Next(2) == 0 ? Media : Office, resource, level),
                _ => Group(Everybody, resource, level),
            };
        }),
    ];

    private static string Describe(EffectivePermissions rights) =>
        $"{rights.IsAdmin}|{string.Join(',', _resources.Where(resource => !resource.StartsWith("share:", StringComparison.Ordinal) && rights.Has(resource)))}|{string.Join(',', new long[] { 1, 2, 3 }.Select(rights.Share))}";

    /// <summary>
    /// Properties over many random grant sets (fixed seed, so a failure repeats): the result does not depend on the
    /// order; another grant never takes anything away; the user ends up with exactly the union of what its own grants
    /// and each of its groups' grants give alone; a group the user is not in changes nothing.
    /// </summary>
    [Fact]
    public void TheEffectiveRightsAreExactlyTheUnionOfTheGrants()
    {
        var random = new Random(290);
        for (var round = 0; round < 500; round++)
        {
            var grants = RandomGrants(random, random.Next(0, 12));
            var principal = random.Next(2) == 0 ? Member(Rita, Media) : Member(Rita, Media, Office);
            var rights = PermissionResolver.Resolve(principal, grants);

            var shuffled = grants.OrderBy(_ => random.Next()).ToList();
            Assert.Equal(Describe(rights), Describe(PermissionResolver.Resolve(principal, shuffled)));

            var more = PermissionResolver.Resolve(principal, [.. grants, .. RandomGrants(random, 1)]);
            Assert.True(!rights.IsAdmin || more.IsAdmin);
            Assert.All(_resources.Where(resource => !resource.StartsWith("share:", StringComparison.Ordinal) && rights.Has(resource)), resource => Assert.True(more.Has(resource)));
            Assert.All(new long[] { 1, 2, 3 }, share => Assert.True(more.Share(share) >= rights.Share(share)));

            var parts = new[] { (SubjectKind.User, Rita) }
                .Concat(principal.GroupIds.Select(group => (SubjectKind.Group, group)))
                .Select(subject => PermissionResolver.Resolve(principal, grants.Where(grant => grant.SubjectKind == subject.Item1 && grant.SubjectId == subject.Item2)))
                .ToList();
            Assert.Equal(parts.Any(part => part.IsAdmin), rights.IsAdmin);
            if (!rights.IsAdmin)
            {
                Assert.All(_resources.Where(resource => !resource.StartsWith("share:", StringComparison.Ordinal)), resource => Assert.Equal(parts.Any(part => part.Has(resource)), rights.Has(resource)));
                Assert.All(new long[] { 1, 2, 3 }, share => Assert.Equal(parts.Max(part => part.Share(share)), rights.Share(share)));
            }

            var outsider = PermissionResolver.Resolve(principal, [.. grants, Group(39999, Resources.Admin), Group(39999, "share:1")]);
            Assert.Equal(Describe(rights), Describe(outsider));
        }
    }
}
