using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Monitoring;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.SystemSettings;

namespace Pithyx.Daemon.Installer;

/// <param name="Active">Whether the daemon runs as the installer of the live system (D294).</param>
/// <param name="Apply">Whether the installation touches the disks; simulation only walks through the steps.</param>
/// <param name="WorkDirectory">Where the target is mounted and its database is prepared; never below <c>/run</c>, which gets bound into the target.</param>
/// <param name="Squashfs">The root file system the live system runs from, which gets unpacked onto the target.</param>
/// <param name="ConsoleFile">Where the console greeting (image/rootfs/usr/lib/pithyx/issue.sh) reads who owns the installation; null in simulation.</param>
/// <param name="FirmwareDirectory">Where the kernel shows the EFI firmware.</param>
/// <param name="ProcDirectory">Where <c>cpuinfo</c> and <c>meminfo</c> are read.</param>
public sealed record InstallerEnvironment(bool Active, bool Apply, string WorkDirectory, string Squashfs, string? ConsoleFile, string FirmwareDirectory, string ProcDirectory)
{
    public const string KernelFlag = "pithyx.installer";

    public static InstallerEnvironment Resolve(IConfiguration configuration, DaemonMode mode, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var active = configuration["PITHYX_INSTALLER"] == "1"
            || (mode == DaemonMode.Real && ReadOrEmpty("/proc/cmdline").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(KernelFlag));
        return mode == DaemonMode.Real
            ? new InstallerEnvironment(active, true, "/mnt/pithyx", "/run/live/medium/live/filesystem.squashfs", "/run/pithyx/console/installer", "/sys/firmware/efi", "/proc")
            : new InstallerEnvironment(active, false, Path.Combine(dataDirectory, "installer"), "", null, Path.Combine(dataDirectory, "installer", "efi"), "/proc");
    }

    private static string ReadOrEmpty(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}

/// <summary>
/// The web installer of the live system (D294, D295): the first browser that claims it owns it; it checks the box,
/// offers the disks and installs the system onto one disk or a mirror of two. Layout per disk: an ESP, a member of the
/// mdadm RAID1 that holds btrfs with the root subvolume <c>@</c>, and a member of the swap RAID1. The root file system
/// is the squashfs the live system runs from, so the installed system is exactly what was tested and needs no network.
/// The administrator and the regional settings go into a database prepared next to the target and copied onto it.
/// </summary>
public sealed partial class SystemInstaller(
    InstallerEnvironment environment,
    ICommandRunner commands,
    DiskInventory inventory,
    SystemSettingsService system,
    Network.NetworkService network,
    PasswordHasher hasher,
    TimeProvider time,
    ILogger<SystemInstaller> logger)
{
    public static readonly IReadOnlyList<string> Steps = ["partition", "raid", "filesystems", "copy", "configure", "bootloader", "finish"];

    /// <summary>The console keyboard layouts the installer offers (XKB codes).</summary>
    public static readonly IReadOnlyList<string> KeyboardLayouts =
        ["us", "gb", "de", "ch", "at", "fr", "be", "nl", "es", "pt", "it", "se", "no", "dk", "fi", "pl", "cz"];

    public const string InstallIdFile = "install-id";

    private static readonly TimeSpan _shortCommand = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan _longCommand = TimeSpan.FromMinutes(30);

    private readonly Lock _lock = new();
    private string? _ownerToken;
    private InstallerPhase _phase = InstallerPhase.Ready;
    private int _stepIndex = -1;
    private string? _errorCode;
    private IReadOnlyDictionary<string, object?> _errorParams = new Dictionary<string, object?>();
    private string? _installId;
    private string? _recoveryKey;

    private string Target => Path.Combine(environment.WorkDirectory, "target");

    /// <summary>
    /// Claims the installation for the caller, or confirms the claim of its owner. Returns the owner's token, or null
    /// when another browser owns it.
    /// </summary>
    public string? Claim(string? token, string address)
    {
        lock (_lock)
        {
            if (_ownerToken is not null)
            {
                return IsOwnerLocked(token) ? _ownerToken : null;
            }
            _ownerToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        }
        LogClaimed(logger, address);
        WriteConsole($"This installation is in use by the browser at {address} since {time.GetUtcNow():HH:mm} UTC. To start over, restart from the USB stick.");
        return _ownerToken;
    }

    public bool IsOwner(string? token)
    {
        lock (_lock)
        {
            return IsOwnerLocked(token);
        }
    }

    public InstallerStatus Status(string? token)
    {
        var disks = Disks();
        lock (_lock)
        {
            var owner = IsOwnerLocked(token);
            return new InstallerStatus(
                _ownerToken is not null,
                owner,
                _phase,
                _stepIndex >= 0 ? Steps[_stepIndex] : null,
                Math.Max(_stepIndex, 0),
                Steps.Count,
                _errorCode,
                _errorParams,
                _installId,
                owner ? _recoveryKey : null,
                BuildInfo.Current.Version,
                Checks(disks));
        }
    }

    public async Task<InstallerOptions> OptionsAsync(CancellationToken cancellationToken)
    {
        var state = await network.StateAsync(null, cancellationToken);
        return new(Disks(), state.Links, state.Config, system.Time().TimeZones, KeyboardLayouts, InstallerFacts.MinDiskBytes);
    }

    /// <summary>Checks the request and starts the installation in the background; <see cref="Status"/> follows it.</summary>
    public async Task StartAsync(InstallRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // The endpoints exist only on the live system; this keeps any other caller off the disks as well.
        if (!environment.Active)
        {
            throw new InvalidOperationException("The installer only runs on the live system.");
        }
        var disks = Validate(request);
        var networkConfig = await NetworkConfigAsync(request, cancellationToken);
        lock (_lock)
        {
            if (_phase is not (InstallerPhase.Ready or InstallerPhase.Failed))
            {
                throw new PithyxException(ErrorCodes.InstallerInstallRunning);
            }
            _phase = InstallerPhase.Installing;
            _stepIndex = 0;
            _errorCode = null;
            _errorParams = new Dictionary<string, object?>();
        }
        _ = Task.Run(() => InstallAsync(request, disks, networkConfig), CancellationToken.None);
    }

    /// <summary>
    /// The network of the installed system: the settings of step 2, checked by the same rules as in Settings against
    /// the cards of this box (they keep their names after the installation), with the host name of step 1.
    /// </summary>
    private async Task<Network.NetworkConfig> NetworkConfigAsync(InstallRequest request, CancellationToken cancellationToken)
    {
        var state = await network.StateAsync(null, cancellationToken);
        var requested = request.Network ?? new Network.NetworkConfig([], new Network.GlobalNetworkConfig(request.Hostname, [], []));
        requested = requested with { Global = (requested.Global ?? new Network.GlobalNetworkConfig(request.Hostname, [], [])) with { HostName = request.Hostname } };
        var current = state.Config with { Global = state.Config.Global with { HostName = request.Hostname } };
        return Network.NetworkValidator.Validate(requested, state.Links, current, Network.NetworkPolicy.Everything);
    }

    /// <summary>Restarts the box into the installed system a moment after the answer went out.</summary>
    public void Reboot()
    {
        lock (_lock)
        {
            if (_phase != InstallerPhase.Installed)
            {
                throw new PithyxException(ErrorCodes.InstallerRebootNotReady);
            }
            _phase = InstallerPhase.Rebooting;
        }
        if (!environment.Apply)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), time);
            await commands.RunAsync(new CommandRequest("systemctl", ["reboot"]), CancellationToken.None);
        });
    }

    private bool IsOwnerLocked(string? token) =>
        _ownerToken is not null && token is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(_ownerToken), Encoding.ASCII.GetBytes(token));

    private IReadOnlyList<InstallerDisk> Disks() => InstallerFacts.EvaluateDisks(inventory.Current.Disks, inventory.Devices);

    private IReadOnlyList<InstallerCheck> Checks(IReadOnlyList<InstallerDisk> disks)
    {
        if (!environment.Apply)
        {
            return InstallerFacts.Checks("flags\t: cx16 lahf_lm popcnt sse4_1 sse4_2 ssse3", 8_000_000_000, uefi: true, secureBootVariable: null, disks, Architecture.X64);
        }
        var memInfo = ReadText(Path.Combine(environment.ProcDirectory, "meminfo"));
        var secureBoot = Directory.Exists(Path.Combine(environment.FirmwareDirectory, "efivars"))
            ? Directory.EnumerateFiles(Path.Combine(environment.FirmwareDirectory, "efivars"), "SecureBoot-*").Select(ReadBytes).FirstOrDefault()
            : null;
        return InstallerFacts.Checks(
            ReadText(Path.Combine(environment.ProcDirectory, "cpuinfo")),
            memInfo.Length == 0 ? 0 : ProcParsers.ParseMemInfo(memInfo).TotalBytes,
            Directory.Exists(environment.FirmwareDirectory),
            secureBoot,
            disks,
            RuntimeInformation.OSArchitecture);
    }

    private List<Disk> Validate(InstallRequest request)
    {
        if (Checks(Disks()).FirstOrDefault(check => !check.Passed) is { } failed)
        {
            throw new PithyxException(ErrorCodes.InstallerCheckFailed, new Dictionary<string, object?> { ["check"] = failed.Id });
        }
        if (!InstallerFacts.IsHostname(request.Hostname))
        {
            throw new PithyxException(ErrorCodes.InstallerHostnameInvalid);
        }
        if (!new SystemRegion(request.Language, request.Region).IsValid())
        {
            throw new PithyxException(ErrorCodes.RequestBodyInvalid);
        }
        if (!SystemSettingsService.IsTimeZone(request.TimeZone))
        {
            throw new PithyxException(ErrorCodes.SystemTimeZoneInvalid, new Dictionary<string, object?> { ["zone"] = request.TimeZone ?? "" });
        }
        if ((request.NtpServers ?? []).FirstOrDefault(server => !HostParsers.IsTimeServer(server.Trim())) is { } server)
        {
            throw new PithyxException(ErrorCodes.SystemTimeServerInvalid, new Dictionary<string, object?> { ["server"] = server });
        }
        if (!KeyboardLayouts.Contains(request.KeyboardLayout))
        {
            throw new PithyxException(ErrorCodes.InstallerKeyboardInvalid);
        }
        CredentialRules.NormalizeUsername(request.Username);
        CredentialRules.NormalizeDisplayName(request.DisplayName);
        CredentialRules.ValidateNewPassword(request.Password);

        var ids = request.DiskIds ?? [];
        if (ids.Count is < 1 or > 2 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
        {
            throw new PithyxException(ErrorCodes.InstallerDiskCount);
        }
        var offered = Disks();
        var chosen = new List<Disk>();
        foreach (var id in ids)
        {
            var disk = offered.FirstOrDefault(candidate => candidate.Disk.Id == id);
            if (disk is not { Suitable: true })
            {
                throw new PithyxException(ErrorCodes.InstallerDiskUnsuitable, new Dictionary<string, object?> { ["diskId"] = id, ["reason"] = disk?.Reason ?? "missing" });
            }
            chosen.Add(disk.Disk);
        }
        if (!request.EraseConfirmed)
        {
            throw new PithyxException(ErrorCodes.InstallerEraseNotConfirmed);
        }
        return chosen;
    }

    private async Task InstallAsync(InstallRequest request, List<Disk> disks, Network.NetworkConfig networkConfig)
    {
        var cancellationToken = CancellationToken.None;
        try
        {
            var described = string.Join(", ", disks.Select(disk => $"{disk.Path} ({disk.Serial})"));
            LogStarted(logger, described);
            var installId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            string recoveryKey;
            if (environment.Apply)
            {
                recoveryKey = await InstallOntoDisksAsync(request, disks, networkConfig, installId, cancellationToken);
            }
            else
            {
                foreach (var index in Enumerable.Range(0, Steps.Count))
                {
                    SetStep(index);
                    await Task.Delay(TimeSpan.FromMilliseconds(300), TimeProvider.System, cancellationToken);
                }
                recoveryKey = await PrepareDatabaseAsync(request, networkConfig, cancellationToken);
            }
            lock (_lock)
            {
                _installId = installId;
                _recoveryKey = recoveryKey;
                _phase = InstallerPhase.Installed;
            }
            LogFinished(logger);
        }
        catch (Exception exception)
        {
            var code = exception is PithyxException pithyx ? pithyx.Code : ErrorCodes.InstallerInstallFailed;
            var parameters = exception is PithyxException withParameters ? withParameters.Parameters : new Dictionary<string, object?>();
            lock (_lock)
            {
                _phase = InstallerPhase.Failed;
                _errorCode = code;
                _errorParams = parameters;
            }
            LogFailed(logger, exception, _stepIndex >= 0 ? Steps[_stepIndex] : "start");
            if (environment.Apply)
            {
                // Leave nothing mounted, so the next attempt can partition the disks again.
                await commands.RunAsync(new CommandRequest("umount", ["--recursive", "--lazy", Target], _shortCommand), CancellationToken.None);
            }
        }
    }

    private async Task<string> InstallOntoDisksAsync(InstallRequest request, List<Disk> disks, Network.NetworkConfig networkConfig, string installId, CancellationToken cancellationToken)
    {
        var esps = InstallPlans.Partitions(disks, 1);

        SetStep(0);
        await commands.RunAsync(new CommandRequest("umount", ["--recursive", "--lazy", Target], _shortCommand), cancellationToken);
        await commands.RunAsync(InstallPlans.StopArrays(), cancellationToken);
        await RunAllAsync(InstallPlans.Partition(disks), cancellationToken);

        SetStep(1);
        await RunAllAsync(InstallPlans.CreateArrays(disks), cancellationToken);

        SetStep(2);
        await RunAllAsync(InstallPlans.Format(disks), cancellationToken);
        Directory.CreateDirectory(Target);
        await RunAsync("mount", [InstallPlans.SystemArray, Target], cancellationToken);
        await RunAsync("btrfs", ["subvolume", "create", Path.Combine(Target, "@")], cancellationToken);
        await RunAsync("umount", [Target], cancellationToken);
        await RunAsync("mount", ["-o", "subvol=@,compress=zstd:1,noatime", InstallPlans.SystemArray, Target], cancellationToken);
        for (var index = 0; index < esps.Count; index++)
        {
            var mountPoint = Path.Combine(Target, EspMountPoint(index).TrimStart('/'));
            Directory.CreateDirectory(mountPoint);
            await RunAsync("mount", [esps[index], mountPoint], cancellationToken);
        }

        SetStep(3);
        await RunAsync("unsquashfs", ["-force", "-no-progress", "-dest", Target, environment.Squashfs], cancellationToken, _longCommand);

        SetStep(4);
        var systemUuid = await UuidAsync(InstallPlans.SystemArray, cancellationToken);
        var swapUuid = await UuidAsync(InstallPlans.SwapArray, cancellationToken);
        var espUuids = new List<string>();
        foreach (var esp in esps)
        {
            espUuids.Add(await UuidAsync(esp, cancellationToken));
        }
        var arrays = await RunAsync(InstallPlans.ScanArrays(), cancellationToken);
        await WriteTargetAsync("etc/fstab", InstallerFiles.Fstab(systemUuid, swapUuid, espUuids), cancellationToken);
        await WriteTargetAsync("etc/mdadm/mdadm.conf", InstallerFiles.MdadmConf(arrays.StandardOutput), cancellationToken);
        await WriteTargetAsync("etc/hostname", request.Hostname + "\n", cancellationToken);
        await WriteTargetAsync("etc/hosts", InstallerFiles.Hosts(request.Hostname), cancellationToken);
        await WriteTargetAsync("etc/timezone", request.TimeZone + "\n", cancellationToken);
        var localtime = Path.Combine(Target, "etc/localtime");
        File.Delete(localtime);
        File.CreateSymbolicLink(localtime, $"/usr/share/zoneinfo/{request.TimeZone}");
        await WriteTargetAsync("etc/default/keyboard", InstallerFiles.Keyboard(request.KeyboardLayout), cancellationToken);
        await WriteTargetAsync("etc/default/grub.d/pithyx.cfg", InstallerFiles.GrubDefaults(), cancellationToken);
        var servers = (request.NtpServers ?? []).Select(server => server.Trim()).Where(server => server.Length > 0).ToList();
        if (servers.Count > 0)
        {
            await WriteTargetAsync(SystemSettingsService.TimesyncdDropIn.TrimStart('/'), HostParsers.RenderTimesyncdDropIn(servers), cancellationToken);
        }
        // The same files Settings > Network writes; cards without settings keep the image's DHCP file.
        foreach (var (path, content) in Network.NetworkdConfig.Render(networkConfig))
        {
            await WriteTargetAsync(path.TrimStart('/'), content, cancellationToken);
        }
        var recoveryKey = await PrepareDatabaseAsync(request, networkConfig, cancellationToken);
        var dataDirectory = Path.Combine(Target, "var/lib/pithyx");
        Directory.CreateDirectory(dataDirectory);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(dataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        await CopyDatabaseAsync(Path.Combine(dataDirectory, "pithyx.db"), cancellationToken);
        await WriteTargetAsync($"var/lib/pithyx/{InstallIdFile}", installId + "\n", cancellationToken);

        SetStep(5);
        // GRUB and the initramfs need the devices, md and the EFI variables of the live system.
        foreach (var path in new[] { "dev", "proc", "sys", "run" })
        {
            await RunAsync("mount", ["--rbind", $"/{path}", Path.Combine(Target, path)], cancellationToken);
            // Unmounting the target later must not reach back into the live system's mounts.
            await RunAsync("mount", ["--make-rslave", Path.Combine(Target, path)], cancellationToken);
        }
        await RunAsync("chroot", [Target, "ssh-keygen", "-A"], cancellationToken);
        // Each box gets its own ZFS host id (D341); the initramfs below copies it, so boxes from one ISO never share one.
        await RunAsync("chroot", [Target, "zgenhostid", "-f"], cancellationToken);
        await RunAsync("chroot", [Target, "update-initramfs", "-u", "-k", "all"], cancellationToken, _longCommand);
        // The second disk first: grub-install of the first disk puts its own boot entry at the front of the boot order.
        for (var index = esps.Count - 1; index >= 0; index--)
        {
            var espMountPoint = EspMountPoint(index);
            // The same installs as the ESP sync after updates (EspPlans); only the first disk's entry goes into NVRAM.
            var installs = Updates.EspPlans.GrubInstall(espMountPoint, Target);
            await RunAsync(installs[0], cancellationToken);
            if (index == 0)
            {
                await RunAsync("chroot", [Target, "grub-install", "--target=x86_64-efi", $"--efi-directory={espMountPoint}", "--bootloader-id=pithyx", "--recheck"], cancellationToken);
            }
            else
            {
                await RunAsync(installs[1], cancellationToken);
                await RunAsync("efibootmgr", ["--quiet", "--create", "--disk", disks[index].Path, "--part", "1", "--label", "Pithyx (second disk)", "--loader", @"\EFI\pithyx\grubx64.efi"], cancellationToken);
            }
        }
        await RunAsync("chroot", [Target, "update-grub"], cancellationToken, _longCommand);

        SetStep(6);
        await RunAsync("sync", [], cancellationToken);
        await RunAsync("umount", ["--recursive", Target], cancellationToken);
        return recoveryKey;
    }

    /// <summary>Where the ESP of the first disk and of the mirror disk is mounted in the installed system.</summary>
    public static string EspMountPoint(int index) => index == 0 ? "/boot/efi" : $"/boot/efi{index + 1}";

    private async Task<string> UuidAsync(string device, CancellationToken cancellationToken)
    {
        var uuid = (await RunAsync("blkid", ["--match-tag", "UUID", "--output", "value", device], cancellationToken)).StandardOutput.Trim();
        return uuid.Length > 0 ? uuid : throw new PithyxException(ErrorCodes.InstallerInstallFailed, new Dictionary<string, object?> { ["step"] = "configure", ["command"] = "blkid" });
    }

    /// <summary>
    /// Creates the database of the installed system next to the target: the migrations, the administrator with its
    /// recovery key (the same code as the first setup), the regional settings and the network. Returns the recovery key.
    /// </summary>
    private async Task<string> PrepareDatabaseAsync(InstallRequest request, Network.NetworkConfig networkConfig, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(environment.WorkDirectory);
        var path = Path.Combine(environment.WorkDirectory, "pithyx.db");
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, $"{path}-wal", $"{path}-shm" })
        {
            File.Delete(file);
        }
        var connections = new SqliteConnectionFactory(path);
        await new MigrationRunner(connections, time, NullLogger<MigrationRunner>.Instance).RunAsync(Migration.Embedded(), cancellationToken);
        var auth = new AuthService(connections, hasher, new LoginChallenges(time), new Permissions.PermissionService(connections), time);
        var (_, recoveryKey) = await auth.SetupAsync(new SetupRequest(request.Username, request.Password, request.DisplayName), cancellationToken);
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync(
                "INSERT INTO system_region (id, language, region, updated_at) VALUES (1, @Language, @Region, @now)",
                new { request.Language, request.Region, now = SessionService.Format(time.GetUtcNow()) });
            await connection.ExecuteAsync(
                "INSERT INTO network_config (id, config, updated_at) VALUES (1, @config, @now)",
                new { config = Network.NetworkJson.Serialize(networkConfig), now = SessionService.Format(time.GetUtcNow()) });
        }
        return recoveryKey;
    }

    private async Task CopyDatabaseAsync(string destination, CancellationToken cancellationToken)
    {
        var connections = new SqliteConnectionFactory(Path.Combine(environment.WorkDirectory, "pithyx.db"));
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync("VACUUM INTO @destination", new { destination });
        }
        SqliteConnection.ClearAllPools();
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private async Task WriteTargetAsync(string relativePath, string content, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Target, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, cancellationToken);
    }

    private Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken, TimeSpan? timeout = null) =>
        RunAsync(new CommandRequest(program, arguments, timeout ?? _shortCommand), cancellationToken);

    private async Task RunAllAsync(IReadOnlyList<CommandRequest> requests, CancellationToken cancellationToken)
    {
        foreach (var request in requests)
        {
            await RunAsync(request, cancellationToken);
        }
    }

    private async Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var result = await commands.RunAsync(request, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new PithyxException(ErrorCodes.InstallerInstallFailed, new Dictionary<string, object?>
            {
                ["step"] = Steps[Math.Max(_stepIndex, 0)],
                ["command"] = request.FileName == "chroot" && request.Arguments.Count > 1 ? request.Arguments[1] : request.FileName,
                ["exitCode"] = result.ExitCode,
            });
        }
        return result;
    }

    private void SetStep(int index)
    {
        lock (_lock)
        {
            _stepIndex = index;
        }
        LogStep(logger, Steps[index]);
    }

    private void WriteConsole(string line)
    {
        if (environment.ConsoleFile is not { } file)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, line + "\n");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogConsoleFailed(logger, exception, file);
        }
    }

    private static string ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static byte[]? ReadBytes(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The installation was claimed by {Address}")]
    private static partial void LogClaimed(ILogger logger, string address);

    [LoggerMessage(Level = LogLevel.Information, Message = "Installation started on {Disks}")]
    private static partial void LogStarted(ILogger logger, string disks);

    [LoggerMessage(Level = LogLevel.Information, Message = "Installation step {Step}")]
    private static partial void LogStep(ILogger logger, string step);

    [LoggerMessage(Level = LogLevel.Information, Message = "Installation finished")]
    private static partial void LogFinished(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Installation failed in step {Step}")]
    private static partial void LogFailed(ILogger logger, Exception exception, string step);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the console state {File}")]
    private static partial void LogConsoleFailed(ILogger logger, Exception exception, string file);
}

/// <summary>The files the installer writes onto the target, as pure functions.</summary>
public static class InstallerFiles
{
    public const string Header = "# Managed by pithyx: written by the installer.\n";

    public static string Fstab(string systemUuid, string swapUuid, IReadOnlyList<string> espUuids)
    {
        ArgumentNullException.ThrowIfNull(espUuids);
        var builder = new StringBuilder(Header);
        builder.Append(CultureInfo.InvariantCulture, $"UUID={systemUuid}\t/\tbtrfs\tsubvol=@,compress=zstd:1,noatime\t0\t0\n");
        for (var index = 0; index < espUuids.Count; index++)
        {
            // No ESP may stop the boot: with a mirror, either disk can be the missing one.
            builder.Append(CultureInfo.InvariantCulture, $"UUID={espUuids[index]}\t{SystemInstaller.EspMountPoint(index)}\tvfat\tumask=0077,nofail,x-systemd.device-timeout=10s\t0\t2\n");
        }
        builder.Append(CultureInfo.InvariantCulture, $"UUID={swapUuid}\tnone\tswap\tsw\t0\t0\n");
        return builder.ToString();
    }

    /// <summary>The arrays from <c>mdadm --detail --scan</c>, so the initramfs assembles them by UUID.</summary>
    public static string MdadmConf(string scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var arrays = scan.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("ARRAY ", StringComparison.Ordinal));
        return Header + "HOMEHOST <ignore>\nMAILADDR root\n" + string.Concat(arrays.Select(line => line + "\n"));
    }

    public static string Hosts(string hostname) =>
        $"127.0.0.1\tlocalhost\n127.0.1.1\t{hostname}\n::1\t\tlocalhost ip6-localhost ip6-loopback\n";

    public static string Keyboard(string layout) =>
        Header + $"XKBMODEL=\"pc105\"\nXKBLAYOUT=\"{layout}\"\nXKBVARIANT=\"\"\nXKBOPTIONS=\"\"\nBACKSPACE=\"guess\"\n";

    public static string GrubDefaults() =>
        Header + "GRUB_DISTRIBUTOR=\"Pithyx\"\nGRUB_TIMEOUT=3\nGRUB_CMDLINE_LINUX_DEFAULT=\"quiet\"\n";
}
