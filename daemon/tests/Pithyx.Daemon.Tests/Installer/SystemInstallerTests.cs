using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Installer;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.SystemSettings;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Installer;

/// <summary>The installation in real mode against scripted commands: the order of the commands and the files on the target.</summary>
public sealed class SystemInstallerTests : IDisposable
{
    private const long Gb = 1_000_000_000;

    private readonly string _root = Directory.CreateTempSubdirectory("pithyx-installer-").FullName;
    private readonly ScriptedCommandRunner _commands = new();
    private readonly DiskInventory _inventory = new();
    private readonly SystemSettingsService _system;
    private readonly PasswordHasher _hasher = new();
    private readonly SystemInstaller _installer;

    public SystemInstallerTests()
    {
        var proc = Directory.CreateDirectory(Path.Combine(_root, "proc")).FullName;
        File.WriteAllText(Path.Combine(proc, "cpuinfo"), "processor\t: 0\nflags\t\t: fpu cx16 lahf_lm popcnt sse4_1 sse4_2 ssse3\n");
        File.WriteAllText(Path.Combine(proc, "meminfo"), "MemTotal:        8000000 kB\nMemAvailable:    7000000 kB\n");
        var firmware = Directory.CreateDirectory(Path.Combine(_root, "efi")).FullName;
        _inventory.Replace(
        [
            new Disk("serial-A", "sda", "/dev/sda", "A", null, "SSD", null, 256 * Gb, false, "sata", false, false,
                [new Partition("sda1", "/dev/sda1", 100 * Gb, "ntfs", null, [])]),
            new Disk("serial-B", "nvme0n1", "/dev/nvme0n1", "B", null, "NVMe", null, 512 * Gb, false, "nvme", false, false, []),
            new Disk("serial-S", "sdc", "/dev/sdc", "S", null, "Stick", null, 16 * Gb, false, "usb", true, false, []),
        ]);
        _commands.Handler = request => request switch
        {
            { FileName: "blkid" } => new CommandResult(0, $"uuid-of-{Path.GetFileName(request.Arguments[^1])}\n", "", TimeSpan.Zero),
            { FileName: "mdadm", Arguments: ["--detail", "--scan"] } => new CommandResult(0, "ARRAY /dev/md/pithyx-system metadata=1.2 name=pithyx-system UUID=a:b:c:d\nARRAY /dev/md/pithyx-swap metadata=1.2 name=pithyx-swap UUID=e:f:0:1\n", "", TimeSpan.Zero),
            _ => new CommandResult(0, "", "", TimeSpan.Zero),
        };
        var environment = new InstallerEnvironment(true, true, Path.Combine(_root, "work"), "/run/live/medium/live/filesystem.squashfs", Path.Combine(_root, "console", "installer"), firmware, proc);
        _system = new SystemSettingsService(_commands, DaemonMode.Real, new SqliteConnectionFactory(Path.Combine(_root, "live.db")), TimeProvider.System);
        _installer = new SystemInstaller(environment, _commands, _inventory, _system, _hasher, TimeProvider.System, NullLogger<SystemInstaller>.Instance);
    }

    public void Dispose()
    {
        _system.Dispose();
        _hasher.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private static InstallRequest Request(params string[] diskIds) =>
        new("nas", "de", "DE", "Europe/Berlin", ["ptbtime1.ptb.de"], "de", diskIds, "admin", "correct horse battery staple", "Admin", EraseConfirmed: true);

    private string Target(string path) => Path.Combine(_root, "work", "target", path);

    private async Task<InstallerStatus> WaitAsync(string token)
    {
        for (var attempt = 0; attempt < 600; attempt++)
        {
            var status = _installer.Status(token);
            if (status.Phase is InstallerPhase.Installed or InstallerPhase.Failed)
            {
                return status;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException();
    }

    [Fact]
    public void TheFirstBrowserOwnsTheInstallation()
    {
        var token = _installer.Claim(null, "192.0.2.10");

        Assert.NotNull(token);
        Assert.Equal(token, _installer.Claim(token, "192.0.2.10"));
        Assert.Null(_installer.Claim(null, "192.0.2.20"));
        Assert.Null(_installer.Claim("forged", "192.0.2.20"));
        Assert.Contains("192.0.2.10", File.ReadAllText(Path.Combine(_root, "console", "installer")), StringComparison.Ordinal);
        Assert.False(_installer.Status("forged").Owner);
    }

    [Fact]
    public void TheStickAndUnknownDisksAreRefused()
    {
        var exception = Assert.Throws<PithyxException>(() => _installer.Start(Request("serial-S")));
        Assert.Equal(ErrorCodes.InstallerDiskUnsuitable, exception.Code);
        Assert.Equal("usb", exception.Parameters["reason"]);
        Assert.Equal(ErrorCodes.InstallerDiskCount, Assert.Throws<PithyxException>(() => _installer.Start(Request())).Code);
        Assert.Equal(ErrorCodes.InstallerDiskCount, Assert.Throws<PithyxException>(() => _installer.Start(Request("serial-A", "serial-A"))).Code);
        Assert.Equal(ErrorCodes.InstallerEraseNotConfirmed, Assert.Throws<PithyxException>(() => _installer.Start(Request("serial-A") with { EraseConfirmed = false })).Code);
        Assert.Equal(ErrorCodes.InstallerHostnameInvalid, Assert.Throws<PithyxException>(() => _installer.Start(Request("serial-A") with { Hostname = "My NAS" })).Code);
        Assert.Empty(_commands.Calls);
    }

    [Fact]
    public async Task AMirrorInstallPartitionsBothDisksAndBootsFromEither()
    {
        var token = _installer.Claim(null, "192.0.2.10")!;
        _installer.Start(Request("serial-A", "serial-B"));
        var status = await WaitAsync(token);

        Assert.Equal(InstallerPhase.Installed, status.Phase);
        Assert.StartsWith(AuthService.RecoveryKeyPrefix, status.RecoveryKey, StringComparison.Ordinal);
        var calls = _commands.Calls.Select(call => $"{call.FileName} {string.Join(' ', call.Arguments)}").ToList();
        // The old partition is wiped before the disk, and nothing is created before both disks are partitioned.
        Assert.True(calls.IndexOf("wipefs --all --force /dev/sda1") < calls.IndexOf("wipefs --all --force /dev/sda"));
        Assert.Contains("wipefs --all --force /dev/sda1 /dev/nvme0n1p1 /dev/sda2 /dev/nvme0n1p2 /dev/sda3 /dev/nvme0n1p3", calls);
        Assert.Contains("mdadm --create /dev/md/pithyx-system --run --level=1 --metadata=1.2 --homehost=any --name=pithyx-system --raid-devices=2 /dev/sda2 /dev/nvme0n1p2", calls);
        Assert.Contains("mkfs.fat -F 32 -n PITHYX-EFI /dev/nvme0n1p1", calls);
        Assert.Contains($"unsquashfs -force -no-progress -dest {Target("")} /run/live/medium/live/filesystem.squashfs", calls);
        // The first disk's boot entry is created last, so it leads the boot order.
        var second = calls.FindIndex(call => call.StartsWith("efibootmgr --quiet --create --disk /dev/nvme0n1", StringComparison.Ordinal));
        var first = calls.FindIndex(call => call.EndsWith("--efi-directory=/boot/efi --bootloader-id=pithyx --recheck", StringComparison.Ordinal));
        Assert.True(second >= 0 && first > second);
        Assert.Equal("umount --recursive " + Target(""), calls[^1]);

        Assert.Contains("UUID=uuid-of-nvme0n1p1\t/boot/efi2\tvfat\tumask=0077,nofail,x-systemd.device-timeout=10s", File.ReadAllText(Target("etc/fstab")), StringComparison.Ordinal);
        Assert.Contains("ARRAY /dev/md/pithyx-swap", File.ReadAllText(Target("etc/mdadm/mdadm.conf")), StringComparison.Ordinal);
        Assert.Equal("nas\n", File.ReadAllText(Target("etc/hostname")));
        Assert.Equal("/usr/share/zoneinfo/Europe/Berlin", new FileInfo(Target("etc/localtime")).LinkTarget);
        Assert.Contains("XKBLAYOUT=\"de\"", File.ReadAllText(Target("etc/default/keyboard")), StringComparison.Ordinal);
        Assert.Contains("ptbtime1.ptb.de", File.ReadAllText(Target("etc/systemd/timesyncd.conf.d/pithyx.conf")), StringComparison.Ordinal);
        Assert.Equal(status.InstallId + "\n", File.ReadAllText(Target("var/lib/pithyx/install-id")));

        var database = new SqliteConnectionFactory(Target("var/lib/pithyx/pithyx.db"));
        await using var connection = await database.OpenAsync(CancellationToken.None);
        Assert.Equal("admin", await connection.ExecuteScalarAsync<string>("SELECT username FROM users"));
        Assert.Equal(("de", "DE"), await connection.QuerySingleAsync<(string, string)>("SELECT language, region FROM system_region"));
    }

    [Fact]
    public async Task ASingleDiskIsAMirrorWithOneMember()
    {
        var token = _installer.Claim(null, "192.0.2.10")!;
        _installer.Start(Request("serial-B"));
        Assert.Equal(InstallerPhase.Installed, (await WaitAsync(token)).Phase);

        var calls = _commands.Calls.Select(call => $"{call.FileName} {string.Join(' ', call.Arguments)}").ToList();
        Assert.Contains("mdadm --create /dev/md/pithyx-swap --run --level=1 --metadata=1.2 --homehost=any --name=pithyx-swap --raid-devices=1 --force /dev/nvme0n1p3", calls);
        Assert.DoesNotContain(calls, call => call.StartsWith("efibootmgr", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, call => call.Contains("/dev/sda", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedStepIsReportedAndCanBeRetried()
    {
        var token = _installer.Claim(null, "192.0.2.10")!;
        var handler = _commands.Handler!;
        _commands.Handler = request => request.FileName == "mkfs.btrfs" ? new CommandResult(1, "", "device busy", TimeSpan.Zero) : handler(request);
        _installer.Start(Request("serial-B"));
        var failed = await WaitAsync(token);

        Assert.Equal(InstallerPhase.Failed, failed.Phase);
        Assert.Equal("filesystems", failed.Step);
        Assert.Equal(ErrorCodes.InstallerInstallFailed, failed.ErrorCode);
        Assert.Null(failed.RecoveryKey);
        Assert.Throws<PithyxException>(_installer.Reboot);

        _commands.Handler = handler;
        _installer.Start(Request("serial-B"));
        Assert.Equal(InstallerPhase.Installed, (await WaitAsync(token)).Phase);
    }
}
