using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.FileServices;
using Pithyx.Daemon.Permissions;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Tests.Storage.Integration;
using Pithyx.Daemon.Users;

namespace Pithyx.Daemon.Tests.Permissions;

/// <summary>
/// Step 11 on disk, as root on the dev VM: users A (read and write through a group), B (read) and C (nothing) get their
/// rights from the resolver, the share's two Linux groups and its ACL, and the kernel agrees when each of them reads
/// and writes as itself. Taking A out of the group takes its write access at its next login. Accounts and groups use
/// the top of the managed ranges and are removed again, whatever happens.
/// </summary>
[Trait("Category", FileVdevs.Category)]
public sealed class PermissionIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly ProcessCommandRunner _commands = new(NullLogger<ProcessCommandRunner>.Instance);

    private static async Task<CommandResult> RunAsync(string command, params string[] arguments) =>
        await _commands.RunAsync(new CommandRequest(command, arguments), Token);

    /// <summary>Runs a shell command as the account with its groups from /etc/group, like a fresh login.</summary>
    private static async Task<bool> AsAsync(string user, string script) =>
        (await RunAsync("setpriv", "--reuid", user, "--regid", "users", "--init-groups", "--", "sh", "-c", script)).ExitCode == 0;

    [Fact]
    public async Task TheKernelGivesEachUserExactlyTheAccessOfItsGrants()
    {
        FileVdevs.RequireEnabled();
        var suffix = RandomNumberGenerator.GetHexString(4, lowercase: true);
        string[] names = [$"pxa{suffix}", $"pxb{suffix}", $"pxc{suffix}"];
        long[] uids = [AccountReconciler.MaxUid - 2, AccountReconciler.MaxUid - 1, AccountReconciler.MaxUid];
        const long ReadGid = GroupReconciler.MaxGid - 1;
        const long WriteGid = GroupReconciler.MaxGid;
        const long ShareId = 1;
        const long Media = 30500;
        // In RAM and outside the run's own temporary folder, which only root may enter.
        var directory = Directory.CreateDirectory($"/dev/shm/pithyx-rights-{suffix}").FullName;
        var share = Path.Combine(directory, "media");
        try
        {
            // Passable for the test users, like a pool's mount point.
            Assert.Equal(0, (await RunAsync("chmod", "755", directory)).ExitCode);
            Directory.CreateDirectory(share);
            await File.WriteAllTextAsync(Path.Combine(share, "song.txt"), "la", Token);
            for (var index = 0; index < 3; index++)
            {
                Assert.Equal(0, (await RunAsync("useradd", "--uid", uids[index].ToString(CultureInfo.InvariantCulture), "--gid", "users", "--no-create-home", "--shell", AccountReconciler.NoLogin, names[index])).ExitCode);
            }

            Grant[] grants = [new(SubjectKind.Group, Media, Resources.Share(ShareId), ShareAccess.ReadWrite), new(SubjectKind.User, uids[1], Resources.Share(ShareId), ShareAccess.Read)];
            IReadOnlyList<UserPermissions> Users(bool aInGroup) =>
            [
                new(uids[0], names[0], uids[0], false, PermissionResolver.Resolve(PermissionService.Principal(uids[0], aInGroup ? [Media] : [], true), grants)),
                new(uids[1], names[1], uids[1], false, PermissionResolver.Resolve(PermissionService.Principal(uids[1], [], true), grants)),
                new(uids[2], names[2], uids[2], false, PermissionResolver.Resolve(PermissionService.Principal(uids[2], [], true), grants)),
            ];
            async Task ApplyGroupsAsync(bool aInGroup)
            {
                var actual = GroupParser.Parse(await File.ReadAllTextAsync("/etc/group", Token))
                    .Where(entry => !GroupReconciler.IsManaged(entry.Gid) || entry.Gid is ReadGid or WriteGid).ToList();
                var wanted = ShareGroups.Wanted([(ShareId, ReadGid, WriteGid)], Users(aInGroup), names.ToHashSet());
                foreach (var action in GroupReconciler.Plan(wanted, actual))
                {
                    foreach (var command in GroupReconciler.Commands(action))
                    {
                        Assert.Equal(0, (await _commands.RunAsync(command, Token)).ExitCode);
                    }
                }
            }

            await ApplyGroupsAsync(aInGroup: true);
            foreach (var command in ShareAcls.Commands(share, ShareAcls.Entries(ReadGid, WriteGid)))
            {
                Assert.Equal(0, (await _commands.RunAsync(command, Token)).ExitCode);
            }

            var song = Path.Combine(share, "song.txt");
            Assert.True(await AsAsync(names[0], $"cat '{song}' && echo a >> '{song}' && touch '{share}/a.txt'"));
            Assert.True(await AsAsync(names[1], $"cat '{song}'"));
            Assert.False(await AsAsync(names[1], $"echo b >> '{song}'"));
            Assert.False(await AsAsync(names[1], $"touch '{share}/b.txt'"));
            Assert.False(await AsAsync(names[2], $"cat '{song}'"));
            Assert.False(await AsAsync(names[2], $"ls '{share}'"));
            // What A created inherits the share's ACL, so B reads it too.
            Assert.True(await AsAsync(names[1], $"cat '{share}/a.txt'"));

            // Out of the group: only group members changed, no walk of the files, and A's next login has no access.
            await ApplyGroupsAsync(aInGroup: false);
            Assert.False(await AsAsync(names[0], $"cat '{song}'"));
            Assert.True(await AsAsync(names[1], $"cat '{song}'"));
        }
        finally
        {
            foreach (var name in names)
            {
                await _commands.RunAsync(new CommandRequest("userdel", [name]), CancellationToken.None);
            }
            await _commands.RunAsync(new CommandRequest("groupdel", [ShareGroups.ReadName(ShareId)]), CancellationToken.None);
            await _commands.RunAsync(new CommandRequest("groupdel", [ShareGroups.WriteName(ShareId)]), CancellationToken.None);
            Directory.Delete(directory, recursive: true);
        }
    }
}
