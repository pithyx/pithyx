using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.FileServices;
using Pithyx.Daemon.Tests.Storage.Integration;
using Pithyx.Daemon.Users;

namespace Pithyx.Daemon.Tests.Users;

/// <summary>
/// Real <c>groupadd</c>, <c>gpasswd</c>, <c>groupmod</c> and <c>groupdel</c> as root on the dev VM. The groups use the
/// top of the managed range and are removed again, whatever happens; the plan never sees the dev installation's
/// groups.
/// </summary>
[Trait("Category", FileVdevs.Category)]
public sealed class GroupIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly ProcessCommandRunner _commands = new(NullLogger<ProcessCommandRunner>.Instance);

    private static async Task<string> GetentAsync(string name) =>
        (await _commands.RunAsync(new CommandRequest("getent", ["group", name]), Token)).StandardOutput.Trim();

    /// <summary>Everything outside the managed range and the test's own groups, so a plan never removes the dev installation's.</summary>
    private static async Task<IReadOnlyList<GroupEntry>> GroupsAsync(params string[] own) =>
        [.. GroupParser.Parse(await File.ReadAllTextAsync("/etc/group", Token)).Where(entry => !GroupReconciler.IsManaged(entry.Gid) || own.Contains(entry.Name))];

    private static async Task ApplyAsync(IReadOnlyList<GroupAction> actions)
    {
        foreach (var action in actions)
        {
            Assert.IsNotType<RefuseGroup>(action);
            foreach (var command in GroupReconciler.Commands(action))
            {
                var result = await _commands.RunAsync(command, Token);
                Assert.True(result.ExitCode == 0, $"{command.FileName}: {result.StandardError}");
            }
        }
    }

    [Fact]
    public async Task AGroupIsCreatedRenamedGivenMembersAndRemovedOnlyInTheManagedRange()
    {
        FileVdevs.RequireEnabled();
        var suffix = RandomNumberGenerator.GetHexString(6, lowercase: true);
        var name = $"pithyxcig{suffix}";
        var renamed = $"pithyxcin{suffix}";
        var member = $"pithyxciu{suffix}";
        // The integration tests run in parallel; each has ids of its own at the top of the ranges.
        const long Gid = GroupReconciler.MaxGid;
        const long Uid = AccountReconciler.MaxUid - 1;
        try
        {
            Assert.DoesNotContain(GroupParser.Parse(await File.ReadAllTextAsync("/etc/group", Token)), entry => entry.Gid == Gid);
            Assert.Equal(0, (await _commands.RunAsync(new CommandRequest("useradd", ["--uid", Uid.ToString(System.Globalization.CultureInfo.InvariantCulture), "--gid", "users", "--no-create-home", "--shell", AccountReconciler.NoLogin, member]), Token)).ExitCode);

            await ApplyAsync(GroupReconciler.Plan([new WantedGroup(name, Gid, [member])], await GroupsAsync(name)));
            Assert.Equal($"{name}:x:{Gid}:{member}", await GetentAsync(name));
            Assert.Contains(name, (await _commands.RunAsync(new CommandRequest("id", ["-nG", member]), Token)).StandardOutput, StringComparison.Ordinal);

            await ApplyAsync(GroupReconciler.Plan([new WantedGroup(renamed, Gid, [])], await GroupsAsync(name, renamed)));
            Assert.Equal("", await GetentAsync(name));
            Assert.Equal($"{renamed}:x:{Gid}:", await GetentAsync(renamed));
            Assert.Empty(GroupReconciler.Plan([new WantedGroup(renamed, Gid, [])], await GroupsAsync(renamed)));

            // Nothing outside the managed range is ever planned, and the only managed group the plan sees is this one.
            var removal = Assert.IsType<RemoveGroup>(Assert.Single(GroupReconciler.Plan([], await GroupsAsync(renamed))));
            await ApplyAsync([removal]);
            Assert.Equal("", await GetentAsync(renamed));
        }
        finally
        {
            await _commands.RunAsync(new CommandRequest("groupdel", [name]), CancellationToken.None);
            await _commands.RunAsync(new CommandRequest("groupdel", [renamed]), CancellationToken.None);
            await _commands.RunAsync(new CommandRequest("userdel", [member]), CancellationToken.None);
        }
    }
}
