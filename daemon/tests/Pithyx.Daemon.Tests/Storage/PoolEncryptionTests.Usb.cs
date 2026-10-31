using System.Text.Json.Nodes;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Encryption;
using Pithyx.Daemon.Storage.Health;
using static Pithyx.Daemon.Tests.Storage.StorageFixtures;

namespace Pithyx.Daemon.Tests.Storage;

/// <summary>
/// Pool keys on USB sticks (D385), with the stick recorded on dev01 (<c>lsblk-scsi-debug-usb-stick</c>: a removable
/// scsi_debug disk <c>sdh</c> with the FAT partition <c>sdh1</c> labelled <c>PXKEYS</c>, next to the system disk, the test
/// disks and a DVD drive). Mounting is scripted; the stick's files live in the drive's mount folder.
/// </summary>
public sealed partial class PoolEncryptionTests
{
    private const string StickDevice = "/dev/sdh1";

    private bool _stickMountFails;
    private bool _stickUnmountFails;

    private string StickFile => Path.Combine(_drive.MountPoint, UsbKeys.Folder, UsbKeys.FileName(Guid));

    private CommandResult Stick(CommandRequest request) => request.FileName == "mount"
        ? _stickMountFails ? new CommandResult(32, "", $"mount: {_drive.MountPoint}: wrong fs type, bad option, bad superblock on {StickDevice}.\n", TimeSpan.Zero) : Ok("")
        : _stickUnmountFails ? new CommandResult(32, "", $"umount: {_drive.MountPoint}: target is busy.\n", TimeSpan.Zero) : Ok("");

    private async Task PlugInStickAsync(string? keyFile = null, Action<JsonNode>? change = null)
    {
        if (keyFile is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StickFile)!);
            File.WriteAllText(StickFile, keyFile);
        }
        var listing = JsonNode.Parse(Read("lsblk-scsi-debug-usb-stick", "lsblk"))!;
        change?.Invoke(listing["blockdevices"]!.AsArray().Single(device => device!["name"]!.GetValue<string>() == "sdh")!);
        _commands.Results["lsblk"] = Ok(listing.ToJsonString());
        await _state.RefreshDisksAsync([], Token);
    }

    private async Task PullStickAsync()
    {
        _commands.Results["lsblk"] = Ok(Read("default", "lsblk"));
        await _state.RefreshDisksAsync([], Token);
    }

    private CommandRequest[] Calls(string program) => [.. _commands.Calls.Where(call => call.FileName == program)];

    private PoolEncryption Encryption() => _pools.Current.Pools.Single().Encryption!;

    private StorageAlert? LockedAlert() =>
        StorageAlerts.Evaluate(_pools.Current, [], [], _time.GetUtcNow()).SingleOrDefault(alert => alert.Key == $"pool.locked:{PoolName}");

    [Fact]
    public async Task OnlyAFileSystemOnAnUnusedRemovableDiskCanCarryAKey()
    {
        await PlugInStickAsync();

        var stick = Assert.Single(_encryption.Sticks());
        Assert.Equal(new UsbStick(StickDevice, stick.DiskId, "vfat", "PXKEYS", stick.Vendor, "scsi_debug", "28000", stick.SizeBytes, false), stick);
        Assert.Equal("PXKEYS", UsbKeys.Describe(stick));
        Assert.Equal("Linux scsi_debug", UsbKeys.Describe(stick with { Label = null, Vendor = "Linux" }));
        Assert.Equal(StickDevice, UsbKeys.Describe(stick with { Label = null, Vendor = null, Model = " " }));

        // A stick with anything mounted, or with a part of a pool, is in use and left alone whole.
        await PlugInStickAsync(change: disk => disk["children"]![0]!["mountpoints"] = new JsonArray("/media/stick"));
        Assert.Empty(_encryption.Sticks());
        await PlugInStickAsync(change: disk => disk["children"]!.AsArray().Add(JsonNode.Parse("""{"name":"sdh2","path":"/dev/sdh2","fstype":"zfs_member","mountpoints":[],"size":1}""")));
        Assert.Empty(_encryption.Sticks());
        // A file system ZFS or Linux does not share with other computers is no key carrier.
        await PlugInStickAsync(change: disk => disk["children"]![0]!["fstype"] = "ntfs");
        Assert.Empty(_encryption.Sticks());
        // Without the removable flag or USB a disk is a fixed disk.
        await PlugInStickAsync(change: disk => disk["rm"] = false);
        Assert.Empty(_encryption.Sticks());
        await PlugInStickAsync(change: disk => { disk["rm"] = false; disk["tran"] = "usb"; disk["ro"] = true; });
        Assert.True(Assert.Single(_encryption.Sticks()).ReadOnly);
    }

    [Fact]
    public void AKeyFileHoldsTheHexKeyAndTheStickIsMountedSoThatNothingChanges()
    {
        var hex = PoolKey.ToHex(_key);
        Assert.Equal(hex, UsbKeys.ParseKey(hex));
        Assert.Equal(hex, UsbKeys.ParseKey($"  {hex.ToUpperInvariant()}\r\n"));
        Assert.Null(UsbKeys.ParseKey(_key));
        Assert.Null(UsbKeys.ParseKey(hex[..63]));
        Assert.Null(UsbKeys.ParseKey($"{hex}\n{hex}"));
        Assert.Equal($"{Guid}.key", UsbKeys.FileName(Guid));

        var stick = new UsbStick(StickDevice, "serial-28000", "vfat", null, null, null, null, 0, false);
        Assert.Equal(["-t", "vfat", "-o", "ro,nosuid,nodev,noexec", StickDevice, "/run/pithyx/usb-key"], UsbKeys.MountReadOnly(stick, "/run/pithyx/usb-key").Arguments);
        Assert.Equal(["-t", "ext4", "-o", "ro,nosuid,nodev,noexec,noload", StickDevice, "/m"], UsbKeys.MountReadOnly(stick with { FileSystem = "ext4" }, "/m").Arguments);
        Assert.Equal(["-t", "exfat", "-o", "nosuid,nodev,noexec", StickDevice, "/m"], UsbKeys.MountWritable(stick with { FileSystem = "exfat" }, "/m").Arguments);
        Assert.Equal(["/m"], UsbKeys.Unmount("/m").Arguments);
    }

    [Fact]
    public async Task AStickWithTheKeyUnlocksTheLockedPoolOnceAndMayThenBePulled()
    {
        await PoolAsync();
        await PlugInStickAsync(PoolKey.ToHex(_key) + "\n");

        await _encryption.UnlockFromSticksAsync(Token);

        var mount = Assert.Single(Calls("mount"));
        Assert.Equal(["-t", "vfat", "-o", "ro,nosuid,nodev,noexec", StickDevice, _drive.MountPoint], mount.Arguments);
        Assert.Equal([_drive.MountPoint], Assert.Single(Calls("umount")).Arguments);
        var load = Assert.Single(_commands.Calls, call => call.Arguments is ["load-key", ..]);
        Assert.Equal(["load-key", Root], load.Arguments);
        Assert.Equal(4, Mounted().Length);
        var encryption = Encryption();
        Assert.False(encryption.Locked);
        Assert.True(encryption.UnlockedFromUsb);
        Assert.Equal(new PoolUsbKey("PXKEYS", "28000", _time.GetUtcNow()), encryption.UsbKey);
        Assert.Null(LockedAlert());

        // Locked again with the stick still in (an export and an import with no pass between), the stick opens it again.
        Pools("zfs-encrypted-locked");
        await _state.RefreshPoolsAsync(Token);
        await _encryption.UnlockFromSticksAsync(Token);
        Assert.Equal(2, Calls("mount").Length);
        Assert.False(Encryption().Locked);

        // Pulled, the stick changes nothing: the pool stays unlocked until it is locked again.
        await PullStickAsync();
        await _encryption.UnlockFromSticksAsync(Token);
        Assert.True(Encryption().UnlockedFromUsb);
        Assert.Equal(2, Calls("mount").Length);
        Assert.Equal(new PoolUsbKey("PXKEYS", "28000", _time.GetUtcNow()), _vault.Usb(Guid));

        Pools("zfs-encrypted-locked");
        await _state.RefreshPoolsAsync(Token);
        await _encryption.UnlockFromSticksAsync(Token);
        Assert.Equal(new PoolEncryption(Locked: true, KeyInVault: false, UsbKey: _vault.Usb(Guid)), Encryption());
        Assert.Equal(("storage.alert.poolLockedUsb", "PXKEYS"), (LockedAlert()!.Code, LockedAlert()!.Params["stick"].GetString()));
    }

    [Theory]
    [InlineData("older key")]
    [InlineData("damaged")]
    public async Task AKeyFileThatDoesNotUnlockLeavesThePoolUntouchedAndSaysSoUntilTheStickComesAgain(string problem)
    {
        await PoolAsync();
        await PlugInStickAsync(problem == "damaged" ? "PXPK-not-a-key-file" : PoolKey.ToHex(PoolKey.New()));

        await _encryption.UnlockFromSticksAsync(Token);
        await _encryption.UnlockFromSticksAsync(Token);

        Assert.Single(Calls("mount"));
        Assert.Empty(Mounted());
        Assert.Equal(new PoolEncryption(Locked: true, KeyInVault: false, UsbFailed: true), Encryption());
        Assert.Equal(("storage.alert.poolLockedUsbFailed", StorageAlerts.Critical), (LockedAlert()!.Code, LockedAlert()!.Severity));

        // Plugged in again, the stick is read again; with the right key now, it opens the pool.
        await PullStickAsync();
        await _encryption.UnlockFromSticksAsync(Token);
        await PlugInStickAsync(PoolKey.ToHex(_key));
        await _encryption.UnlockFromSticksAsync(Token);
        Assert.Equal(2, Calls("mount").Length);
        Assert.False(Encryption().Locked);
        Assert.False(Encryption().UsbFailed);
    }

    [Fact]
    public async Task APoolTheVaultOpensNeverMountsAStickButOneWhoseVaultKeyFailedDoes()
    {
        _vault.Save(Guid, PoolKey.ToHex(_key));
        await PoolAsync();
        await PlugInStickAsync(PoolKey.ToHex(_key));

        await _encryption.UnlockFromVaultAsync(Token);
        await _encryption.UnlockFromSticksAsync(Token);
        Assert.Empty(Calls("mount"));
        Assert.False(Encryption().Locked);
        Assert.False(Encryption().UnlockedFromUsb);

        Pools("zfs-encrypted-locked");
        _vault.Save(Guid, PoolKey.ToHex(PoolKey.New()));
        await _state.RefreshPoolsAsync(Token);
        await _encryption.UnlockFromVaultAsync(Token);
        Assert.True(Encryption().VaultFailed);
        await _encryption.UnlockFromSticksAsync(Token);
        Assert.Single(Calls("mount"));
        Assert.False(Encryption().Locked);
        Assert.True(Encryption().UnlockedFromUsb);
    }

    [Fact]
    public async Task AStickWithoutTheKeyOrOneThatCannotBeMountedChangesNothing()
    {
        await PoolAsync();
        await PlugInStickAsync();

        await _encryption.UnlockFromSticksAsync(Token);
        Assert.Single(Calls("umount"));
        Assert.Equal(new PoolEncryption(Locked: true, KeyInVault: false), Encryption());
        Assert.Equal("storage.alert.poolLocked", LockedAlert()!.Code);

        _stickMountFails = true;
        await PullStickAsync();
        await _encryption.UnlockFromSticksAsync(Token);
        await PlugInStickAsync(PoolKey.ToHex(_key));
        await _encryption.UnlockFromSticksAsync(Token);
        Assert.Equal(2, Calls("mount").Length);
        Assert.Equal(new PoolEncryption(Locked: true, KeyInVault: false), Encryption());
        Assert.DoesNotContain(_commands.Calls, call => call.Arguments is ["load-key", ..]);
    }

    [Fact]
    public async Task AKeyIsWrittenToAStickOnlyOnceItIsCheckedAndIsReadBack()
    {
        await PoolAsync();
        await PlugInStickAsync();

        Assert.Equal("storage.encryption.key_wrong", await ErrorAsync(() => _encryption.WriteToUsbAsync(PoolName, StickDevice, PoolKey.New(), false, "admin", Token)));
        Assert.Equal("storage.encryption.key_required", await ErrorAsync(() => _encryption.WriteToUsbAsync(PoolName, StickDevice, null, false, "admin", Token)));
        Assert.Equal("storage.usb.not_found", await ErrorAsync(() => _encryption.WriteToUsbAsync(PoolName, "/dev/sda1", _key, false, "admin", Token)));
        Assert.Equal("storage.pool.not_found", await ErrorAsync(() => _encryption.WriteToUsbAsync("nope", StickDevice, _key, false, "admin", Token)));
        Assert.Empty(Calls("mount"));

        await _encryption.WriteToUsbAsync(PoolName, StickDevice, _key, false, "admin", Token);

        Assert.Equal(PoolKey.ToHex(_key) + "\n", File.ReadAllText(StickFile));
        Assert.Equal(["-t", "vfat", "-o", "nosuid,nodev,noexec", StickDevice, _drive.MountPoint], Assert.Single(Calls("mount")).Arguments);
        Assert.Single(Calls("umount"));
        Assert.Equal(["load-key", "-n", Root], _commands.Calls.Last(call => call.Arguments is ["load-key", ..]).Arguments);
        Assert.DoesNotContain(_commands.Calls, call => call.Arguments.Any(argument => argument.Contains(PoolKey.ToHex(_key), StringComparison.Ordinal)));
        Assert.Equal(new PoolUsbKey("PXKEYS", "28000", _time.GetUtcNow()), Encryption().UsbKey);
        Assert.False(File.Exists($"{StickFile}.new"));
    }

    [Fact]
    public async Task TheVaultsKeyGoesOnAStickAndLeavesTheVaultWhenAsked()
    {
        _vault.Save(Guid, PoolKey.ToHex(_key));
        Pools("zfs-encrypted");
        await PoolAsync();
        await PlugInStickAsync();

        await _encryption.WriteToUsbAsync(PoolName, StickDevice, null, true, "admin", Token);

        Assert.Equal(PoolKey.ToHex(_key) + "\n", File.ReadAllText(StickFile));
        Assert.False(_vault.Has(Guid));
        Assert.Equal(new PoolEncryption(Locked: false, KeyInVault: false, UsbKey: new PoolUsbKey("PXKEYS", "28000", _time.GetUtcNow())), Encryption());
    }

    [Fact]
    public async Task AStickThatCannotTakeTheKeyIsNeverTakenForOne()
    {
        _vault.Save(Guid, PoolKey.ToHex(_key));
        await PoolAsync();

        await PlugInStickAsync(change: disk => { disk["rm"] = false; disk["tran"] = "usb"; disk["ro"] = true; });
        Assert.Equal("storage.usb.read_only", await ErrorAsync(() => _encryption.WriteToUsbAsync(PoolName, StickDevice, _key, true, "admin", Token)));
        Assert.Empty(Calls("mount"));

        await PlugInStickAsync();
        _stickMountFails = true;
        Assert.Equal("storage.usb.unreadable", await ErrorAsync(() => _encryption.WriteToUsbAsync(PoolName, StickDevice, _key, true, "admin", Token)));
        _stickMountFails = false;
        _stickUnmountFails = true;
        var error = await Assert.ThrowsAsync<Daemon.Errors.PithyxException>(() => _encryption.WriteToUsbAsync(PoolName, StickDevice, _key, true, "admin", Token));
        Assert.Equal(("storage.usb.write_failed", "PXKEYS"), (error.Code, error.Parameters!["stick"]));

        // Nothing failed quietly: the vault keeps the key, and no stick is named for the pool.
        Assert.True(_vault.Has(Guid));
        Assert.Null(_vault.Usb(Guid));
    }

    [Fact]
    public async Task AChangedKeyForgetsTheStickThatHoldsTheOldOne()
    {
        Pools("zfs-encrypted");
        await PoolAsync();
        _vault.SaveUsb(Guid, new PoolUsbKey("PXKEYS", "28000", _time.GetUtcNow()));

        await _encryption.ChangeKeyAsync(PoolName, PoolKey.New(), true, Token);

        Assert.Null(_vault.Usb(Guid));
        Assert.Null(Encryption().UsbKey);
    }

    [Fact]
    public void TheNoticeAfterAStickUnlockedAPoolNamesPoolAndStick()
    {
        var notice = PoolEncryptionService.UnlockedFromUsbNotice(PoolName, "PXKEYS");
        Assert.Equal("storage.usb.unlocked", notice.Code);
        Assert.Equal(PoolName, notice.Params["pool"]);
        Assert.Equal("PXKEYS", notice.Params["stick"]);
    }
}
