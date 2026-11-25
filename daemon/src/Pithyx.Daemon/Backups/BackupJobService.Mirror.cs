using System.Globalization;
using System.Security.Claims;
using Dapper;
using Microsoft.Data.Sqlite;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Jobs;

namespace Pithyx.Daemon.Backups;

/// <summary>
/// Mirrors (#397, D421): one current copy of folders of shared folders and homes on a target, as plain files in a
/// folder of the job's own, written by rsync over SSH from the run's ZFS snapshots. Each source lands below its shared
/// folder's or home's name. A mirror keeps no versions: what is deleted or damaged on the box reaches the copy at the
/// next run (deletions only when the job says so).
/// </summary>
public sealed partial class BackupJobService
{
    private const string MirrorKind = "mirror";

    private const string VersionedKind = "versioned";

    /// <summary>Saves a new mirror after its folder was made on the target and rsync answered there.</summary>
    public async Task<BackupJobCreated> CreateMirrorAsync(ClaimsPrincipal caller, BackupMirrorCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = BackupJobRules.CheckMirror(request.Settings);
        var folder = BackupJobRules.CheckRepository(request.Folder);
        var mirror = request.Mirror ?? throw BackupJobRules.Invalid("mirror");
        await CheckSourcesExistAsync(settings.Sources, cancellationToken);
        var (endpoint, targetPath) = await targets.EndpointAsync(request.TargetId, cancellationToken);
        var remote = BackupJobRules.RemotePath(targetPath, folder);
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            await EnsureNameFreeAsync(connection, settings.Name, null, cancellationToken);
            var taken = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM backup_jobs WHERE target_id = @targetId AND repository = @folder",
                new { targetId = request.TargetId, folder },
                cancellationToken: cancellationToken));
            if (taken > 0)
            {
                throw new PithyxException(ErrorCodes.BackupRepositoryExists, new Dictionary<string, object?> { ["path"] = remote });
            }
        }
        try
        {
            await ProbeMirrorAsync(endpoint, remote, cancellationToken);
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
                        upload_limit_kib, overdue_hours, created_at, updated_at, delete_removed)
                    VALUES (@Name, 'mirror', @targetId, @folder, '', @sources, @excludes, @Schedule, @Enabled,
                        @UploadLimitKib, @OverdueHours, @now, @now, @delete)
                    RETURNING id
                    """,
                    Parameters(settings, new { targetId = request.TargetId, folder, now, delete = mirror.Delete ? 1 : 0 }),
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
            ["folder"] = folder,
            ["delete"] = mirror.Delete ? "true" : "false",
        }, cancellationToken);
        LogMirrorCreated(id, remote);
        Changed?.Invoke();
        return new BackupJobCreated(await GetAsync(id, cancellationToken), null);
    }

    /// <summary>Changes what a mirror copies and when; its target and folder stay.</summary>
    public async Task<BackupJob> UpdateMirrorAsync(long id, BackupMirrorUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await connections.OpenAsync(cancellationToken);
        if ((await RowAsync(connection, id, cancellationToken)).Kind != MirrorKind)
        {
            throw Unsupported("edit");
        }
        var settings = BackupJobRules.CheckMirror(request.Settings);
        var mirror = request.Mirror ?? throw BackupJobRules.Invalid("mirror");
        await CheckSourcesExistAsync(settings.Sources, cancellationToken);
        await EnsureNameFreeAsync(connection, settings.Name, id, cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE backup_jobs SET name = @Name, sources = @sources, excludes = @excludes, schedule = @Schedule, enabled = @Enabled,
                    upload_limit_kib = @UploadLimitKib, overdue_hours = @OverdueHours, delete_removed = @delete, updated_at = @now
                WHERE id = @id
                """,
                Parameters(settings, new { id, now = SessionService.Format(time.GetUtcNow()), delete = mirror.Delete ? 1 : 0 }),
                cancellationToken: cancellationToken));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw NameTaken(settings.Name);
        }
        Changed?.Invoke();
        return await ViewAsync(connection, await RowAsync(connection, id, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Makes the mirror's folder on the target over SFTP and writes a test file there, then lists it with rsync, which
    /// a target that offers only SFTP refuses.
    /// </summary>
    private async Task ProbeMirrorAsync(SshEndpoint endpoint, string remote, CancellationToken cancellationToken)
    {
        if ((await ssh.ProbeAsync(endpoint, remote, cancellationToken)).Error is { } error)
        {
            throw error;
        }
        using var folder = new KeyFolder();
        CommandResult result;
        try
        {
            result = await commands.RunAsync(BackupRsync.ProbeTarget(folder, endpoint, remote), cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new PithyxException(ErrorCodes.BackupTargetUnreachable, new Dictionary<string, object?> { ["reason"] = "timeout" });
        }
        if (result.ExitCode == 0)
        {
            return;
        }
        LogMirrorProbeFailed(result.ExitCode, result.StandardError.Length > 4000 ? result.StandardError[..4000] : result.StandardError);
        // SFTP just made and wrote the folder, so a listing that fails without a named cause is a target without rsync.
        throw BackupRsync.MirrorFailure(result.ExitCode, result.StandardError, remote) is { Code: not ErrorCodes.BackupRunFailed } failure
            ? failure
            : new PithyxException(ErrorCodes.BackupTargetRsyncMissing);
    }

    /// <summary>One mirror run: snapshots of the sources, each shared folder bound at its name, rsync to the target.</summary>
    private async Task<(BackupRunState State, PithyxException? Error)> MirrorAsync(JobRow row, ResticReport report, JobContext context)
    {
        var settings = Settings(row);
        var sources = await ResolveSourcesAsync(settings.Sources, context.Cancellation);
        var (endpoint, targetPath) = await targets.EndpointAsync(row.TargetId, context.Cancellation);
        var remote = BackupJobRules.RemotePath(targetPath, row.Repository);
        var mounts = new List<SnapshotMount>();
        try
        {
            foreach (var dataset in sources.Select(source => (source.Dataset, source.Mountpoint, source.Share)).Distinct())
            {
                mounts.Add(await TakeSnapshotAsync(dataset.Dataset, dataset.Mountpoint, dataset.Share, context.Cancellation));
            }
            var rsync = new RsyncReport();
            CommandResult result;
            using (var folder = new KeyFolder())
            {
                var items = sources.Select(source => source.Path == source.Root ? source.Share : source.Share + "/" + Path.GetRelativePath(source.Root, source.Path)).ToList();
                var view = new List<string> { "sh", "-c", BackupRsync.ViewScript, "sh", BackupRsync.MirrorView };
                foreach (var (root, name) in sources.Select(source => (source.Root, source.Share)).Distinct())
                {
                    view.AddRange([root, name]);
                }
                view.AddRange(["--", "env"]);
                view.AddRange(BackupRsync.MirrorArguments(BackupRsync.Shell(folder, endpoint), items, remote, row.DeleteRemoved == 1, settings.Excludes ?? [], settings.UploadLimitKib));
                result = await commands.RunAsync(
                    new CommandRequest("unshare", ResticCommands.InNamespace(mounts, view), BackupRestic.BackupTimeout, OutputLine: line =>
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
            report.WarningItems.AddRange(rsync.Unreadable.Select(item => new ResticUnreadable(BackupRsync.MirrorItem(item.Item), item.Reason switch
            {
                "permission" => "permission denied",
                "missing" => "no such file",
                _ => item.Reason,
            })));
            if (BackupRsync.MirrorFailure(result.ExitCode, result.StandardError, remote) is { } failure)
            {
                LogRsyncFailed(row.Id, result.ExitCode, result.StandardError.Length > 4000 ? result.StandardError[..4000] : result.StandardError);
                return (BackupRunState.Failed, failure);
            }
            await targets.RecordConnectionAsync(row.TargetId, CancellationToken.None);
            if (rsync.UnreadableCount > 0)
            {
                LogUnreadable(row.Id, rsync.UnreadableCount, string.Join(", ", report.WarningItems.Take(20).Select(item => item.Item)));
            }
            report.AddedBytes = rsync.Transferred;
            report.RepositoryBytes = rsync.Bytes;
            await targets.MeasureSpaceAsync(row.TargetId, context.Cancellation);
            var state = result.ExitCode is BackupRsync.Partial or BackupRsync.Vanished || rsync.UnreadableCount > 0 ? BackupRunState.Warning : BackupRunState.Succeeded;
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

    private static BackupMirrorSettings? MirrorSettings(JobRow row) => row.Kind == MirrorKind ? new BackupMirrorSettings(row.DeleteRemoved == 1) : null;

    private static BackupJobKind KindOf(JobRow row) => row.Kind switch
    {
        PullKind => BackupJobKind.Pull,
        MirrorKind => BackupJobKind.Mirror,
        _ => BackupJobKind.Versioned,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Mirror {JobId} added into {Folder}")]
    private partial void LogMirrorCreated(long jobId, string folder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "rsync on a mirror's target failed with exit code {ExitCode}: {StandardError}")]
    private partial void LogMirrorProbeFailed(int exitCode, string standardError);
}
