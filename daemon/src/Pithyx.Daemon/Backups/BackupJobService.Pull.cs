using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Files;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Storage.Snapshots;
using Pithyx.Daemon.Storage.Zfs;

namespace Pithyx.Daemon.Backups;

/// <summary>
/// Server backups (#396, D420): rsync over SSH pulls folders of a Linux server into a dataset of the job's own, made
/// as a folder of a shared folder, and every successful run ends with a ZFS snapshot of it as the run's version. The
/// retention keeps versions with restic's rules (<see cref="BackupKeepRules"/>); a protected version also carries a
/// ZFS hold. The live folder is always the newest state, and Files' previous versions restore from the snapshots.
/// </summary>
public sealed partial class BackupJobService
{
    private const string PullKind = "pull";

    /// <summary>Saves a new server backup after its dataset was made in the shared folder.</summary>
    public async Task<BackupJobCreated> CreatePullAsync(ClaimsPrincipal caller, BackupPullCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = BackupJobRules.CheckPull(request.Settings);
        var pull = BackupJobRules.CheckPullSettings(request.Pull);
        var folder = BackupJobRules.CheckFolder(pull.Folder);
        await targets.EndpointAsync(request.ServerId, cancellationToken, BackupTargetPurpose.Server);
        var (share, parent, parentMount) = await DestinationParentAsync(pull.ShareId, cancellationToken);
        var dataset = ZfsPlans.PullDataset(parent, folder);
        if (Path.Exists(Path.Combine(parentMount, folder))
            || (await commands.RunAsync(new CommandRequest("zfs", ["list", "-H", "-o", "name", dataset]), cancellationToken)).ExitCode == 0)
        {
            throw DestinationInvalid(folder, share, "exists");
        }
        var now = SessionService.Format(time.GetUtcNow());
        long id;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            await EnsureNameFreeAsync(connection, settings.Name, null, cancellationToken);
            try
            {
                id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    """
                    INSERT INTO backup_jobs (name, kind, target_id, repository, repository_key, sources, excludes, schedule, enabled,
                        keep_last, keep_hourly, keep_daily, keep_weekly, keep_monthly, keep_yearly, check_schedule, check_read_percent,
                        upload_limit_kib, download_limit_kib, overdue_hours, created_at, updated_at,
                        destination_share_id, destination_folder, destination_dataset, remote_paths, pull_delete, pull_owners)
                    VALUES (@Name, 'pull', @serverId, '', '', '[]', @excludes, @Schedule, @Enabled,
                        @keepLast, @keepHourly, @keepDaily, @keepWeekly, @keepMonthly, @keepYearly, NULL, 0,
                        0, @DownloadLimitKib, @OverdueHours, @now, @now,
                        @shareId, @folder, @dataset, @paths, @delete, @owners)
                    RETURNING id
                    """,
                    Parameters(settings, new
                    {
                        serverId = request.ServerId,
                        now,
                        shareId = pull.ShareId,
                        folder,
                        dataset,
                        paths = JsonSerializer.Serialize(pull.Paths),
                        delete = pull.Delete ? 1 : 0,
                        owners = Text(pull.Owners),
                    }),
                    cancellationToken: cancellationToken));
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                throw NameTaken(settings.Name);
            }
        }
        try
        {
            await MakeDestinationAsync(parentMount, parent, folder, id, share, cancellationToken);
        }
        catch
        {
            await using var connection = await connections.OpenAsync(CancellationToken.None);
            await connection.ExecuteAsync("DELETE FROM backup_jobs WHERE id = @id", new { id });
            throw;
        }
        await audit.RecordAsync(caller, JobCreated, settings.Name, new Dictionary<string, string>
        {
            ["server"] = request.ServerId.ToString(CultureInfo.InvariantCulture),
            ["destination"] = share + "/" + folder,
            ["paths"] = string.Join(", ", pull.Paths),
        }, cancellationToken);
        LogPullCreated(id, dataset);
        Changed?.Invoke();
        return new BackupJobCreated(await GetAsync(id, cancellationToken), null);
    }

    /// <summary>Changes what a server backup pulls and when; its server and destination stay.</summary>
    public async Task<BackupJob> UpdatePullAsync(long id, BackupPullUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await connections.OpenAsync(cancellationToken);
        if ((await RowAsync(connection, id, cancellationToken)).Kind != PullKind)
        {
            throw Unsupported("edit");
        }
        var settings = BackupJobRules.CheckPull(request.Settings);
        var pull = BackupJobRules.CheckPullSettings(request.Pull);
        await EnsureNameFreeAsync(connection, settings.Name, id, cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE backup_jobs SET name = @Name, excludes = @excludes, schedule = @Schedule, enabled = @Enabled,
                    keep_last = @keepLast, keep_hourly = @keepHourly, keep_daily = @keepDaily, keep_weekly = @keepWeekly,
                    keep_monthly = @keepMonthly, keep_yearly = @keepYearly, download_limit_kib = @DownloadLimitKib,
                    overdue_hours = @OverdueHours, remote_paths = @paths, pull_delete = @delete, pull_owners = @owners, updated_at = @now
                WHERE id = @id
                """,
                Parameters(settings, new
                {
                    id,
                    now = SessionService.Format(time.GetUtcNow()),
                    paths = JsonSerializer.Serialize(pull.Paths),
                    delete = pull.Delete ? 1 : 0,
                    owners = Text(pull.Owners),
                }),
                cancellationToken: cancellationToken));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw NameTaken(settings.Name);
        }
        Changed?.Invoke();
        return await ViewAsync(connection, await RowAsync(connection, id, cancellationToken), cancellationToken);
    }

    /// <summary>The shared folder a server backup's folder goes into: its name, dataset and mountpoint, which must be the shared folder itself.</summary>
    private async Task<(string Share, string Dataset, string Mountpoint)> DestinationParentAsync(long shareId, CancellationToken cancellationToken)
    {
        var root = (await shares.ListIndexRootsAsync(cancellationToken)).FirstOrDefault(root => root.Id == shareId) ?? throw BackupJobRules.Invalid("destination");
        var name = (await shares.ListPathsAsync(cancellationToken)).Where(share => share.Id == shareId).Select(share => DisplayName(share.Name)).FirstOrDefault() ?? "";
        if (root.Dataset is null)
        {
            throw DestinationInvalid("", name, "unsupported");
        }
        if (!root.Available)
        {
            throw DestinationUnavailable(name, "unavailable");
        }
        var mountpoint = await MountpointAsync(root.Dataset, cancellationToken) ?? throw DestinationUnavailable(name, "unavailable");
        // A child dataset mounts below its parent's mountpoint, so the shared folder must be the dataset's root.
        if (!string.Equals(root.Path.TrimEnd('/'), mountpoint.TrimEnd('/'), StringComparison.Ordinal))
        {
            throw DestinationInvalid("", name, "unsupported");
        }
        return (name, root.Dataset, mountpoint);
    }

    /// <summary>Makes the server backup's dataset and gives its root the shared folder's owner, mode and ACL.</summary>
    private async Task MakeDestinationAsync(string parentMount, string parent, string folder, long id, string share, CancellationToken cancellationToken)
    {
        var dataset = ZfsPlans.PullDataset(parent, folder);
        var created = await commands.RunAsync(ZfsPlans.CreatePullDataset(parent, folder, id), cancellationToken);
        if (created.ExitCode != 0)
        {
            LogDestinationFailed(dataset, created.StandardError);
            throw DestinationInvalid(folder, share, "failed");
        }
        var mountpoint = await MountpointAsync(dataset, cancellationToken);
        CommandRequest[] steps = mountpoint is null
            ? []
            :
            [
                new("chown", ["--reference=" + parentMount, "--", mountpoint]),
                new("chmod", ["--reference=" + parentMount, "--", mountpoint]),
                new("getfacl", ["--omit-header", "--numeric", "--absolute-names", "--", parentMount], PipeTo: new CommandRequest("setfacl", ["--set-file=-", "--", mountpoint])),
            ];
        foreach (var step in steps)
        {
            var result = await commands.RunAsync(step, cancellationToken);
            if (result.ExitCode != 0)
            {
                LogDestinationFailed(dataset, result.StandardError);
                mountpoint = null;
                break;
            }
        }
        if (mountpoint is null)
        {
            await commands.RunAsync(ZfsPlans.DestroyPullDataset(dataset), CancellationToken.None);
            throw DestinationInvalid(folder, share, "failed");
        }
    }

    /// <summary>Destroys a server backup's dataset with its versions, after checking it is the job's.</summary>
    private async Task DestroyPullDataAsync(JobRow row, CancellationToken cancellationToken)
    {
        var dataset = row.DestinationDataset!;
        var owner = await commands.RunAsync(new CommandRequest("zfs", ["get", "-H", "-o", "value", ZfsPlans.BackupJobProperty, dataset]), cancellationToken);
        if (owner.ExitCode != 0)
        {
            // Gone already.
            return;
        }
        if (owner.StandardOutput.Trim() != row.Id.ToString(CultureInfo.InvariantCulture))
        {
            throw DestinationInvalid(row.DestinationFolder ?? "", dataset, "failed");
        }
        foreach (var snapshot in await PullSnapshotsAsync(dataset, cancellationToken))
        {
            if (snapshot.Holds > 0)
            {
                await commands.RunAsync(SnapshotPlans.Release(dataset, snapshot.Name), cancellationToken);
            }
        }
        var result = await commands.RunAsync(ZfsPlans.DestroyPullDataset(dataset), cancellationToken);
        if (result.ExitCode != 0)
        {
            LogDestinationFailed(dataset, result.StandardError);
            throw DestinationInvalid(row.DestinationFolder ?? "", dataset, "failed");
        }
    }

    /// <summary>One pull: rsync into the dataset, then a snapshot as the version, then the retention.</summary>
    private async Task<(BackupRunState State, PithyxException? Error)> PullAsync(JobRow row, ResticReport report, JobContext context)
    {
        var settings = Settings(row);
        var pull = PullSettings(row)!;
        var dataset = row.DestinationDataset!;
        var mountpoint = await PullMountpointAsync(row, context.Cancellation);
        var (endpoint, _) = await targets.EndpointAsync(row.TargetId, context.Cancellation, BackupTargetPurpose.Server);
        var rsync = new RsyncReport();
        CommandResult result;
        using (var root = LinuxFs.OpenRoot(mountpoint))
        using (var folder = new KeyFolder())
        {
            // rsync writes through the daemon's handle, so a folder renamed or replaced meanwhile cannot redirect it.
            var destination = string.Create(CultureInfo.InvariantCulture, $"/proc/{Environment.ProcessId}/fd/{root.DangerousGetHandle()}/");
            var arguments = BackupRsync.PullArguments(BackupRsync.Shell(folder, endpoint), pull.Paths, destination, pull.Delete, pull.Owners, settings.Excludes ?? [], settings.DownloadLimitKib);
            result = await commands.RunAsync(
                new CommandRequest("env", arguments, BackupRestic.BackupTimeout, OutputLine: line =>
                {
                    var before = rsync.Progress;
                    BackupRsync.ReadLine(line, rsync);
                    if (rsync.Progress is { } progress && !ReferenceEquals(progress, before))
                    {
                        context.Report(progress);
                    }
                }, Interruptible: true),
                context.Cancellation);
        }
        BackupRsync.ReadErrors(result.StandardError, rsync);
        report.Files = rsync.Files;
        report.Bytes = rsync.Bytes;
        report.Warnings = rsync.UnreadableCount;
        report.WarningItems.AddRange(rsync.Unreadable.Select(item => new ResticUnreadable(item.Item, item.Reason switch
        {
            "permission" => "permission denied",
            "missing" => "no such file",
            _ => item.Reason,
        })));
        if (BackupRsync.Failure(result.ExitCode, result.StandardError, string.Join(", ", pull.Paths)) is { } failure)
        {
            LogRsyncFailed(row.Id, result.ExitCode, result.StandardError.Length > 4000 ? result.StandardError[..4000] : result.StandardError);
            return (BackupRunState.Failed, failure);
        }
        await targets.RecordConnectionAsync(row.TargetId, CancellationToken.None);
        if (rsync.UnreadableCount > 0)
        {
            LogUnreadable(row.Id, rsync.UnreadableCount, string.Join(", ", rsync.Unreadable.Take(20).Select(item => item.Item)));
        }
        var existing = await PullSnapshotsAsync(dataset, context.Cancellation);
        var stamp = time.GetUtcNow();
        var name = SnapshotNames.For(SnapshotKinds.Pull, stamp);
        while (existing.Any(snapshot => snapshot.Name == name))
        {
            stamp = stamp.AddSeconds(1);
            name = SnapshotNames.For(SnapshotKinds.Pull, stamp);
        }
        if ((await commands.RunAsync(SnapshotPlans.Take(dataset, name), CancellationToken.None)).ExitCode != 0)
        {
            return (BackupRunState.Failed, new PithyxException(ErrorCodes.BackupDestinationUnavailable, new Dictionary<string, object?> { ["reason"] = "snapshot", ["path"] = row.DestinationFolder }));
        }
        report.Snapshot = name;
        report.AddedBytes = await SyncPullVersionsAsync(row, settings, pull, name, rsync, CancellationToken.None);
        report.RepositoryBytes = await DatasetBytesAsync(dataset, "used", CancellationToken.None);
        var state = result.ExitCode is BackupRsync.Partial or BackupRsync.Vanished || rsync.UnreadableCount > 0 ? BackupRunState.Warning : BackupRunState.Succeeded;
        return (state, null);
    }

    /// <summary>
    /// Lists the dataset's versions, applies the retention to them (destroying the versions it does not keep and no hold
    /// protects) and keeps the rest in <c>backup_versions</c>, the new one with what rsync reported. Returns what the
    /// new version added to the dataset.
    /// </summary>
    private async Task<long?> SyncPullVersionsAsync(JobRow row, BackupJobSettings settings, BackupPullSettings pull, string made, RsyncReport rsync, CancellationToken cancellationToken)
    {
        var dataset = row.DestinationDataset!;
        var snapshots = await PullSnapshotsAsync(dataset, cancellationToken);
        var keep = BackupKeepRules.Keep(snapshots.Select(snapshot => new KeepCandidate(snapshot.Name, snapshot.Created, snapshot.Holds > 0)), settings.Retention, TimeZoneInfo.Local);
        var kept = new List<PullSnapshot>();
        foreach (var snapshot in snapshots)
        {
            if (keep.ContainsKey(snapshot.Name) || snapshot.Holds > 0 || snapshot.Name == made)
            {
                kept.Add(snapshot);
                continue;
            }
            var destroyed = await commands.RunAsync(SnapshotPlans.Destroy(dataset, snapshot.Name), cancellationToken);
            if (destroyed.ExitCode != 0)
            {
                LogSnapshotLeft(dataset + "@" + snapshot.Name, destroyed.StandardError);
                kept.Add(snapshot);
            }
        }
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var snapshot in kept)
        {
            var isNew = snapshot.Name == made;
            await connection.ExecuteAsync(
                """
                INSERT INTO backup_versions (job_id, snapshot, origin, time, files, bytes, added_bytes, files_added, files_changed,
                    files_removed, protected, keep, changes, paths)
                VALUES (@jobId, @name, @name, @time, @files, @bytes, @written, @added, @changed, @removed, @protect, @keep, @changes, @paths)
                ON CONFLICT (job_id, origin) DO UPDATE SET
                    protected = excluded.protected,
                    keep = excluded.keep,
                    added_bytes = coalesce(added_bytes, excluded.added_bytes)
                """,
                new
                {
                    jobId = row.Id,
                    name = snapshot.Name,
                    time = SessionService.Format(snapshot.Created),
                    files = isNew ? rsync.Files : null,
                    bytes = isNew ? rsync.Bytes : null,
                    written = snapshot.Written,
                    added = isNew ? rsync.Added : (long?)null,
                    changed = isNew ? rsync.Changed : (long?)null,
                    removed = isNew ? rsync.Removed : (long?)null,
                    protect = snapshot.Holds > 0 ? 1 : 0,
                    keep = KeepJson(keep.GetValueOrDefault(snapshot.Name) ?? (snapshot.Holds > 0 ? [BackupKeepReason.Protected] : [])),
                    changes = isNew ? JsonSerializer.Serialize(rsync.Changes.Select(change => new { path = change.Path, change = Text(change.Change) }), _json) : null,
                    paths = isNew ? JsonSerializer.Serialize(pull.Paths) : null,
                },
                transaction);
        }
        await connection.ExecuteAsync(
            "DELETE FROM backup_versions WHERE job_id = @jobId AND origin NOT IN (SELECT value FROM json_each(@origins))",
            new { jobId = row.Id, origins = JsonSerializer.Serialize(kept.Select(snapshot => snapshot.Name)) },
            transaction);
        await transaction.CommitAsync(cancellationToken);
        return kept.FirstOrDefault(snapshot => snapshot.Name == made)?.Written;
    }

    /// <summary>Holds or releases a server backup's version, so the retention keeps it.</summary>
    private async Task<BackupVersion> ProtectPullAsync(ClaimsPrincipal caller, JobRow row, VersionRow version, bool protect, CancellationToken cancellationToken)
    {
        var dataset = row.DestinationDataset!;
        await PullMountpointAsync(row, cancellationToken);
        var snapshot = (await PullSnapshotsAsync(dataset, cancellationToken)).FirstOrDefault(snapshot => snapshot.Name == version.Snapshot)
            ?? throw new PithyxException(ErrorCodes.BackupVersionNotFound, new Dictionary<string, object?> { ["id"] = version.Id });
        if ((snapshot.Holds > 0) != protect)
        {
            var result = await commands.RunAsync(protect ? SnapshotPlans.Hold(dataset, snapshot.Name) : SnapshotPlans.Release(dataset, snapshot.Name), cancellationToken);
            if (result.ExitCode != 0)
            {
                LogDestinationFailed(dataset, result.StandardError);
                throw DestinationUnavailable(row.DestinationFolder ?? "", "dataset");
            }
        }
        var keep = Reasons(version.Keep).Where(reason => reason != BackupKeepReason.Protected).ToList();
        if (protect)
        {
            keep.Add(BackupKeepReason.Protected);
        }
        await using var connection = await connections.OpenAsync(CancellationToken.None);
        await connection.ExecuteAsync(
            "UPDATE backup_versions SET protected = @protect, keep = @keep WHERE id = @versionId",
            new { versionId = version.Id, protect = protect ? 1 : 0, keep = KeepJson(keep) });
        await audit.RecordAsync(caller, protect ? VersionProtected : VersionReleased, row.Name, new Dictionary<string, string>
        {
            ["version"] = snapshot.Name,
            ["time"] = version.Time,
        }, CancellationToken.None);
        LogVersionProtection(row.Id, snapshot.Name, protect);
        return View(await VersionRowAsync(connection, row.Id, version.Id, CancellationToken.None));
    }

    /// <summary>The mountpoint of a server backup's dataset, after checking it is mounted and the job's.</summary>
    private async Task<string> PullMountpointAsync(JobRow row, CancellationToken cancellationToken)
    {
        var dataset = row.DestinationDataset!;
        var owner = await commands.RunAsync(new CommandRequest("zfs", ["get", "-H", "-o", "value", ZfsPlans.BackupJobProperty, dataset]), cancellationToken);
        if (owner.ExitCode != 0 || owner.StandardOutput.Trim() != row.Id.ToString(CultureInfo.InvariantCulture))
        {
            throw DestinationUnavailable(row.DestinationFolder ?? "", "dataset");
        }
        return await MountpointAsync(dataset, cancellationToken) ?? throw DestinationUnavailable(row.DestinationFolder ?? "", "unavailable");
    }

    private sealed record PullSnapshot(string Name, DateTimeOffset Created, long? Written, int Holds);

    /// <summary>The dataset's versions: its snapshots of the kind <see cref="SnapshotKinds.Pull"/>.</summary>
    private async Task<List<PullSnapshot>> PullSnapshotsAsync(string dataset, CancellationToken cancellationToken)
    {
        var listing = await commands.RunAsync(new CommandRequest("zfs", ["list", "-H", "-p", "-o", "name,creation,written,userrefs", "-t", "snapshot", "-d", "1", dataset]), cancellationToken);
        return listing.ExitCode != 0 ? [] : [.. BackupRsync.ParseSnapshots(listing.StandardOutput, dataset).Select(item => new PullSnapshot(item.Name, item.Created, item.Written, item.Holds))];
    }

    private async Task<long?> DatasetBytesAsync(string dataset, string property, CancellationToken cancellationToken)
    {
        var result = await commands.RunAsync(new CommandRequest("zfs", ["get", "-H", "-p", "-o", "value", property, dataset]), cancellationToken);
        return result.ExitCode == 0 && long.TryParse(result.StandardOutput.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ? bytes : null;
    }

    private static BackupPullSettings? PullSettings(JobRow row) => row.Kind != PullKind
        ? null
        : new BackupPullSettings(
            row.DestinationShareId ?? 0,
            row.DestinationFolder ?? "",
            JsonSerializer.Deserialize<List<string>>(row.RemotePaths ?? "[]") ?? [],
            row.PullDelete == 1,
            row.PullOwners == "box" ? BackupPullOwners.Box : BackupPullOwners.Server);

    private static PithyxException Unsupported(string action) =>
        new(ErrorCodes.BackupJobUnsupported, new Dictionary<string, object?> { ["action"] = action });

    private static PithyxException DestinationInvalid(string path, string share, string reason) =>
        new(ErrorCodes.BackupDestinationInvalid, new Dictionary<string, object?> { ["path"] = path, ["share"] = share, ["reason"] = reason });

    private static PithyxException DestinationUnavailable(string path, string reason) =>
        new(ErrorCodes.BackupDestinationUnavailable, new Dictionary<string, object?> { ["path"] = path, ["reason"] = reason });

    [LoggerMessage(Level = LogLevel.Information, Message = "Server backup {JobId} added with dataset {Dataset}")]
    private partial void LogPullCreated(long jobId, string dataset);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dataset {Dataset} of a server backup could not be set up or removed: {Reason}")]
    private partial void LogDestinationFailed(string dataset, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "rsync failed for server backup {JobId} with exit code {ExitCode}: {StandardError}")]
    private partial void LogRsyncFailed(long jobId, int exitCode, string standardError);
}
