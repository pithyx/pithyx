using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Encryption;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Tests.Storage.Integration;

/// <summary>
/// Pool keys on a USB stick (D385) with real mounts, file systems and OpenZFS: the stick is a removable scsi_debug disk in
/// memory, the pool an encrypted pool on a file vdev. The key is written to a FAT partition and to an ext4 file system on
/// the whole disk, the stick is read back after a re-import and unlocks the pool, and after a key change the stick's old
/// key leaves the pool locked and untouched.
/// </summary>
[Trait("Category", FileVdevs.Category)]
[Collection(ScsiDebug.Name)]
public sealed class UsbKeyIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AKeyOnAStickUnlocksTheReimportedPoolAndAnOlderKeyLeavesItLocked()
    {
        FileVdevs.RequireEnabled();
        await using var vdevs = FileVdevs.Create(1);
        var altRoot = $"/mnt/{vdevs.PoolName}";
        var commands = vdevs.Commands;
        var stickDisk = await LoadStickAsync(commands);
        try
        {
            var key = PoolKey.ToHex(PoolKey.New());
            await vdevs.CreateLayoutPoolAsync(key);
            await vdevs.RunAsync("zfs", ["create", $"{EncryptionCommands.Root(vdevs.PoolName)}/homes/alice"]);
            await File.WriteAllTextAsync(Path.Combine(vdevs.AltRoot, vdevs.PoolName, "homes", "alice", "note.txt"), "kept", Token);

            var vault = new KeyVault(Path.Combine(vdevs.Directory, "pool-keys"));
            var disks = new DiskInventory();
            var pools = new PoolInventory();
            var facts = new StorageFacts(disks, pools, DeploymentEnvironment.Prod, SystemFiles.Host);
            var hub = new TopicHub(new TopicRegistry([disks.Definition(), pools.Definition()]), TimeProvider.System, Options.Create(new JsonOptions()), NullLogger<TopicHub>.Instance);
            using var state = new SystemStateService(commands, disks, pools, new ZfsBackend(commands), new DiskProbe(commands), facts, hub, TimeProvider.System, NullLogger<SystemStateService>.Instance, vault: vault);
            var drive = new UsbKeyDrive(commands, Path.Combine(vdevs.Directory, "usb-key"), NullLogger<UsbKeyDrive>.Instance);
            using var encryption = new PoolEncryptionService(commands, pools, disks, state, facts, vault, drive, TimeProvider.System, NullLogger<PoolEncryptionService>.Instance);
            Pool Pool() => pools.Current.Pools.Single(pool => pool.Name == vdevs.PoolName);
            var note = Path.Combine(altRoot, vdevs.PoolName, "homes", "alice", "note.txt");

            async Task<UsbStick> StickAsync(string device)
            {
                await commands.RunAsync(new CommandRequest("udevadm", ["settle"]), Token);
                await state.RefreshDisksAsync([], Token);
                // Only the stick is offered: never the system disk, the test disks or the file vdev.
                return Assert.Single(encryption.Sticks(), stick => stick.Device == device);
            }

            await state.RefreshPoolsAsync(Token);
            var guid = Pool().PoolGuid;

            async Task ReimportAsync()
            {
                await vdevs.RunAsync("zpool", ["export", vdevs.PoolName]);
                await vdevs.RunAsync("zpool", ["import", "-N", "-R", altRoot, "-d", vdevs.Directory, guid]);
                await state.RefreshPoolsAsync(Token);
                Assert.True(Pool().Encryption!.Locked);
                Assert.False(File.Exists(note));
            }

            // A FAT partition, as most sticks come.
            await Run(commands, "sh", "-c", $"echo type=c | sfdisk -q {stickDisk}");
            await commands.RunAsync(new CommandRequest("udevadm", ["settle"]), Token);
            await Run(commands, "mkfs.vfat", "-n", "PXKEYS", $"{stickDisk}1");
            var fat = await StickAsync($"{stickDisk}1");
            Assert.Equal(("vfat", "PXKEYS"), (fat.FileSystem, fat.Label));
            await encryption.WriteToUsbAsync(vdevs.PoolName, fat.Device, PoolKey.FromHex(key), removeFromVault: false, "test", Token);
            Assert.Equal(0, StickMountCount(drive));
            Assert.Equal(key, (await drive.ReadAsync(fat, [guid], Token))[guid]);
            Assert.Equal("PXKEYS", Pool().Encryption!.UsbKey!.Stick);

            await ReimportAsync();
            await encryption.UnlockFromVaultAsync(Token);
            await encryption.UnlockFromSticksAsync(Token);
            Assert.Equal(0, StickMountCount(drive));
            Assert.False(Pool().Encryption!.Locked);
            Assert.True(Pool().Encryption!.UnlockedFromUsb);
            Assert.Equal("kept", await File.ReadAllTextAsync(note, Token));

            // ext4 over the whole disk, with the key taken from the vault and out of it afterwards.
            await Run(commands, "wipefs", "-a", "-q", stickDisk);
            await Run(commands, "mkfs.ext4", "-q", "-F", "-L", "PXEXT", stickDisk);
            var ext = await StickAsync(stickDisk);
            Assert.Equal(("ext4", stickDisk), (ext.FileSystem, ext.Device));
            vault.Save(guid, key);
            await encryption.WriteToUsbAsync(vdevs.PoolName, ext.Device, null, removeFromVault: true, "test", Token);
            Assert.False(vault.Has(guid));

            await ReimportAsync();
            await encryption.UnlockFromVaultAsync(Token);
            Assert.True(Pool().Encryption!.Locked);
            await encryption.UnlockFromSticksAsync(Token);
            Assert.False(Pool().Encryption!.Locked);
            Assert.Equal("kept", await File.ReadAllTextAsync(note, Token));

            // A changed key leaves the stick with the old one: the pool stays locked and untouched, and says why.
            await encryption.ChangeKeyAsync(vdevs.PoolName, PoolKey.New(), keepInVault: false, Token);
            Assert.Null(Pool().Encryption!.UsbKey);
            await ReimportAsync();
            await encryption.UnlockFromSticksAsync(Token);
            Assert.Equal(new PoolEncryption(Locked: true, KeyInVault: false, UsbKey: Pool().Encryption!.UsbKey, UsbFailed: true), Pool().Encryption);
            Assert.Equal(0, StickMountCount(drive));
        }
        finally
        {
            await commands.RunAsync(new CommandRequest("zpool", ["export", vdevs.PoolName]), CancellationToken.None);
            await commands.RunAsync(new CommandRequest("udevadm", ["settle"]), CancellationToken.None);
            await commands.RunAsync(new CommandRequest("modprobe", ["-r", "scsi_debug"]), CancellationToken.None);
            if (Directory.Exists(altRoot))
            {
                Directory.Delete(altRoot, recursive: true);
            }
        }
    }

    /// <summary>One removable scsi_debug disk in memory, the stick; returns its device node.</summary>
    private static async Task<string> LoadStickAsync(ProcessCommandRunner commands)
    {
        var load = await commands.RunAsync(new CommandRequest("modprobe", ["scsi_debug", "removable=1", "add_host=1", "num_tgts=1", "per_host_store=1", "dev_size_mb=64"]), Token);
        Assert.True(load.ExitCode == 0, $"modprobe scsi_debug failed ({load.StandardError})");
        await commands.RunAsync(new CommandRequest("udevadm", ["settle"]), Token);
        foreach (var block in Directory.EnumerateDirectories("/sys/block", "sd*"))
        {
            if ((await File.ReadAllTextAsync(Path.Combine(block, "device", "model"), Token)).Trim() == "scsi_debug")
            {
                return $"/dev/{Path.GetFileName(block)}";
            }
        }
        throw new InvalidOperationException("The scsi_debug stick did not appear.");
    }

    private static async Task Run(ProcessCommandRunner commands, string program, params string[] arguments)
    {
        var result = await commands.RunAsync(new CommandRequest(program, arguments, TimeSpan.FromMinutes(1)), Token);
        Assert.True(result.ExitCode == 0, $"{program} {string.Join(' ', arguments)} failed: {result.StandardError}");
    }

    /// <summary>How often the drive's mount folder is mounted now: after every access, never.</summary>
    private static int StickMountCount(UsbKeyDrive drive) =>
        MountTable.ParseMountInfo(File.ReadAllText("/proc/self/mountinfo")).Count(mount => mount.Target == drive.MountPoint);
}

/// <summary>Tests that load the scsi_debug module run one at a time: its parameters hold for every disk it makes.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ScsiDebug
{
    public const string Name = "scsi_debug";
}
