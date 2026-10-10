using System.Text.Json;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Updates;

/// <summary>Where the updater keeps its state on a box.</summary>
/// <param name="State">On the system disk, inside <c>@</c>: <c>trial.json</c>, <c>result.json</c>, <c>request.json</c>.</param>
/// <param name="Cache">The unpacked update files (<c>/var/cache/pithyx/updates</c>), rebuildable.</param>
/// <param name="Run">Runtime state on tmpfs: the progress of an apply and the copy of the daemon that applies.</param>
/// <param name="Top">Where the top of the system btrfs is mounted while snapshots change.</param>
/// <param name="Release">The installed version (<c>PITHYX_VERSION=</c>).</param>
/// <param name="DaemonDirectory">The installed daemon, copied to <see cref="Run"/> before an apply.</param>
public sealed record UpdatePaths(string State, string Cache, string Run, string Top, string Release, string DaemonDirectory)
{
    /// <summary>The link udev makes to the system array.</summary>
    public string SystemArray { get; init; } = SystemSnapshots.SystemArray;

    /// <summary>The mount table, where the ESPs are found.</summary>
    public string Mounts { get; init; } = "/proc/mounts";

    public static UpdatePaths Box { get; } = new(
        "/var/lib/pithyx/update", "/var/cache/pithyx/updates", "/run/pithyx/update", "/run/pithyx/system",
        "/usr/lib/pithyx/release", "/usr/lib/pithyx/daemon");

    public string TrialFile => Path.Combine(State, "trial.json");
    public string ResultFile => Path.Combine(State, "result.json");
    public string RequestFile => Path.Combine(State, "request.json");
    public string ProgressFile => Path.Combine(Run, "progress.json");
    public string StagedDirectory => Path.Combine(Cache, "staged");
}

/// <summary>An update waiting for its health check after the restart (<c>trial.json</c>).</summary>
public sealed record UpdateTrial(long HistoryId, string FromVersion, string ToVersion, string Snapshot, IReadOnlyList<string> Pools, DateTimeOffset StartedAt);

/// <summary>
/// How an update or rollback ended, written for the daemon of the system that runs afterwards (<c>result.json</c>): a
/// rollback writes it into the restored <c>@</c>, together with the history the restored database does not know yet.
/// </summary>
public sealed record UpdateOutcome(
    long HistoryId,
    string State,
    string? ErrorCode,
    IReadOnlyDictionary<string, object?>? ErrorParams,
    DateTimeOffset FinishedAt,
    IReadOnlyList<UpdateHistoryEntry> History);

/// <summary>What the copy of the daemon that applies an update reads (<c>request.json</c>).
/// <c>Directory</c>: The unpacked, verified update file.
/// <c>IncludeDebian</c>: Also install the Debian updates in the same snapshot.
/// <c>History</c>: The history so far, carried into the restored system if the update rolls back.
/// </summary>
public sealed record UpdateApplyRequest(
    long HistoryId,
    string Directory,
    string FromVersion,
    string ToVersion,
    bool IncludeDebian,
    IReadOnlyList<UpdateHistoryEntry> History);

/// <summary>The progress of an apply, as the applying process writes it for the daemon (<c>progress.json</c>).
/// <c>Step</c>: snapshot, install, debian, bootloader, restart.
/// <c>State</c>: running, restarting (the box reboots now), failed, rolled_back (the box reboots into the snapshot).
/// </summary>
public sealed record UpdateProgress(long HistoryId, string Step, string State, string? ErrorCode, IReadOnlyDictionary<string, object?>? ErrorParams);

/// <summary>
/// The system side of updates on a box, through <see cref="ICommandRunner"/>: the layout check, the snapshots with
/// their GRUB menu, the trial flag GRUB reads from every ESP, the ESP sync, the pools and the reboot. Used by the daemon,
/// by the process that applies an update, by the health check after the restart and by the recovery menu, so all of
/// them take the same steps (D338).
/// </summary>
public sealed partial class UpdateSystem(ICommandRunner commands, UpdatePaths paths, TimeProvider time, ILogger<UpdateSystem> logger)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static readonly TimeSpan _short = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan _long = TimeSpan.FromMinutes(30);

    public UpdatePaths Paths => paths;

    /// <summary>The installed Pithyx version from <c>/usr/lib/pithyx/release</c>.</summary>
    public string InstalledVersion() =>
        File.Exists(paths.Release)
            ? File.ReadLines(paths.Release).Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("PITHYX_VERSION=", StringComparison.Ordinal))?["PITHYX_VERSION=".Length..] ?? "0.0.0"
            : "0.0.0";

    /// <summary>Whether root is the installer's layout; only then do updates, snapshots and rollbacks apply.</summary>
    public async Task<SystemLayout> LayoutAsync(CancellationToken cancellationToken)
    {
        var result = await commands.RunAsync(new CommandRequest("findmnt", ["--json", "--output", "SOURCE,FSTYPE,OPTIONS,UUID", "--target", "/"], _short), cancellationToken);
        if (result.ExitCode != 0)
        {
            return new SystemLayout("findmnt", null);
        }
        // udev's link names the array's kernel device (../md127); findmnt shows that device.
        var link = new FileInfo(paths.SystemArray);
        var array = link.LinkTarget is { } target ? Path.GetFullPath(target, link.DirectoryName!) : link.Exists ? link.FullName : null;
        return SystemSnapshots.CheckLayout(result.StandardOutput, array);
    }

    public async Task<SystemLayout> RequireLayoutAsync(CancellationToken cancellationToken)
    {
        var layout = await LayoutAsync(cancellationToken);
        return layout.Supported
            ? layout
            : throw new PithyxException(ErrorCodes.UpdateSystemUnsupported, new Dictionary<string, object?> { ["reason"] = layout.Reason });
    }

    /// <summary>Runs <paramref name="work"/> with the top of the system btrfs mounted at <see cref="UpdatePaths.Top"/>.</summary>
    public async Task<T> WithTopAsync<T>(Func<string, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        Directory.CreateDirectory(paths.Top);
        await RunAsync(SystemSnapshots.Plans.MountTop(paths.Top), "mount", cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.Combine(paths.Top, SystemSnapshots.Directory));
            return await work(paths.Top);
        }
        finally
        {
            await commands.RunAsync(SystemSnapshots.Plans.UnmountTop(paths.Top), CancellationToken.None);
        }
    }

    /// <summary>The kept snapshots, newest first, read from their files beside them (the top must be mounted).</summary>
    public static IReadOnlyList<SnapshotInfo> ReadSnapshots(string top) =>
        [.. Directory.EnumerateFiles(Path.Combine(top, SystemSnapshots.Directory), "*.json")
            .Where(file => SystemSnapshots.IsName(Path.GetFileNameWithoutExtension(file)) && Directory.Exists(Path.ChangeExtension(file, null)))
            .Select(file => SystemSnapshots.Deserialize(File.ReadAllText(file)))
            .OfType<SnapshotInfo>()
            .OrderByDescending(snapshot => snapshot.Name, StringComparer.Ordinal)];

    public Task<IReadOnlyList<SnapshotInfo>> SnapshotsAsync(CancellationToken cancellationToken) =>
        WithTopAsync(top => Task.FromResult(ReadSnapshots(top)), cancellationToken);

    /// <summary>
    /// Takes a read-only snapshot of <c>@</c> (with <c>/var/lib/pithyx</c>), keeps the newest three, removes old
    /// <c>@rollback-*</c> leftovers and writes the GRUB menu.
    /// </summary>
    public Task<SnapshotInfo> TakeSnapshotAsync(string reason, string fileSystemUuid, CancellationToken cancellationToken) =>
        WithTopAsync(async top =>
        {
            var info = new SnapshotInfo(SystemSnapshots.Name(time.GetUtcNow()), InstalledVersion(), time.GetUtcNow(), KernelRelease(), reason);
            await RunAsync(SystemSnapshots.Plans.Take(top, info.Name), "snapshot", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(top, SystemSnapshots.Directory, info.Name + ".json"), SystemSnapshots.Serialize(info), cancellationToken);
            foreach (var old in SystemSnapshots.Prune(ReadSnapshots(top), SystemSnapshots.Keep, info.Name))
            {
                await RunAsync(SystemSnapshots.Plans.Delete(top, Path.Combine(SystemSnapshots.Directory, old)), "snapshot", cancellationToken);
                File.Delete(Path.Combine(top, SystemSnapshots.Directory, old + ".json"));
            }
            foreach (var leftover in Directory.EnumerateDirectories(top, "@rollback-*"))
            {
                await RunAsync(SystemSnapshots.Plans.Delete(top, Path.GetFileName(leftover)), "snapshot", cancellationToken);
            }
            await WriteMenuAsync(top, fileSystemUuid, trialSnapshot: null, cancellationToken);
            LogSnapshot(info.Name, reason);
            return info;
        }, cancellationToken);

    /// <summary>
    /// Makes <paramref name="snapshot"/> the system again: <c>@</c> moves aside, a writable snapshot of it becomes
    /// <c>@</c>, <paramref name="outcome"/> lands in the restored <c>/var/lib/pithyx/update/result.json</c> for its
    /// daemon, the trial flag goes and the menu is written. The caller reboots.
    /// </summary>
    public async Task RollbackAsync(string snapshot, string fileSystemUuid, UpdateOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        await WithTopAsync(async top =>
        {
            if (!SystemSnapshots.IsName(snapshot) || !Directory.Exists(Path.Combine(top, SystemSnapshots.Directory, snapshot)))
            {
                throw new PithyxException(ErrorCodes.UpdateSnapshotNotFound, new Dictionary<string, object?> { ["snapshot"] = snapshot });
            }
            foreach (var command in SystemSnapshots.Plans.Rollback(top, snapshot, time.GetUtcNow()))
            {
                await RunAsync(command, "rollback", cancellationToken);
            }
            var state = Path.Combine(top, SystemSnapshots.Root, paths.State.TrimStart('/'));
            Directory.CreateDirectory(state);
            File.Delete(Path.Combine(state, Path.GetFileName(paths.TrialFile)));
            await File.WriteAllTextAsync(Path.Combine(state, Path.GetFileName(paths.ResultFile)), JsonSerializer.Serialize(outcome, Json), cancellationToken);
            await WriteMenuAsync(top, fileSystemUuid, trialSnapshot: null, cancellationToken);
            return true;
        }, cancellationToken);
        await SetTrialAsync(null, cancellationToken);
        LogRollback(snapshot, outcome.State);
    }

    /// <summary>Writes <c>/@snapshots/menu.cfg</c>; with <paramref name="trialSnapshot"/> it holds the automatic rollback entry.</summary>
    public static async Task WriteMenuAsync(string top, string fileSystemUuid, string? trialSnapshot, CancellationToken cancellationToken)
    {
        var menu = SystemSnapshots.RenderMenu(ReadSnapshots(top), fileSystemUuid, trialSnapshot);
        var path = Path.Combine(top, SystemSnapshots.Directory, SystemSnapshots.MenuFile);
        await File.WriteAllTextAsync(path + ".tmp", menu, cancellationToken);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public Task WriteMenuAsync(string fileSystemUuid, string? trialSnapshot, CancellationToken cancellationToken) =>
        WithTopAsync(async top =>
        {
            await WriteMenuAsync(top, fileSystemUuid, trialSnapshot, cancellationToken);
            return true;
        }, cancellationToken);

    /// <summary>The mounted ESPs (<c>/boot/efi</c>, <c>/boot/efi2</c>), the first disk's first (D295, D299).</summary>
    public static IReadOnlyList<string> EspMountPoints(string mounts) =>
        [.. mounts.Split('\n')
            .Select(line => line.Split(' '))
            .Where(fields => fields.Length > 2 && fields[2] == "vfat" && (fields[1] == "/boot/efi" || (fields[1].StartsWith("/boot/efi", StringComparison.Ordinal) && int.TryParse(fields[1]["/boot/efi".Length..], out _))))
            .Select(fields => fields[1])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(mount => mount.Length)
            .ThenBy(mount => mount, StringComparer.Ordinal)];

    private IReadOnlyList<string> EspMountPoints() => EspMountPoints(File.Exists(paths.Mounts) ? File.ReadAllText(paths.Mounts) : "");

    /// <summary>
    /// Sets the trial flag GRUB reads from the ESP it started from (<c>image/rootfs/etc/grub.d/01_pithyx</c>):
    /// <c>pending</c> boots the new version once; null removes it after a good health check or a rollback.
    /// </summary>
    public async Task SetTrialAsync(string? value, CancellationToken cancellationToken)
    {
        var esps = EspMountPoints();
        var temporary = new List<string>();
        if (esps.Count == 0)
        {
            // The recovery menu started from a snapshot runs without fstab: it mounts the ESPs itself, by their label.
            temporary.AddRange(await MountEspsAsync(cancellationToken));
            esps = temporary;
        }
        try
        {
            foreach (var esp in esps)
            {
                foreach (var folder in new[] { "EFI/pithyx", "EFI/BOOT" })
                {
                    var directory = Path.Combine(esp, folder);
                    if (!Directory.Exists(directory))
                    {
                        continue;
                    }
                    var file = Path.Combine(directory, "pithyx.env");
                    await RunAsync(value is null
                        ? new CommandRequest("grub-editenv", [file, "unset", "pithyx_trial"], _short)
                        : new CommandRequest("grub-editenv", [file, "set", $"pithyx_trial={value}"], _short), "bootloader", cancellationToken);
                }
            }
        }
        finally
        {
            foreach (var mountPoint in temporary)
            {
                await commands.RunAsync(new CommandRequest("umount", [mountPoint], _short), CancellationToken.None);
            }
        }
    }

    /// <summary>Mounts every partition the installer named <c>pithyx-esp</c> below <see cref="UpdatePaths.Run"/>.</summary>
    private async Task<IReadOnlyList<string>> MountEspsAsync(CancellationToken cancellationToken)
    {
        var listing = await commands.RunAsync(new CommandRequest("lsblk", ["--json", "--paths", "--list", "--output", "PATH,PARTLABEL"], _short), cancellationToken);
        if (listing.ExitCode != 0)
        {
            return [];
        }
        using var document = JsonDocument.Parse(listing.StandardOutput);
        var mounted = new List<string>();
        foreach (var device in document.RootElement.GetProperty("blockdevices").EnumerateArray())
        {
            if (device.GetProperty("partlabel").GetString() != "pithyx-esp")
            {
                continue;
            }
            var mountPoint = Path.Combine(paths.Run, $"esp{mounted.Count}");
            Directory.CreateDirectory(mountPoint);
            var result = await commands.RunAsync(new CommandRequest("mount", [device.GetProperty("path").GetString()!, mountPoint], _short), cancellationToken);
            if (result.ExitCode == 0)
            {
                mounted.Add(mountPoint);
            }
        }
        return mounted;
    }

    /// <summary>
    /// The ESP sync (D295): GRUB on every ESP, the named entry and the removable fallback, without touching the firmware's
    /// boot entries that the installer made, then the menu. Runs after every update and from apt's hook when GRUB changed.
    /// </summary>
    public async Task SyncEspsAsync(CancellationToken cancellationToken)
    {
        var esps = EspMountPoints();
        foreach (var esp in esps)
        {
            foreach (var command in EspPlans.GrubInstall(esp, chroot: null))
            {
                await RunAsync(command, "bootloader", cancellationToken);
            }
        }
        await RunAsync(new CommandRequest("update-grub", [], _long), "bootloader", cancellationToken);
        LogEspSync(esps.Count);
    }

    /// <summary>
    /// Starts <c>pithyxd update-apply</c> from a copy of the installed daemon on tmpfs as its own systemd unit, so apt can
    /// replace the installed daemon while the apply runs and the apply outlives whoever started it (D338).
    /// </summary>
    public async Task LaunchApplyAsync(UpdateApplyRequest request, CancellationToken cancellationToken)
    {
        // /run is noexec, and a copy on the system disk would end up in the snapshot: the copy gets a tmpfs of its own.
        var runner = Path.Combine(paths.Run, "runner");
        Directory.CreateDirectory(runner);
        if (!(File.Exists(paths.Mounts) ? File.ReadAllText(paths.Mounts) : "").Split('\n').Any(line => line.Split(' ').ElementAtOrDefault(1) == runner))
        {
            await RunAsync(new CommandRequest("mount", ["-t", "tmpfs", "-o", "size=1g,mode=0700,nosuid,nodev", "tmpfs", runner], _short), "snapshot", cancellationToken);
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(runner))
        {
            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }
        File.Delete(paths.ProgressFile);
        await RunAsync(new CommandRequest("cp", ["--archive", "--no-target-directory", paths.DaemonDirectory, Path.Combine(runner, "daemon")], _short), "snapshot", cancellationToken);
        await WriteJsonAsync(paths.RequestFile, request, cancellationToken);
        await RunAsync(new CommandRequest("systemd-run",
            ["--unit=pithyx-update-apply", "--collect", "--quiet", "--setenv=DOTNET_CLI_TELEMETRY_OPTOUT=1", Path.Combine(runner, "daemon", "pithyxd"), "update-apply", paths.RequestFile], _short), "snapshot", cancellationToken);
    }

    /// <summary>The apply's progress after its next change (or after a minute without one).</summary>
    public async Task<UpdateProgress?> WaitForProgressAsync(CancellationToken cancellationToken)
    {
        using var watcher = new FileSystemWatcher(paths.Run, Path.GetFileName(paths.ProgressFile))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
        };
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Changed += (_, _) => changed.TrySetResult();
        watcher.Created += (_, _) => changed.TrySetResult();
        watcher.Renamed += (_, _) => changed.TrySetResult();
        watcher.EnableRaisingEvents = true;
        var current = ReadJson<UpdateProgress>(paths.ProgressFile);
        if (current is null || current.State == "running")
        {
            await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromMinutes(1), time, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            current = ReadJson<UpdateProgress>(paths.ProgressFile) ?? current;
        }
        return current;
    }

    /// <summary>The imported pools; the health check after an update expects them back.</summary>
    public async Task<IReadOnlyList<string>> PoolsAsync(CancellationToken cancellationToken)
    {
        var result = await commands.RunAsync(new CommandRequest("zpool", ["list", "-H", "-o", "name"], _short), cancellationToken);
        return result.ExitCode == 0
            ? [.. result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal)]
            : [];
    }

    public Task RebootAsync() => commands.RunAsync(new CommandRequest("systemctl", ["reboot"], _short), CancellationToken.None);

    /// <summary>The running kernel's release, as <c>uname -r</c> shows it.</summary>
    public static string KernelRelease() =>
        File.Exists("/proc/sys/kernel/osrelease") ? File.ReadAllText("/proc/sys/kernel/osrelease").Trim() : "";

    /// <summary>Runs a command and turns a failure into <c>update.apply.failed</c> with the step and the command.</summary>
    public async Task<CommandResult> RunAsync(CommandRequest request, string step, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await commands.RunAsync(request, cancellationToken);
        if (result.ExitCode != 0)
        {
            var program = request.FileName == "env" ? request.Arguments.FirstOrDefault(argument => !argument.Contains('=', StringComparison.Ordinal)) ?? "env" : request.FileName;
            throw new PithyxException(ErrorCodes.UpdateApplyFailed, new Dictionary<string, object?>
            {
                ["step"] = step,
                ["command"] = program,
                ["exitCode"] = result.ExitCode,
            });
        }
        return result;
    }

    public static T? ReadJson<T>(string path)
        where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(value, Json), cancellationToken);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "System snapshot {Snapshot} taken ({Reason})")]
    private partial void LogSnapshot(string snapshot, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "System rolled back to snapshot {Snapshot} ({State})")]
    private partial void LogRollback(string snapshot, string state);

    [LoggerMessage(Level = LogLevel.Information, Message = "GRUB synced to {Count} ESPs")]
    private partial void LogEspSync(int count);
}

/// <summary>The GRUB installs of an ESP, shared by the installer and the ESP sync after updates (D295, D299).</summary>
public static class EspPlans
{
    private static readonly TimeSpan _timeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// GRUB into the ESP at <paramref name="mountPoint"/>: the removable fallback <c>\EFI\BOOT\BOOTX64.EFI</c> and the
    /// named <c>\EFI\pithyx</c>, both without boot entries (the installer sets those once). With <paramref name="chroot"/>
    /// the commands run in the target of an installation.
    /// </summary>
    public static IReadOnlyList<CommandRequest> GrubInstall(string mountPoint, string? chroot)
    {
        CommandRequest Install(params string[] extra)
        {
            List<string> arguments = ["--target=x86_64-efi", $"--efi-directory={mountPoint}", "--bootloader-id=pithyx", .. extra];
            return chroot is null
                ? new CommandRequest("grub-install", arguments, _timeout)
                : new CommandRequest("chroot", [chroot, "grub-install", .. arguments], _timeout);
        }
        return [Install("--removable", "--no-nvram"), Install("--no-nvram")];
    }
}
