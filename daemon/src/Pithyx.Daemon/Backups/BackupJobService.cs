using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Pithyx.Daemon.Apps.Docker;
using Pithyx.Daemon.Audit;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Notifications;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage.Snapshots;
using Pithyx.Daemon.Users;

namespace Pithyx.Daemon.Backups;

/// <summary>
/// Versioned backup jobs (#394, D415): folders of shared folders and homes, backed up with restic over SFTP into a
/// repository of the job's own on a target. A run takes a ZFS snapshot of every source dataset, mounts each over its
/// live folder in a private mount namespace, so restic stores the live paths with owners, modes, ACLs and times, and
/// drops the snapshots afterwards; a successful run applies the retention. Runs and checks are box jobs on the job
/// board in a slot of their own, one at a time per job, and report their outcome as events of their own.
/// </summary>
public sealed partial class BackupJobService(
    SqliteConnectionFactory connections,
    BackupTargetService targets,
    BackupRestic restic,
    ShareService shares,
    ICommandRunner commands,
    JobService jobs,
    NotificationService notifications,
    AuditLog audit,
    IHostApplicationLifetime lifetime,
    TimeProvider time,
    ILogger<BackupJobService> logger)
{
    public const string RunKind = "backup.run";
    public const string CheckKind = "backup.check";

    public const string JobCreated = "backup.job.created";
    public const string JobDeleted = "backup.job.deleted";

    public const string RunFailedCode = "backup.alert.runFailed";
    public const string RunWarningCode = "backup.alert.runWarning";
    public const string CheckFailedCode = "backup.alert.checkFailed";
    public const string OverdueCode = "backup.alert.overdue";
    public const string HostKeyChangedCode = "backup.alert.hostKeyChanged";

    /// <summary>Runs kept per job; older ones are deleted.</summary>
    public const int KeptRuns = 500;

    private const string SelectJob =
        """
        SELECT id AS Id, name AS Name, target_id AS TargetId, repository AS Repository, repository_key AS RepositoryKey,
            sources AS Sources, excludes AS Excludes, schedule AS Schedule, enabled AS Enabled,
            keep_last AS KeepLast, keep_hourly AS KeepHourly, keep_daily AS KeepDaily, keep_weekly AS KeepWeekly,
            keep_monthly AS KeepMonthly, keep_yearly AS KeepYearly, check_schedule AS CheckSchedule,
            check_read_percent AS CheckReadPercent, upload_limit_kib AS UploadLimitKib, download_limit_kib AS DownloadLimitKib,
            overdue_hours AS OverdueHours, overdue_notified_at AS OverdueNotifiedAt, created_at AS CreatedAt
        FROM backup_jobs
        """;

    private const string SelectRun =
        """
        SELECT id AS Id, job_id AS JobId, kind AS Kind, trigger AS Trigger, board_job_id AS BoardJobId, state AS State,
            started_at AS StartedAt, finished_at AS FinishedAt, files AS Files, bytes AS Bytes, added_bytes AS AddedBytes,
            snapshot AS Snapshot, warnings AS Warnings, error_code AS ErrorCode, error_params AS ErrorParams
        FROM backup_runs
        """;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>Jobs with a run or check going on, by job id.</summary>
    private readonly ConcurrentDictionary<long, byte> _active = new();

    /// <summary>Snapshots runs hold now, so two runs never take the same name and a run never drops another's.</summary>
    private readonly HashSet<string> _snapshotsInUse = new(StringComparer.Ordinal);

    /// <summary>Raised when a job was added, changed or deleted, for the scheduler.</summary>
    public event Action? Changed;

    public async Task<BackupJobList> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<JobRow>(new CommandDefinition(SelectJob + " ORDER BY name COLLATE NOCASE", cancellationToken: cancellationToken));
        var result = new List<BackupJob>();
        foreach (var row in rows)
        {
            result.Add(await ViewAsync(connection, row, cancellationToken));
        }
        return new BackupJobList(result);
    }

    public async Task<BackupJob> GetAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await ViewAsync(connection, await RowAsync(connection, id, cancellationToken), cancellationToken);
    }

    /// <summary>Every shared folder and home, and whether a job can back it up.</summary>
    public async Task<BackupSourceList> SourcesAsync(CancellationToken cancellationToken)
    {
        var roots = (await shares.ListIndexRootsAsync(cancellationToken)).ToDictionary(root => root.Id);
        var paths = await shares.ListPathsAsync(cancellationToken);
        var result = paths
            .Where(share => roots.ContainsKey(share.Id))
            .Select(share =>
            {
                var root = roots[share.Id];
                return new BackupSourceOption(share.Id, DisplayName(share.Name), share.Name.StartsWith(Homes.RowPrefix, StringComparison.Ordinal), root.Dataset is not null, root.Available);
            })
            .OrderBy(source => source.Home)
            .ThenBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new BackupSourceList(result);
    }

    /// <summary>
    /// Saves a new job after its repository was made with a new key, or after the existing repository opened with the
    /// given key. The new key is returned only now.
    /// </summary>
    public async Task<BackupJobCreated> CreateAsync(ClaimsPrincipal caller, BackupJobCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = BackupJobRules.Check(request.Settings);
        var repository = BackupJobRules.CheckRepository(request.Repository);
        var existingKey = request.ExistingKey is null ? null : BackupJobRules.CheckKey(request.ExistingKey);
        await CheckSourcesExistAsync(settings.Sources, cancellationToken);
        var (endpoint, targetPath) = await targets.EndpointAsync(request.TargetId, cancellationToken);
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            await EnsureNameFreeAsync(connection, settings.Name, null, cancellationToken);
            var taken = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM backup_jobs WHERE target_id = @targetId AND repository = @repository",
                new { targetId = request.TargetId, repository },
                cancellationToken: cancellationToken));
            if (taken > 0)
            {
                throw new PithyxException(ErrorCodes.BackupRepositoryExists, new Dictionary<string, object?> { ["path"] = BackupJobRules.RemotePath(targetPath, repository) });
            }
        }
        var key = existingKey ?? Secrets.NewToken(32);
        var location = new ResticRepository(endpoint, BackupJobRules.RemotePath(targetPath, repository), key);
        try
        {
            if (existingKey is null)
            {
                await restic.InitAsync(location, cancellationToken);
            }
            else
            {
                await restic.VerifyAsync(location, cancellationToken);
            }
        }
        catch (TimeoutException)
        {
            throw new PithyxException(ErrorCodes.BackupTargetUnreachable, new Dictionary<string, object?> { ["reason"] = "timeout" });
        }
        catch (PithyxException error) when (error.Code.StartsWith("backup.target.", StringComparison.Ordinal))
        {
            await NoteConnectionFailureAsync(request.TargetId, error, CancellationToken.None);
            throw;
        }

        var now = SessionService.Format(time.GetUtcNow());
        long id;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            try
            {
                id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    """
                    INSERT INTO backup_jobs (name, kind, target_id, repository, repository_key, sources, excludes, schedule, enabled,
                        keep_last, keep_hourly, keep_daily, keep_weekly, keep_monthly, keep_yearly, check_schedule, check_read_percent,
                        upload_limit_kib, download_limit_kib, overdue_hours, created_at, updated_at)
                    VALUES (@Name, 'versioned', @targetId, @repository, @key, @sources, @excludes, @Schedule, @Enabled,
                        @keepLast, @keepHourly, @keepDaily, @keepWeekly, @keepMonthly, @keepYearly, @CheckSchedule, @CheckReadPercent,
                        @UploadLimitKib, @DownloadLimitKib, @OverdueHours, @now, @now)
                    RETURNING id
                    """,
                    Parameters(settings, new { targetId = request.TargetId, repository, key, now }),
                    cancellationToken: cancellationToken));
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                throw NameTaken(settings.Name);
            }
        }
        await audit.RecordAsync(caller, JobCreated, settings.Name, new Dictionary<string, string>
        {
            ["target"] = request.TargetId.ToString(CultureInfo.InvariantCulture),
            ["repository"] = repository,
            ["connected"] = existingKey is null ? "false" : "true",
        }, cancellationToken);
        LogCreated(id, repository, existingKey is not null);
        Changed?.Invoke();
        return new BackupJobCreated(await GetAsync(id, cancellationToken), existingKey is null ? key : null);
    }

    /// <summary>Changes what a job backs up and when; its target, repository and key stay.</summary>
    public async Task<BackupJob> UpdateAsync(long id, BackupJobSettings request, CancellationToken cancellationToken)
    {
        var settings = BackupJobRules.Check(request);
        await CheckSourcesExistAsync(settings.Sources, cancellationToken);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await RowAsync(connection, id, cancellationToken);
        await EnsureNameFreeAsync(connection, settings.Name, id, cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE backup_jobs SET name = @Name, sources = @sources, excludes = @excludes, schedule = @Schedule, enabled = @Enabled,
                    keep_last = @keepLast, keep_hourly = @keepHourly, keep_daily = @keepDaily, keep_weekly = @keepWeekly,
                    keep_monthly = @keepMonthly, keep_yearly = @keepYearly, check_schedule = @CheckSchedule,
                    check_read_percent = @CheckReadPercent, upload_limit_kib = @UploadLimitKib, download_limit_kib = @DownloadLimitKib,
                    overdue_hours = @OverdueHours, updated_at = @now
                WHERE id = @id
                """,
                Parameters(settings, new { id, now = SessionService.Format(time.GetUtcNow()) }),
                cancellationToken: cancellationToken));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw NameTaken(settings.Name);
        }
        Changed?.Invoke();
        return await ViewAsync(connection, await RowAsync(connection, id, cancellationToken), cancellationToken);
    }

    /// <summary>Deletes a job, its runs and its key. The repository stays on the target.</summary>
    public async Task DeleteAsync(ClaimsPrincipal caller, long id, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var row = await RowAsync(connection, id, cancellationToken);
        if (_active.ContainsKey(id))
        {
            throw Busy(row.Name);
        }
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM backup_jobs WHERE id = @id", new { id }, cancellationToken: cancellationToken));
        await audit.RecordAsync(caller, JobDeleted, row.Name, new Dictionary<string, string> { ["repository"] = row.Repository }, cancellationToken);
        LogDeleted(id);
        Changed?.Invoke();
    }

    /// <summary>A job's runs and checks, newest first.</summary>
    public async Task<BackupRunList> RunsAsync(long id, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await RowAsync(connection, id, cancellationToken);
        var rows = await connection.QueryAsync<RunRow>(new CommandDefinition(
            SelectRun + " WHERE job_id = @id ORDER BY id DESC LIMIT @limit", new { id, limit = Math.Clamp(limit, 1, KeptRuns) }, cancellationToken: cancellationToken));
        return new BackupRunList([.. rows.Select(View)]);
    }

    /// <summary>Starts a run or a check as a box job. Throws <see cref="ErrorCodes.BackupJobBusy"/> while one goes on.</summary>
    public async Task<Job> StartAsync(long id, BackupRunKind kind, BackupRunTrigger trigger, CancellationToken cancellationToken)
    {
        JobRow row;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            row = await RowAsync(connection, id, cancellationToken);
        }
        if (!_active.TryAdd(id, 0))
        {
            throw Busy(row.Name);
        }
        try
        {
            return await jobs.StartAsync(
                JobService.Box,
                kind == BackupRunKind.Backup ? RunKind : CheckKind,
                new { jobId = id, name = row.Name },
                context => WorkAsync(id, kind, trigger, context),
                cancellationToken,
                new JobOptions(OwnSlot: true, Notify: false));
        }
        catch
        {
            _active.TryRemove(id, out _);
            throw;
        }
    }

    /// <summary>After a restart: runs that were going on when the daemon stopped become interrupted.</summary>
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var count = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE backup_runs SET state = 'interrupted', finished_at = @now WHERE state = 'running'",
            new { now = SessionService.Format(time.GetUtcNow()) },
            cancellationToken: cancellationToken));
        if (count > 0)
        {
            LogRecovered(count);
        }
    }

    /// <summary>The enabled jobs' schedules, for the scheduler.</summary>
    public async Task<IReadOnlyList<(long Id, string Schedule, string? CheckSchedule)>> SchedulesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return [.. await connection.QueryAsync<(long, string, string?)>(new CommandDefinition(
            "SELECT id, schedule, check_schedule FROM backup_jobs WHERE enabled = 1", cancellationToken: cancellationToken))];
    }

    /// <summary>
    /// Tells administrators once about each enabled job without a successful run for longer than its limit, until it
    /// succeeds again. Returns when the next job falls due, or null.
    /// </summary>
    public async Task<DateTimeOffset?> NotifyOverdueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        DateTimeOffset? next = null;
        await using var connection = await connections.OpenAsync(cancellationToken);
        var rows = (await connection.QueryAsync<JobRow>(new CommandDefinition(SelectJob + " WHERE enabled = 1", cancellationToken: cancellationToken))).ToList();
        foreach (var row in rows)
        {
            var since = await LastSuccessAsync(connection, row.Id, cancellationToken) ?? SessionService.Parse(row.CreatedAt);
            var due = since + BackupJobRules.OverdueLimit(row.Schedule, (int?)row.OverdueHours, now, TimeZoneInfo.Local);
            var notified = row.OverdueNotifiedAt is null ? (DateTimeOffset?)null : SessionService.Parse(row.OverdueNotifiedAt);
            if (notified is { } at && at >= since)
            {
                continue;
            }
            if (due > now)
            {
                next = next is null || due < next ? due : next;
                continue;
            }
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE backup_jobs SET overdue_notified_at = @now WHERE id = @id",
                new { id = row.Id, now = SessionService.Format(now) },
                cancellationToken: cancellationToken));
            LogOverdue(row.Id, since);
            await notifications.CreateForAdministratorsAsync(new NotificationDraft(OverdueCode, new Dictionary<string, object?>
            {
                ["job"] = row.Name,
                ["jobId"] = row.Id,
                ["days"] = (int)Math.Floor((now - since).TotalDays),
            }, NotificationTone.Warning), cancellationToken);
        }
        return next;
    }

    private async Task WorkAsync(long id, BackupRunKind kind, BackupRunTrigger trigger, JobContext context)
    {
        try
        {
            await RunAsync(id, kind, trigger, context);
        }
        finally
        {
            _active.TryRemove(id, out _);
        }
    }

    private async Task RunAsync(long id, BackupRunKind kind, BackupRunTrigger trigger, JobContext context)
    {
        JobRow row;
        long runId;
        await using (var connection = await connections.OpenAsync(CancellationToken.None))
        {
            row = await RowAsync(connection, id, CancellationToken.None);
            runId = await connection.ExecuteScalarAsync<long>(
                """
                INSERT INTO backup_runs (job_id, kind, trigger, board_job_id, state, started_at)
                VALUES (@id, @kind, @trigger, @boardJobId, 'running', @now)
                RETURNING id
                """,
                new { id, kind = Text(kind), trigger = Text(trigger), boardJobId = context.JobId, now = SessionService.Format(time.GetUtcNow()) });
            await connection.ExecuteAsync(
                "DELETE FROM backup_runs WHERE job_id = @id AND id NOT IN (SELECT id FROM backup_runs WHERE job_id = @id ORDER BY id DESC LIMIT @kept)",
                new { id, kept = KeptRuns });
        }
        LogRunStarted(id, runId, kind, trigger);
        var report = new ResticReport();
        var state = BackupRunState.Succeeded;
        PithyxException? error = null;
        try
        {
            (state, error) = kind == BackupRunKind.Backup
                ? await BackupAsync(row, report, context)
                : await CheckAsync(row, report, context.Cancellation);
        }
        catch (OperationCanceledException) when (context.Cancellation.IsCancellationRequested)
        {
            state = lifetime.ApplicationStopping.IsCancellationRequested ? BackupRunState.Interrupted : BackupRunState.Cancelled;
            await FinishAsync(row, runId, kind, state, null, report);
            throw;
        }
        catch (PithyxException exception)
        {
            (state, error) = (BackupRunState.Failed, exception);
        }
        catch (TimeoutException)
        {
            (state, error) = (BackupRunState.Failed, new PithyxException(ErrorCodes.BackupRunFailed, new Dictionary<string, object?> { ["reason"] = "timeout" }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LogRunCrashed(exception, id, runId);
            (state, error) = (BackupRunState.Failed, new PithyxException(ErrorCodes.SystemInternalUnexpected));
        }
        await FinishAsync(row, runId, kind, state, error, report);
        if (state == BackupRunState.Failed && error is not null)
        {
            throw error;
        }
        context.SetResult(new { runId, state = Text(state) });
    }

    private async Task<(BackupRunState State, PithyxException? Error)> BackupAsync(JobRow row, ResticReport report, JobContext context)
    {
        var settings = Settings(row);
        var sources = await ResolveSourcesAsync(settings.Sources, context.Cancellation);
        var pool = sources[0].Dataset.Split('/')[0];
        var location = await LocationAsync(row, await CacheDirAsync(pool, row.Id, context.Cancellation), context.Cancellation);
        var mounts = new List<SnapshotMount>();
        try
        {
            foreach (var dataset in sources.Select(source => (source.Dataset, source.Mountpoint, source.Share)).Distinct())
            {
                mounts.Add(await TakeSnapshotAsync(dataset.Dataset, dataset.Mountpoint, dataset.Share, context.Cancellation));
            }
            var result = await restic.BackupAsync(
                location,
                mounts,
                [.. sources.Select(source => source.Path)],
                settings.Excludes ?? [],
                report,
                () =>
                {
                    if (report.Progress is { } progress)
                    {
                        context.Report(progress);
                    }
                },
                context.Cancellation);
            if (ResticOutput.Failure(result.ExitCode, result.StandardError, location.RemotePath) is { } failure)
            {
                LogResticFailed(row.Id, result.ExitCode, result.StandardError.Length > 4000 ? result.StandardError[..4000] : result.StandardError);
                return (BackupRunState.Failed, failure);
            }
            await targets.RecordConnectionAsync(row.TargetId, CancellationToken.None);
            if (report.Warnings > 0 || result.ExitCode == ResticCommands.Incomplete)
            {
                LogUnreadable(row.Id, report.Warnings, string.Join(", ", report.WarningItems));
            }
            var state = result.ExitCode == ResticCommands.Incomplete ? BackupRunState.Warning : BackupRunState.Succeeded;
            if (await restic.ForgetAsync(location, settings.Retention, context.Cancellation) is { ExitCode: not 0 } forget)
            {
                LogForgetFailed(row.Id, forget.ExitCode, forget.StandardError.Length > 4000 ? forget.StandardError[..4000] : forget.StandardError);
                return (BackupRunState.Warning, ResticOutput.Failure(forget.ExitCode, forget.StandardError, location.RemotePath));
            }
            return (state, null);
        }
        finally
        {
            foreach (var mount in mounts)
            {
                await DropSnapshotAsync(mount.Snapshot);
            }
        }
    }

    private async Task<(BackupRunState State, PithyxException? Error)> CheckAsync(JobRow row, ResticReport report, CancellationToken cancellationToken)
    {
        var settings = Settings(row);
        string? cacheDir = null;
        var sources = await shares.ListIndexRootsAsync(cancellationToken);
        if (settings.Sources.Select(source => sources.FirstOrDefault(root => root.Id == source.ShareId)?.Dataset).FirstOrDefault(dataset => dataset is not null) is { } dataset)
        {
            cacheDir = await CacheDirAsync(dataset.Split('/')[0], row.Id, cancellationToken);
        }
        var location = await LocationAsync(row, cacheDir, cancellationToken);
        var result = await restic.CheckAsync(location, (int)row.CheckReadPercent, report, cancellationToken);
        if (result.ExitCode == 0)
        {
            await targets.RecordConnectionAsync(row.TargetId, CancellationToken.None);
            return (BackupRunState.Succeeded, null);
        }
        LogResticFailed(row.Id, result.ExitCode, result.StandardError.Length > 4000 ? result.StandardError[..4000] : result.StandardError);
        var failure = ResticOutput.Failure(result.ExitCode, result.StandardError, location.RemotePath);
        return (BackupRunState.Failed, failure is null || failure.Code == ErrorCodes.BackupRunFailed ? new PithyxException(ErrorCodes.BackupCheckFailed) : failure);
    }

    private async Task FinishAsync(JobRow row, long runId, BackupRunKind kind, BackupRunState state, PithyxException? error, ResticReport report)
    {
        await using (var connection = await connections.OpenAsync(CancellationToken.None))
        {
            await connection.ExecuteAsync(
                """
                UPDATE backup_runs SET state = @state, finished_at = @now, files = @Files, bytes = @Bytes, added_bytes = @AddedBytes,
                    snapshot = @Snapshot, warnings = @Warnings, error_code = @code, error_params = @parameters
                WHERE id = @runId
                """,
                new
                {
                    runId,
                    state = Text(state),
                    now = SessionService.Format(time.GetUtcNow()),
                    report.Files,
                    report.Bytes,
                    report.AddedBytes,
                    report.Snapshot,
                    report.Warnings,
                    code = error?.Code,
                    parameters = error is null ? null : BackupSsh.Params(error),
                });
        }
        LogRunFinished(row.Id, runId, state, error?.Code);
        if (error is not null && error.Code.StartsWith("backup.target.", StringComparison.Ordinal))
        {
            await NoteConnectionFailureAsync(row.TargetId, error, CancellationToken.None);
        }
        var code = state switch
        {
            BackupRunState.Failed => kind == BackupRunKind.Backup ? RunFailedCode : CheckFailedCode,
            BackupRunState.Warning => RunWarningCode,
            _ => null,
        };
        if (code is not null)
        {
            await notifications.CreateForAdministratorsAsync(new NotificationDraft(code, new Dictionary<string, object?>
            {
                ["job"] = row.Name,
                ["jobId"] = row.Id,
                ["warnings"] = report.Warnings,
                ["error"] = error?.Code,
            }, state == BackupRunState.Failed ? NotificationTone.Critical : NotificationTone.Warning), CancellationToken.None);
        }
    }

    private async Task NoteConnectionFailureAsync(long targetId, PithyxException error, CancellationToken cancellationToken)
    {
        if (!await targets.RecordConnectionFailureAsync(targetId, error, cancellationToken))
        {
            return;
        }
        var target = await targets.GetAsync(targetId, cancellationToken);
        await notifications.CreateForAdministratorsAsync(new NotificationDraft(HostKeyChangedCode, new Dictionary<string, object?>
        {
            ["target"] = target.Name,
            ["targetId"] = target.Id,
            ["fingerprint"] = error.Parameters.TryGetValue("fingerprint", out var fingerprint) ? fingerprint : null,
        }, NotificationTone.Critical), cancellationToken);
    }

    private async Task<ResticRepository> LocationAsync(JobRow row, string? cacheDir, CancellationToken cancellationToken)
    {
        var (endpoint, targetPath) = await targets.EndpointAsync(row.TargetId, cancellationToken);
        return new ResticRepository(endpoint, BackupJobRules.RemotePath(targetPath, row.Repository), row.RepositoryKey, (int)row.UploadLimitKib, (int)row.DownloadLimitKib, cacheDir);
    }

    /// <summary>restic's cache of the job on the pool of its first source (dataset <c>&lt;pool&gt;/pithyx/backup-cache</c>); null when it cannot be made.</summary>
    private async Task<string?> CacheDirAsync(string pool, long jobId, CancellationToken cancellationToken)
    {
        var result = await commands.RunAsync(Storage.Zfs.ZfsPlans.CreateBackupCacheDataset(pool), cancellationToken);
        if (result.ExitCode != 0)
        {
            LogCacheUnavailable(pool, result.StandardError);
            return null;
        }
        if (await MountpointAsync(pool + "/pithyx/backup-cache", cancellationToken) is not { } root)
        {
            LogCacheUnavailable(pool, "not mounted");
            return null;
        }
        var folder = Path.Combine(root, jobId.ToString(CultureInfo.InvariantCulture));
        try
        {
            if (OperatingSystem.IsLinux())
            {
                Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            else
            {
                Directory.CreateDirectory(folder);
            }
            return folder;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogCacheUnavailable(pool, exception.Message);
            return null;
        }
    }

    private sealed record ResolvedSource(string Share, string Dataset, string Mountpoint, string Path);

    /// <summary>Each source's dataset, its mountpoint and the live folder. Throws when a source cannot be backed up now.</summary>
    private async Task<List<ResolvedSource>> ResolveSourcesAsync(IReadOnlyList<BackupSource> sources, CancellationToken cancellationToken)
    {
        var roots = (await shares.ListIndexRootsAsync(cancellationToken)).ToDictionary(root => root.Id);
        var names = (await shares.ListPathsAsync(cancellationToken)).ToDictionary(share => share.Id, share => DisplayName(share.Name));
        var mountpoints = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new List<ResolvedSource>();
        foreach (var source in sources)
        {
            var name = names.GetValueOrDefault(source.ShareId) ?? "#" + source.ShareId.ToString(CultureInfo.InvariantCulture);
            if (!roots.TryGetValue(source.ShareId, out var root))
            {
                throw SourceUnavailable(name, "missing", source.Path);
            }
            if (root.Dataset is null)
            {
                throw new PithyxException(ErrorCodes.BackupSourceUnsupported, new Dictionary<string, object?> { ["share"] = name });
            }
            if (!root.Available)
            {
                throw SourceUnavailable(name, "unavailable", source.Path);
            }
            if (!mountpoints.TryGetValue(root.Dataset, out var mountpoint))
            {
                mountpoint = await MountpointAsync(root.Dataset, cancellationToken) ?? throw SourceUnavailable(name, "unavailable", source.Path);
                mountpoints[root.Dataset] = mountpoint;
            }
            var path = source.Path.Length == 0 ? root.Path.TrimEnd('/') : root.Path.TrimEnd('/') + "/" + source.Path;
            var relative = Path.GetRelativePath(mountpoint, path);
            if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                throw new PithyxException(ErrorCodes.BackupSourceUnsupported, new Dictionary<string, object?> { ["share"] = name });
            }
            if (!Directory.Exists(path))
            {
                throw SourceUnavailable(name, "missing", source.Path);
            }
            result.Add(new ResolvedSource(name, root.Dataset, mountpoint, path));
        }
        return result;
    }

    /// <summary>A mounted dataset's mountpoint, or null when it is not mounted.</summary>
    private async Task<string?> MountpointAsync(string dataset, CancellationToken cancellationToken)
    {
        var result = await commands.RunAsync(new CommandRequest("zfs", ["get", "-H", "-p", "-o", "value", "mounted,mountpoint", dataset]), cancellationToken);
        var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return result.ExitCode == 0 && lines.Length == 2 && lines[0] == "yes" && lines[1].StartsWith('/') ? lines[1] : null;
    }

    /// <summary>Takes the run's snapshot of a dataset, after dropping backup snapshots a crashed run left there.</summary>
    private async Task<SnapshotMount> TakeSnapshotAsync(string dataset, string mountpoint, string share, CancellationToken cancellationToken)
    {
        var listing = await commands.RunAsync(new CommandRequest("zfs", ["list", "-H", "-o", "name", "-t", "snapshot", "-d", "1", dataset]), cancellationToken);
        foreach (var name in listing.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = name.IndexOf('@', StringComparison.Ordinal);
            if (at > 0 && SnapshotNames.KindOf(name[(at + 1)..]) == SnapshotKinds.Backup && !InUse(name))
            {
                await DropSnapshotAsync(name);
            }
        }
        string snapshot;
        lock (_snapshotsInUse)
        {
            var stamp = time.GetUtcNow();
            snapshot = $"{dataset}@{SnapshotNames.For(SnapshotKinds.Backup, stamp)}";
            while (!_snapshotsInUse.Add(snapshot))
            {
                stamp = stamp.AddSeconds(1);
                snapshot = $"{dataset}@{SnapshotNames.For(SnapshotKinds.Backup, stamp)}";
            }
        }
        var result = await commands.RunAsync(Storage.Zfs.SnapshotPlans.Take(dataset, snapshot[(dataset.Length + 1)..]), cancellationToken);
        if (result.ExitCode != 0)
        {
            lock (_snapshotsInUse)
            {
                _snapshotsInUse.Remove(snapshot);
            }
            throw SourceUnavailable(share, "snapshot", "");
        }
        return new SnapshotMount(snapshot, mountpoint);
    }

    private async Task DropSnapshotAsync(string snapshot)
    {
        try
        {
            var at = snapshot.IndexOf('@', StringComparison.Ordinal);
            var result = await commands.RunAsync(Storage.Zfs.SnapshotPlans.Destroy(snapshot[..at], snapshot[(at + 1)..]), CancellationToken.None);
            if (result.ExitCode != 0)
            {
                LogSnapshotLeft(snapshot, result.StandardError);
            }
        }
        finally
        {
            lock (_snapshotsInUse)
            {
                _snapshotsInUse.Remove(snapshot);
            }
        }
    }

    private bool InUse(string snapshot)
    {
        lock (_snapshotsInUse)
        {
            return _snapshotsInUse.Contains(snapshot);
        }
    }

    private async Task CheckSourcesExistAsync(IReadOnlyList<BackupSource> sources, CancellationToken cancellationToken)
    {
        var roots = (await shares.ListIndexRootsAsync(cancellationToken)).Select(root => root.Id).ToHashSet();
        if (sources.Any(source => !roots.Contains(source.ShareId)))
        {
            throw BackupJobRules.Invalid("sources");
        }
    }

    private static string DisplayName(string shareName) =>
        shareName.StartsWith(Homes.RowPrefix, StringComparison.Ordinal) ? Homes.AllName + "/" + shareName[Homes.RowPrefix.Length..] : shareName;

    private static PithyxException SourceUnavailable(string share, string reason, string path) =>
        new(ErrorCodes.BackupSourceUnavailable, new Dictionary<string, object?> { ["share"] = share, ["reason"] = reason, ["path"] = path });

    private static PithyxException Busy(string name) => new(ErrorCodes.BackupJobBusy, new Dictionary<string, object?> { ["name"] = name });

    private static PithyxException NameTaken(string name) => new(ErrorCodes.BackupJobNameTaken, new Dictionary<string, object?> { ["name"] = name });

    private static async Task EnsureNameFreeAsync(SqliteConnection connection, string name, long? except, CancellationToken cancellationToken)
    {
        var taken = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM backup_jobs WHERE name = @name COLLATE NOCASE AND id IS NOT @except",
            new { name, except },
            cancellationToken: cancellationToken));
        if (taken > 0)
        {
            throw NameTaken(name);
        }
    }

    private static async Task<JobRow> RowAsync(SqliteConnection connection, long id, CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition(SelectJob + " WHERE id = @id", new { id }, cancellationToken: cancellationToken))
        ?? throw new PithyxException(ErrorCodes.BackupJobNotFound, new Dictionary<string, object?> { ["id"] = id });

    private static async Task<DateTimeOffset?> LastSuccessAsync(SqliteConnection connection, long id, CancellationToken cancellationToken)
    {
        var at = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT max(finished_at) FROM backup_runs WHERE job_id = @id AND kind = 'backup' AND state IN ('succeeded', 'warning')",
            new { id },
            cancellationToken: cancellationToken));
        return at is null ? null : SessionService.Parse(at);
    }

    private async Task<BackupJob> ViewAsync(SqliteConnection connection, JobRow row, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var settings = Settings(row);
        var lastRun = await connection.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(
            SelectRun + " WHERE job_id = @id AND kind = 'backup' ORDER BY id DESC LIMIT 1", new { id = row.Id }, cancellationToken: cancellationToken));
        var lastCheck = await connection.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(
            SelectRun + " WHERE job_id = @id AND kind = 'check' ORDER BY id DESC LIMIT 1", new { id = row.Id }, cancellationToken: cancellationToken));
        var lastSuccess = await LastSuccessAsync(connection, row.Id, cancellationToken);
        var since = lastSuccess ?? SessionService.Parse(row.CreatedAt);
        var overdue = settings.Enabled && now - since > BackupJobRules.OverdueLimit(settings.Schedule, settings.OverdueHours, now, TimeZoneInfo.Local);
        return new BackupJob(
            row.Id,
            BackupJobKind.Versioned,
            row.TargetId,
            row.Repository,
            settings,
            SessionService.Parse(row.CreatedAt),
            settings.Enabled ? CronSchedule.Parse(settings.Schedule).Next(now, TimeZoneInfo.Local) : null,
            settings.Enabled && settings.CheckSchedule is not null ? CronSchedule.Parse(settings.CheckSchedule).Next(now, TimeZoneInfo.Local) : null,
            lastRun is null ? null : View(lastRun),
            lastCheck is null ? null : View(lastCheck),
            lastSuccess,
            overdue);
    }

    private static BackupJobSettings Settings(JobRow row) => new(
        row.Name,
        JsonSerializer.Deserialize<List<BackupSource>>(row.Sources, _json) ?? [],
        row.Schedule,
        row.Enabled != 0,
        new BackupRetention((int)row.KeepLast, (int)row.KeepHourly, (int)row.KeepDaily, (int)row.KeepWeekly, (int)row.KeepMonthly, (int)row.KeepYearly),
        JsonSerializer.Deserialize<List<string>>(row.Excludes, _json) ?? [],
        row.CheckSchedule,
        (int)row.CheckReadPercent,
        (int)row.UploadLimitKib,
        (int)row.DownloadLimitKib,
        (int?)row.OverdueHours);

    private static DynamicParameters Parameters(BackupJobSettings settings, object extra)
    {
        var parameters = new DynamicParameters(extra);
        parameters.AddDynamicParams(new
        {
            settings.Name,
            sources = JsonSerializer.Serialize(settings.Sources, _json),
            excludes = JsonSerializer.Serialize(settings.Excludes ?? [], _json),
            settings.Schedule,
            settings.Enabled,
            keepLast = settings.Retention.Last,
            keepHourly = settings.Retention.Hourly,
            keepDaily = settings.Retention.Daily,
            keepWeekly = settings.Retention.Weekly,
            keepMonthly = settings.Retention.Monthly,
            keepYearly = settings.Retention.Yearly,
            settings.CheckSchedule,
            settings.CheckReadPercent,
            settings.UploadLimitKib,
            settings.DownloadLimitKib,
            settings.OverdueHours,
        });
        return parameters;
    }

    private static BackupRun View(RunRow row) => new(
        row.Id,
        Enum.Parse<BackupRunKind>(row.Kind, ignoreCase: true),
        Enum.Parse<BackupRunTrigger>(row.Trigger, ignoreCase: true),
        row.BoardJobId,
        Enum.Parse<BackupRunState>(row.State, ignoreCase: true),
        SessionService.Parse(row.StartedAt),
        row.FinishedAt is null ? null : SessionService.Parse(row.FinishedAt),
        row.Files,
        row.Bytes,
        row.AddedBytes,
        row.Snapshot,
        (int)row.Warnings,
        row.ErrorCode,
        row.ErrorParams is null ? null : JsonSerializer.Deserialize<JsonElement>(row.ErrorParams));

    private static string Text<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    private sealed class JobRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public long TargetId { get; init; }
        public string Repository { get; init; } = "";
        public string RepositoryKey { get; init; } = "";
        public string Sources { get; init; } = "[]";
        public string Excludes { get; init; } = "[]";
        public string Schedule { get; init; } = "";
        public long Enabled { get; init; }
        public long KeepLast { get; init; }
        public long KeepHourly { get; init; }
        public long KeepDaily { get; init; }
        public long KeepWeekly { get; init; }
        public long KeepMonthly { get; init; }
        public long KeepYearly { get; init; }
        public string? CheckSchedule { get; init; }
        public long CheckReadPercent { get; init; }
        public long UploadLimitKib { get; init; }
        public long DownloadLimitKib { get; init; }
        public long? OverdueHours { get; init; }
        public string? OverdueNotifiedAt { get; init; }
        public string CreatedAt { get; init; } = "";
    }

    private sealed class RunRow
    {
        public long Id { get; init; }
        public long JobId { get; init; }
        public string Kind { get; init; } = "";
        public string Trigger { get; init; } = "";
        public long? BoardJobId { get; init; }
        public string State { get; init; } = "";
        public string StartedAt { get; init; } = "";
        public string? FinishedAt { get; init; }
        public long? Files { get; init; }
        public long? Bytes { get; init; }
        public long? AddedBytes { get; init; }
        public string? Snapshot { get; init; }
        public long Warnings { get; init; }
        public string? ErrorCode { get; init; }
        public string? ErrorParams { get; init; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup job {JobId} added with repository {Repository} (existing: {Existing})")]
    private partial void LogCreated(long jobId, string repository, bool existing);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup job {JobId} removed")]
    private partial void LogDeleted(long jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup job {JobId} started {Kind} run {RunId} by {Trigger}")]
    private partial void LogRunStarted(long jobId, long runId, BackupRunKind kind, BackupRunTrigger trigger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup job {JobId} finished run {RunId} as {State} {ErrorCode}")]
    private partial void LogRunFinished(long jobId, long runId, BackupRunState state, string? errorCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Backup job {JobId} run {RunId} failed unexpectedly")]
    private partial void LogRunCrashed(Exception exception, long jobId, long runId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "restic failed for backup job {JobId} with exit code {ExitCode}: {StandardError}")]
    private partial void LogResticFailed(long jobId, int exitCode, string standardError);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retention of backup job {JobId} failed with exit code {ExitCode}: {StandardError}")]
    private partial void LogForgetFailed(long jobId, int exitCode, string standardError);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup job {JobId} could not read {Count} files or folders: {Items}")]
    private partial void LogUnreadable(long jobId, int count, string items);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No backup cache on pool {Pool}; restic runs without one: {Reason}")]
    private partial void LogCacheUnavailable(string pool, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup snapshot {Snapshot} could not be destroyed; the next run of a job on its dataset tries again: {Reason}")]
    private partial void LogSnapshotLeft(string snapshot, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup job {JobId} had no successful run since {Since}")]
    private partial void LogOverdue(long jobId, DateTimeOffset since);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} backup runs were interrupted by a restart")]
    private partial void LogRecovered(int count);
}
