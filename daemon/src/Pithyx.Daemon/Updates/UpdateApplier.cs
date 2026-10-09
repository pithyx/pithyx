using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Updates;

/// <summary>The apt commands of an update, as pure functions (D338).</summary>
public static class UpdatePlans
{
    private static readonly TimeSpan _long = TimeSpan.FromMinutes(30);

    public static readonly IReadOnlyList<string> DpkgOptions =
        ["-o", "Dpkg::Options::=--force-confdef", "-o", "Dpkg::Options::=--force-confold"];

    /// <summary>The update file's packages as the only apt source: its own list and lists folder, nothing from Debian.</summary>
    public static IReadOnlyList<string> LocalSource(string sourceList, string listsDirectory, string emptyDirectory) =>
        ["-o", $"Dir::Etc::SourceList={sourceList}", "-o", $"Dir::Etc::SourceParts={emptyDirectory}", "-o", $"Dir::State::Lists={listsDirectory}"];

    public static string SourceListLine(string updateDirectory) => $"deb [trusted=yes] file:{Path.Combine(updateDirectory, "debs")} ./\n";

    /// <summary>
    /// Installs <c>pithyx</c> and <c>pithyx-kernel</c> of the update file non-interactively, changed configuration files
    /// kept as the administrator left them.
    /// </summary>
    public static IReadOnlyList<CommandRequest> InstallLocal(IReadOnlyList<string> source, string debianVersion) =>
    [
        Apt([.. source, "update"]),
        Apt([.. source, .. DpkgOptions, "install", "--yes", "--no-install-recommends", $"pithyx={debianVersion}", $"pithyx-kernel={debianVersion}"]),
    ];

    /// <summary>The Debian updates from Debian's own sources; the pinning keeps the kernel and ZFS where they are.</summary>
    public static IReadOnlyList<CommandRequest> UpgradeDebian() =>
    [
        Apt(["update"]),
        Apt([.. DpkgOptions, "upgrade", "--yes"]),
    ];

    /// <summary>Reads Debian's lists and simulates an upgrade for the daily check; installs nothing.</summary>
    public static IReadOnlyList<CommandRequest> CheckDebian() =>
    [
        Apt(["update"]),
        new("apt-get", ["--simulate", "upgrade"], TimeSpan.FromMinutes(5)),
    ];

    private static CommandRequest Apt(IReadOnlyList<string> arguments) =>
        new("env", ["DEBIAN_FRONTEND=noninteractive", "apt-get", .. arguments], _long);
}

/// <summary>
/// Applies a verified update in its own process, started by the daemon or the recovery menu from a copy of the daemon in
/// <c>/run/pithyx/update/runner</c>, so apt can replace the installed daemon underneath (D338): the snapshot, the
/// packages, optionally the Debian updates, GRUB on every ESP, the trial flag, the restart. A failure after the
/// snapshot rolls back to it and restarts into the previous version.
/// </summary>
public sealed partial class UpdateApplier(UpdateSystem system, TimeProvider time, ILogger<UpdateApplier> logger)
{
    /// <summary>Room for the snapshot's changed blocks and apt beside the packages themselves.</summary>
    public const long SpareBytes = 1L << 30;

    public async Task<UpdateProgress> RunAsync(UpdateApplyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var manifestPath = Path.Combine(request.Directory, UpdateFile.ManifestFile);
        var manifest = UpdateSystem.ReadJson<UpdateManifest>(manifestPath)
            ?? throw new PithyxException(ErrorCodes.UpdateFileNotFound);
        var debianVersion = manifest.Packages.FirstOrDefault(package => package.Name == "pithyx")?.Version
            ?? throw new PithyxException(ErrorCodes.UpdateFileInvalid);

        await ReportAsync(request, "snapshot", "running", null, cancellationToken);
        SystemLayout layout;
        SnapshotInfo snapshot;
        IReadOnlyList<string> pools;
        try
        {
            layout = await system.RequireLayoutAsync(cancellationToken);
            var needed = manifest.Files.Sum(file => file.Size) * 2 + SpareBytes;
            var free = new DriveInfo("/").AvailableFreeSpace;
            if (free < needed)
            {
                throw new PithyxException(ErrorCodes.UpdateSpaceInsufficient, new Dictionary<string, object?> { ["free"] = free, ["needed"] = needed });
            }
            pools = await system.PoolsAsync(cancellationToken);
            snapshot = await system.TakeSnapshotAsync($"update to {manifest.Version}", layout.FileSystemUuid!, cancellationToken);
        }
        catch (PithyxException exception)
        {
            LogFailed(exception, "snapshot");
            return await ReportAsync(request, "snapshot", "failed", exception, cancellationToken);
        }

        var step = "install";
        try
        {
            await ReportAsync(request, step, "running", null, cancellationToken);
            var lists = Path.Combine(system.Paths.Run, "apt-lists");
            var empty = Path.Combine(system.Paths.Run, "apt-empty");
            Directory.CreateDirectory(Path.Combine(lists, "partial"));
            Directory.CreateDirectory(empty);
            var sourceList = Path.Combine(system.Paths.Run, "pithyx-update.list");
            await File.WriteAllTextAsync(sourceList, UpdatePlans.SourceListLine(request.Directory), cancellationToken);
            foreach (var command in UpdatePlans.InstallLocal(UpdatePlans.LocalSource(sourceList, lists, empty), debianVersion))
            {
                await system.RunAsync(command, step, cancellationToken);
            }

            if (request.IncludeDebian)
            {
                step = "debian";
                await ReportAsync(request, step, "running", null, cancellationToken);
                foreach (var command in UpdatePlans.UpgradeDebian())
                {
                    await system.RunAsync(command, step, cancellationToken);
                }
            }

            step = "bootloader";
            await ReportAsync(request, step, "running", null, cancellationToken);
            await system.SyncEspsAsync(cancellationToken);
            await UpdateSystem.WriteJsonAsync(system.Paths.TrialFile,
                new UpdateTrial(request.HistoryId, request.FromVersion, request.ToVersion, snapshot.Name, pools, time.GetUtcNow()), cancellationToken);
            await system.WriteMenuAsync(layout.FileSystemUuid!, snapshot.Name, cancellationToken);
            await system.SetTrialAsync("pending", cancellationToken);
        }
        catch (Exception exception) when (exception is PithyxException or IOException)
        {
            var problem = exception as PithyxException ?? new PithyxException(ErrorCodes.UpdateApplyFailed, new Dictionary<string, object?> { ["step"] = step, ["command"] = "io", ["exitCode"] = -1 });
            LogFailed(problem, step);
            var outcome = new UpdateOutcome(request.HistoryId, "rolled_back", problem.Code, problem.Parameters, time.GetUtcNow(), request.History);
            await system.RollbackAsync(snapshot.Name, layout.FileSystemUuid!, outcome, CancellationToken.None);
            var rolledBack = await ReportAsync(request, step, "rolled_back", problem, CancellationToken.None);
            await system.RebootAsync();
            return rolledBack;
        }

        var restarting = await ReportAsync(request, "restart", "restarting", null, cancellationToken);
        LogRestarting(manifest.Version, snapshot.Name);
        await system.RebootAsync();
        return restarting;
    }

    private async Task<UpdateProgress> ReportAsync(UpdateApplyRequest request, string step, string state, PithyxException? error, CancellationToken cancellationToken)
    {
        var progress = new UpdateProgress(request.HistoryId, step, state, error?.Code, error?.Parameters);
        await UpdateSystem.WriteJsonAsync(system.Paths.ProgressFile, progress, cancellationToken);
        return progress;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The update failed in step {Step}")]
    private partial void LogFailed(Exception exception, string step);

    [LoggerMessage(Level = LogLevel.Information, Message = "Update to {Version} installed after snapshot {Snapshot}; restarting for the health check")]
    private partial void LogRestarting(string version, string snapshot);
}

/// <summary>
/// The health check after an update's restart (<c>pithyx-update-health.service</c>, D338), for at most five minutes:
/// the daemon runs, its database has every migration of this version, the API and the desktop answer, and every pool
/// that was imported before the update is imported again. A good check clears the trial flag and reports success; a
/// failed one rolls back to the snapshot and restarts. Pools are never upgraded.
/// </summary>
public sealed partial class UpdateHealth(UpdateSystem system, ICommandRunner commands, HttpClient http, TimeProvider time, ILogger<UpdateHealth> logger)
{
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(5);

    /// <summary>Returns the name of the check that failed last, or null when every check passed in time.</summary>
    public async Task<string?> CheckAsync(UpdateTrial trial, Func<CancellationToken, Task<bool>> migrationsCurrent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trial);
        ArgumentNullException.ThrowIfNull(migrationsCurrent);
        var deadline = time.GetUtcNow() + Limit;
        string? failed;
        while (true)
        {
            failed = await FirstFailureAsync(trial, migrationsCurrent, cancellationToken);
            if (failed is null || time.GetUtcNow() >= deadline)
            {
                break;
            }
            await Task.Delay(_interval, time, cancellationToken);
        }
        LogHealth(trial.ToVersion, failed ?? "none");
        return failed;
    }

    private async Task<string?> FirstFailureAsync(UpdateTrial trial, Func<CancellationToken, Task<bool>> migrationsCurrent, CancellationToken cancellationToken)
    {
        var active = await commands.RunAsync(new CommandRequest("systemctl", ["is-active", "pithyx.service"], TimeSpan.FromSeconds(30)), cancellationToken);
        if (active.ExitCode != 0)
        {
            return "service";
        }
        if (!await migrationsCurrent(cancellationToken))
        {
            return "migrations";
        }
        if (!await AnswersAsync("/api/health", "\"ok\"", cancellationToken))
        {
            return "api";
        }
        if (!await AnswersAsync("/", "<html", cancellationToken))
        {
            return "desktop";
        }
        var pools = await system.PoolsAsync(cancellationToken);
        return trial.Pools.All(pool => pools.Contains(pool)) ? null : "pools";
    }

    private async Task<bool> AnswersAsync(string path, string expected, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await http.GetAsync(new Uri($"http://127.0.0.1:7484{path}"), timeout.Token);
            return response.IsSuccessStatusCode
                && (await response.Content.ReadAsStringAsync(timeout.Token)).Contains(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>The trial's verdict: success clears the GRUB flag; a failure restores the snapshot and restarts.</summary>
    public async Task FinishAsync(UpdateTrial trial, string? failed, IReadOnlyList<UpdateHistoryEntry> history, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trial);
        if (failed is null)
        {
            await system.SetTrialAsync(null, cancellationToken);
            var layout = await system.LayoutAsync(cancellationToken);
            if (layout.FileSystemUuid is { } uuid)
            {
                await system.WriteMenuAsync(uuid, trialSnapshot: null, cancellationToken);
            }
            await UpdateSystem.WriteJsonAsync(system.Paths.ResultFile,
                new UpdateOutcome(trial.HistoryId, "succeeded", null, null, time.GetUtcNow(), history), cancellationToken);
            File.Delete(system.Paths.TrialFile);
            return;
        }
        var required = await system.RequireLayoutAsync(cancellationToken);
        var outcome = new UpdateOutcome(trial.HistoryId, "rolled_back", ErrorCodes.UpdateApplyFailed,
            new Dictionary<string, object?> { ["step"] = "health", ["command"] = failed, ["exitCode"] = 1 }, time.GetUtcNow(), history);
        await system.RollbackAsync(trial.Snapshot, required.FileSystemUuid!, outcome, cancellationToken);
        await system.RebootAsync();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Health check after the update to {Version}: failed check {Failed}")]
    private partial void LogHealth(string version, string failed);
}
