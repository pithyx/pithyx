using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Tests.Storage.Integration;
using Pithyx.Daemon.Users;

namespace Pithyx.Daemon.Tests.Users;

/// <summary>
/// Real <c>useradd</c>, <c>userdel</c>, <c>fchown</c> and a home dataset on a throwaway pool, as root in the CI storage
/// job (or on the dev VM with <c>PITHYX_STORAGE_INTEGRATION=1</c>). Accounts use the top of the managed range and are
/// removed again, whatever happens.
/// </summary>
[Trait("Category", FileVdevs.Category)]
public sealed class LinuxAccountIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly ProcessCommandRunner _commands = new(NullLogger<ProcessCommandRunner>.Instance);

    private static async Task<string> RunAsync(string command, params string[] arguments) =>
        (await _commands.RunAsync(new CommandRequest(command, arguments), Token)).StandardOutput;

    /// <summary>
    /// The passwd entries the test may plan against: everything outside the managed range and its own account. The dev
    /// VM has a real installation whose users are in the managed range too; a plan must never see them, or it would
    /// remove them.
    /// </summary>
    private static async Task<IReadOnlyList<PasswdEntry>> PasswdAsync(string name) =>
        [.. PasswdParser.Parse(await File.ReadAllTextAsync("/etc/passwd", Token)).Where(entry => !AccountReconciler.IsManaged(entry.Uid) || entry.Name == name)];

    private static async Task<IReadOnlySet<string>> ExpiringAsync() => ShadowParser.Expiring(await File.ReadAllTextAsync("/etc/shadow", Token));

    [Fact]
    public async Task AnAccountIsCreatedChangedAndRemovedOnlyInTheManagedRange()
    {
        FileVdevs.RequireEnabled();
        var name = $"pithyxci{RandomNumberGenerator.GetHexString(6, lowercase: true)}";
        const long Uid = AccountReconciler.MaxUid;
        try
        {
            Assert.DoesNotContain(PasswdParser.Parse(await File.ReadAllTextAsync("/etc/passwd", Token)), entry => entry.Uid == Uid);
            var before = await PasswdAsync(name);
            var wanted = new WantedAccount(name, Uid, "CI user", AccountReconciler.NoHome, AccountReconciler.NoLogin);

            foreach (var action in AccountReconciler.Plan([wanted], before, await ExpiringAsync()))
            {
                Assert.IsType<CreateAccount>(action);
                var result = await _commands.RunAsync(AccountReconciler.Command(action)!, Token);
                Assert.True(result.ExitCode == 0, result.StandardError);
            }
            Assert.Equal($"{name}:x:{Uid}:100:CI user:{AccountReconciler.NoHome}:{AccountReconciler.NoLogin}", (await RunAsync("getent", "passwd", name)).Trim());

            var changed = wanted with { Comment = "CI user renamed" };
            var update = Assert.IsType<UpdateAccount>(Assert.Single(AccountReconciler.Plan([changed], await PasswdAsync(name), await ExpiringAsync())));
            Assert.Equal(0, (await _commands.RunAsync(AccountReconciler.Command(update)!, Token)).ExitCode);
            Assert.Contains("CI user renamed", await RunAsync("getent", "passwd", name), StringComparison.Ordinal);

            // Deactivated: the account expires; activated again: the expiry goes.
            var expire = Assert.IsType<UpdateAccount>(Assert.Single(AccountReconciler.Plan([changed with { Expired = true }], await PasswdAsync(name), await ExpiringAsync())));
            Assert.Equal(0, (await _commands.RunAsync(AccountReconciler.Command(expire)!, Token)).ExitCode);
            Assert.Contains(name, await ExpiringAsync());
            Assert.Empty(AccountReconciler.Plan([changed with { Expired = true }], await PasswdAsync(name), await ExpiringAsync()));
            var revive = Assert.IsType<UpdateAccount>(Assert.Single(AccountReconciler.Plan([changed], await PasswdAsync(name), await ExpiringAsync())));
            Assert.Equal(0, (await _commands.RunAsync(AccountReconciler.Command(revive)!, Token)).ExitCode);
            Assert.DoesNotContain(name, await ExpiringAsync());

            var removals = AccountReconciler.Plan([], await PasswdAsync(name), await ExpiringAsync());
            // Nothing outside the managed range is ever planned, and the only managed account the plan sees is this one.
            var own = Assert.IsType<RemoveAccount>(Assert.Single(removals));
            Assert.Equal(name, own.Name);
            Assert.Equal(0, (await _commands.RunAsync(AccountReconciler.Command(own)!, Token)).ExitCode);
            Assert.Equal("", (await RunAsync("getent", "passwd", name)).Trim());
        }
        finally
        {
            await _commands.RunAsync(new CommandRequest("userdel", [name]), CancellationToken.None);
        }
    }
}
