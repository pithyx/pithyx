using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Encryption;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Tests.Storage.Integration;

/// <summary>
/// Encrypted pools (D384) against real OpenZFS on a file vdev, with the exact commands of the plan and of the unlock:
/// the key goes on stdin, a re-imported pool is locked, only its key opens it, the unlock mounts what an import would,
/// and a changed key replaces the old one. With <c>PITHYX_RECORD_DIR</c> set, the locked and the unlocked pool are
/// written as the fixtures <c>zfs-encrypted-locked</c> and <c>zfs-encrypted</c>.
/// </summary>
[Trait("Category", FileVdevs.Category)]
public sealed class EncryptionIntegrationTests
{
    private const string Alias = "pithyxteste";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnEncryptedPoolOpensOnlyWithItsKeyAndMountsWhatAnImportWould()
    {
        FileVdevs.RequireEnabled();
        await using var vdevs = FileVdevs.Create(1);
        // Imported again below /mnt, where a pool may mount (the test folder is below /tmp, where nothing may).
        var altRoot = $"/mnt/{vdevs.PoolName}";
        try
        {
            await RunAsync(vdevs, altRoot);
        }
        finally
        {
            if (Directory.Exists(altRoot))
            {
                Directory.Delete(altRoot, recursive: true);
            }
        }
    }

    private static async Task RunAsync(FileVdevs vdevs, string altRoot)
    {
        var key = PoolKey.ToHex(PoolKey.New());
        var root = EncryptionCommands.Root(vdevs.PoolName);
        await vdevs.CreateLayoutPoolAsync(key);
        await vdevs.RunAsync("zfs", ["create", $"{root}/homes/alice"]);
        await File.WriteAllTextAsync(Path.Combine(vdevs.AltRoot, vdevs.PoolName, "homes", "alice", "note.txt"), "kept", Token);

        var created = await ReadAsync(vdevs);
        var pool = created.Pools.Single(candidate => candidate.Name == vdevs.PoolName);
        Assert.Equal(new PoolEncryption(Locked: false, KeyInVault: false), pool.Encryption);
        Assert.All(created.Datasets.Where(dataset => dataset.Name.StartsWith(root, StringComparison.Ordinal)), dataset => Assert.Equal(("aes-256-gcm", root), (dataset.Encryption, dataset.EncryptionRoot)));
        Assert.Equal("off", created.Datasets.Single(dataset => dataset.Name == vdevs.PoolName).Encryption);

        await vdevs.RunAsync("zpool", ["export", vdevs.PoolName]);
        await vdevs.RunAsync("zpool", ["import", "-N", "-R", altRoot, "-d", vdevs.Directory, pool.PoolGuid]);
        await RecordAsync(vdevs, "zfs-encrypted-locked");
        var locked = await ReadAsync(vdevs);
        Assert.Equal(new PoolEncryption(Locked: true, KeyInVault: false), locked.Pools.Single(candidate => candidate.Name == vdevs.PoolName).Encryption);
        var note = Path.Combine(altRoot, vdevs.PoolName, "homes", "alice", "note.txt");
        Assert.False(File.Exists(note));
        // Something landed in an empty mount point while the pool was locked: the unlock must still mount over it.
        Directory.CreateDirectory(Path.Combine(altRoot, vdevs.PoolName, "homes", "alice"));
        await File.WriteAllTextAsync(Path.Combine(altRoot, vdevs.PoolName, "homes", "alice", "stray.txt"), "on the parent", Token);

        Assert.True(EncryptionCommands.IsWrongKey(await vdevs.Commands.RunAsync(EncryptionCommands.LoadKey(root, PoolKey.ToHex(PoolKey.New())), Token)));
        Assert.Equal(0, (await vdevs.Commands.RunAsync(EncryptionCommands.LoadKey(root, key), Token)).ExitCode);
        var properties = ZfsDatasetParser.ParseProperties(await vdevs.RunAsync("zfs", ImportPlans.MountProperties(vdevs.PoolName).Arguments));
        var mounts = ImportPlans.DecideMounts(properties, MountTable.ParseMountInfo(await File.ReadAllTextAsync("/proc/self/mountinfo", Token)));
        Assert.Equal([$"{root}/cache", $"{root}/system", $"{root}/homes", $"{root}/homes/alice"], mounts.Mount);
        foreach (var dataset in mounts.Mount)
        {
            await vdevs.RunAsync("zfs", EncryptionCommands.Mount(dataset).Arguments);
        }
        Assert.Equal("kept", await File.ReadAllTextAsync(note, Token));
        await RecordAsync(vdevs, "zfs-encrypted");

        var newKey = PoolKey.ToHex(PoolKey.New());
        Assert.Equal(0, (await vdevs.Commands.RunAsync(EncryptionCommands.ChangeKey(root, newKey), Token)).ExitCode);
        foreach (var dataset in mounts.Mount.Reverse())
        {
            await vdevs.RunAsync("zfs", ["unmount", "-f", dataset]);
        }
        await vdevs.RunAsync("zfs", ["unload-key", root]);
        Assert.True(EncryptionCommands.IsWrongKey(await vdevs.Commands.RunAsync(EncryptionCommands.LoadKey(root, key), Token)));
        Assert.Equal(0, (await vdevs.Commands.RunAsync(EncryptionCommands.LoadKey(root, newKey), Token)).ExitCode);
    }

    [Fact]
    public async Task TheServiceUnlocksFromTheVaultAndADamagedVaultKeyLeavesThePoolLockedAndUntouched()
    {
        FileVdevs.RequireEnabled();
        await using var vdevs = FileVdevs.Create(1);
        var altRoot = $"/mnt/{vdevs.PoolName}";
        var vaultFolder = Path.Combine(vdevs.Directory, "pool-keys");
        try
        {
            var key = PoolKey.ToHex(PoolKey.New());
            await vdevs.CreateLayoutPoolAsync(key);
            await vdevs.RunAsync("zfs", ["create", $"{EncryptionCommands.Root(vdevs.PoolName)}/homes/alice"]);
            var guid = (await ReadAsync(vdevs)).Pools.Single(pool => pool.Name == vdevs.PoolName).PoolGuid;
            await vdevs.RunAsync("zpool", ["export", vdevs.PoolName]);
            await vdevs.RunAsync("zpool", ["import", "-N", "-R", altRoot, "-d", vdevs.Directory, guid]);

            var vault = new KeyVault(vaultFolder);
            var disks = new DiskInventory();
            var pools = new PoolInventory();
            var facts = new StorageFacts(disks, pools, DeploymentEnvironment.Prod, SystemFiles.Host);
            var hub = new TopicHub(new TopicRegistry([disks.Definition(), pools.Definition()]), TimeProvider.System, Options.Create(new JsonOptions()), NullLogger<TopicHub>.Instance);
            using var state = new SystemStateService(vdevs.Commands, disks, pools, new ZfsBackend(vdevs.Commands), new DiskProbe(vdevs.Commands), facts, hub, TimeProvider.System, NullLogger<SystemStateService>.Instance, vault: vault);
            var drive = new UsbKeyDrive(vdevs.Commands, Path.Combine(vdevs.Directory, "usb-key"), NullLogger<UsbKeyDrive>.Instance);
            using var encryption = new PoolEncryptionService(vdevs.Commands, pools, disks, state, facts, vault, drive, TimeProvider.System, NullLogger<PoolEncryptionService>.Instance);
            Pool Pool() => pools.Current.Pools.Single(pool => pool.Name == vdevs.PoolName);

            // A damaged vault file: nothing is loaded or changed, and the pool says why it waits.
            Directory.CreateDirectory(vaultFolder);
            await File.WriteAllTextAsync(Path.Combine(vaultFolder, $"{guid}.key"), key[..40] + "\n", Token);
            await state.RefreshPoolsAsync(Token);
            await encryption.UnlockFromVaultAsync(Token);
            Assert.Equal(new PoolEncryption(Locked: true, KeyInVault: true, VaultFailed: true), Pool().Encryption);

            // The key typed by hand repairs the vault, and the next import unlocks by itself.
            await encryption.UnlockAsync(vdevs.PoolName, PoolKey.FromHex(key), keepInVault: true, Token);
            Assert.Equal(new PoolEncryption(Locked: false, KeyInVault: true), Pool().Encryption);
            Assert.All(
                pools.Current.Datasets.Where(dataset => dataset.Name is var name && (name.EndsWith("/homes", StringComparison.Ordinal) || name.EndsWith("/alice", StringComparison.Ordinal)) && dataset.Pool == vdevs.PoolName),
                dataset => Assert.True(dataset.Mounted, dataset.Name));

            await vdevs.RunAsync("zpool", ["export", vdevs.PoolName]);
            await vdevs.RunAsync("zpool", ["import", "-N", "-R", altRoot, "-d", vdevs.Directory, guid]);
            await state.RefreshPoolsAsync(Token);
            Assert.True(Pool().Encryption!.Locked);
            await encryption.UnlockFromVaultAsync(Token);
            Assert.Equal(new PoolEncryption(Locked: false, KeyInVault: true), Pool().Encryption);
        }
        finally
        {
            await vdevs.Commands.RunAsync(new Pithyx.Daemon.Commands.CommandRequest("zpool", ["export", vdevs.PoolName]), CancellationToken.None);
            if (Directory.Exists(altRoot))
            {
                Directory.Delete(altRoot, recursive: true);
            }
        }
    }

    private static async Task<PoolList> ReadAsync(FileVdevs vdevs) => EncryptionCommands.Apply(await new ZfsBackend(vdevs.Commands).ReadAsync(Token), _ => default);

    private static async Task RecordAsync(FileVdevs vdevs, string scenario)
    {
        await vdevs.RecordAsync(scenario, "zpool-status", Alias, "zpool", ZfsBackend.Status.Arguments);
        await vdevs.RecordAsync(scenario, "zpool-list", Alias, "zpool", ZfsBackend.List.Arguments);
        await vdevs.RecordAsync(scenario, "zfs-list", Alias, "zfs", ZfsBackend.Datasets.Arguments);
        await vdevs.RecordAsync(scenario, "zfs-get", Alias, "zfs", ImportPlans.MountProperties(vdevs.PoolName).Arguments);
    }
}
