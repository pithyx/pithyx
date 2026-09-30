using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Files;
using Pithyx.Daemon.Storage;
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

            foreach (var action in AccountReconciler.Plan([wanted], before))
            {
                Assert.IsType<CreateAccount>(action);
                var result = await _commands.RunAsync(AccountReconciler.Command(action)!, Token);
                Assert.True(result.ExitCode == 0, result.StandardError);
            }
            Assert.Equal($"{name}:x:{Uid}:100:CI user:{AccountReconciler.NoHome}:{AccountReconciler.NoLogin}", (await RunAsync("getent", "passwd", name)).Trim());

            var changed = wanted with { Comment = "CI user renamed" };
            var update = Assert.IsType<UpdateAccount>(Assert.Single(AccountReconciler.Plan([changed], await PasswdAsync(name))));
            Assert.Equal(0, (await _commands.RunAsync(AccountReconciler.Command(update)!, Token)).ExitCode);
            Assert.Contains("CI user renamed", await RunAsync("getent", "passwd", name), StringComparison.Ordinal);

            var removals = AccountReconciler.Plan([], await PasswdAsync(name));
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

    [Fact]
    public async Task FilesAndTheHomeDatasetBelongToTheUser()
    {
        FileVdevs.RequireEnabled();
        await using var vdevs = FileVdevs.Create(1);
        await vdevs.CreateLayoutPoolAsync();
        var homes = Path.Combine(vdevs.AltRoot, vdevs.PoolName, "homes");
        var inventory = new PoolInventory();
        inventory.Replace(new PoolList(
            [new Pool(vdevs.PoolName, "1", "ONLINE", 1, 0, 1, 0, 0, false, 0, null, [])],
            [new Dataset($"{vdevs.PoolName}/pithyx/homes", vdevs.PoolName, "filesystem", 1, 1, 1, homes, true, "off", null, null, null, null, "lz4", 131072, null, null, 0)]));
        var database = Path.Combine(vdevs.Directory, "pithyx.db");
        var accounts = new LinuxAccounts(new SqliteConnectionFactory(database), _commands, inventory, new AccountOptions("/etc/passwd", Apply: true), NullLogger<LinuxAccounts>.Instance);

        var home = await accounts.CreateHomeAsync("rita", 29998, Token);

        Assert.Equal(Path.Combine(homes, "rita"), home);
        Assert.Contains($"{vdevs.PoolName}/pithyx/homes/rita", await RunAsync("zfs", "list", "-H", "-o", "name", "-r", vdevs.PoolName), StringComparison.Ordinal);
        Assert.Equal("29998:100 700", (await RunAsync("stat", "-c", "%u:%g %a", home!)).Trim());

        var file = Path.Combine(home!, "upload.txt");
        await using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write))
        {
            new FileOwner(29998, FileOwner.UsersGroup).Apply(stream.SafeFileHandle);
        }
        using (var folder = LinuxFs.OpenRoot(home!))
        {
            LinuxFs.MakeDirectory(folder, "new folder");
            new FileOwner(29998, FileOwner.UsersGroup).ApplyAt(folder, "new folder");
        }
        Assert.Equal("29998:100", (await RunAsync("stat", "-c", "%u:%g", file)).Trim());
        Assert.Equal("29998:100", (await RunAsync("stat", "-c", "%u:%g", Path.Combine(home!, "new folder"))).Trim());
    }
}
