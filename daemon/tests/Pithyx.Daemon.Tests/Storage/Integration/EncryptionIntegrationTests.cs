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

    private static async Task<PoolList> ReadAsync(FileVdevs vdevs) => EncryptionCommands.Apply(await new ZfsBackend(vdevs.Commands).ReadAsync(Token), _ => false);

    private static async Task RecordAsync(FileVdevs vdevs, string scenario)
    {
        await vdevs.RecordAsync(scenario, "zpool-status", Alias, "zpool", ZfsBackend.Status.Arguments);
        await vdevs.RecordAsync(scenario, "zpool-list", Alias, "zpool", ZfsBackend.List.Arguments);
        await vdevs.RecordAsync(scenario, "zfs-list", Alias, "zfs", ZfsBackend.Datasets.Arguments);
        await vdevs.RecordAsync(scenario, "zfs-get", Alias, "zfs", ImportPlans.MountProperties(vdevs.PoolName).Arguments);
    }
}
