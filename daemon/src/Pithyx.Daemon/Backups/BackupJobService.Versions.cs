using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Backups;

/// <summary>
/// A job's versions (#404): restic's snapshots, kept in <c>backup_versions</c> after every run and every change of a
/// version, so reading them never starts restic.
/// </summary>
public sealed partial class BackupJobService
{
    public const string KeyRevealed = "backup.job.key_revealed";
    public const string VersionProtected = "backup.version.protected";
    public const string VersionReleased = "backup.version.released";

    private const string SelectVersion =
        """
        SELECT id AS Id, snapshot AS Snapshot, time AS Time, files AS Files, bytes AS Bytes, added_bytes AS AddedBytes,
            files_added AS FilesAdded, files_changed AS FilesChanged, files_removed AS FilesRemoved, protected AS Protected,
            keep AS Keep, changes AS Changes, paths AS Paths
        FROM backup_versions
        """;

    /// <summary>A job's versions, newest first, as the box last saw them.</summary>
    public async Task<BackupVersionList> VersionsAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await RowAsync(connection, id, cancellationToken);
        var rows = await connection.QueryAsync<VersionRow>(new CommandDefinition(
            SelectVersion + " WHERE job_id = @id ORDER BY time DESC, id DESC", new { id }, cancellationToken: cancellationToken));
        return new BackupVersionList([.. rows.Select(View)]);
    }

    /// <summary>Protects a version from the retention or releases it again. Refused while the job runs.</summary>
    public async Task<BackupVersion> ProtectAsync(ClaimsPrincipal caller, long id, long versionId, bool protect, CancellationToken cancellationToken)
    {
        JobRow row;
        VersionRow version;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            row = await RowAsync(connection, id, cancellationToken);
            version = await VersionRowAsync(connection, id, versionId, cancellationToken);
        }
        if (!_active.TryAdd(id, 0))
        {
            throw Busy(row.Name);
        }
        try
        {
            if (row.Kind == PullKind)
            {
                return await ProtectPullAsync(caller, row, version, protect, cancellationToken);
            }
            if (Restoring(id))
            {
                throw Busy(row.Name);
            }
            // The tag gives the version a new id, which the mount would not know.
            await browser.CloseAsync(id);
            string? changed;
            try
            {
                changed = await restic.ProtectAsync(await LocationAsync(row, null, cancellationToken), version.Snapshot, protect, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new PithyxException(ErrorCodes.BackupTargetUnreachable, new Dictionary<string, object?> { ["reason"] = "timeout" });
            }
            catch (PithyxException error) when (error.Code.StartsWith("backup.target.", StringComparison.Ordinal))
            {
                await NoteConnectionFailureAsync(row.TargetId, error, CancellationToken.None);
                throw;
            }
            var keep = Reasons(version.Keep).Where(reason => reason != BackupKeepReason.Protected).ToList();
            if (protect)
            {
                keep.Add(BackupKeepReason.Protected);
            }
            await using var connection = await connections.OpenAsync(CancellationToken.None);
            await connection.ExecuteAsync(
                "UPDATE backup_versions SET snapshot = @snapshot, protected = @protect, keep = @keep WHERE id = @versionId",
                new { versionId, snapshot = changed ?? version.Snapshot, protect = protect ? 1 : 0, keep = KeepJson(keep) });
            var shown = ResticVersions.ShortId(changed ?? version.Snapshot);
            await audit.RecordAsync(caller, protect ? VersionProtected : VersionReleased, row.Name, new Dictionary<string, string>
            {
                ["version"] = shown,
                ["time"] = version.Time,
            }, CancellationToken.None);
            LogVersionProtection(id, shown, protect);
            return View(await VersionRowAsync(connection, id, versionId, CancellationToken.None));
        }
        finally
        {
            _active.TryRemove(id, out _);
        }
    }

    /// <summary>A job's repository key, for an administrator who proved it is them; the endpoint asks for the step-up.</summary>
    public async Task<BackupJobKey> RevealKeyAsync(ClaimsPrincipal caller, long id, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var row = await RowAsync(connection, id, cancellationToken);
        if (row.Kind == PullKind)
        {
            throw Unsupported("key");
        }
        await audit.RecordAsync(caller, KeyRevealed, row.Name, new Dictionary<string, string> { ["repository"] = row.Repository }, cancellationToken);
        LogKeyRevealed(id);
        return new BackupJobKey(row.RepositoryKey);
    }

    /// <summary>
    /// Reads the repository's versions after a run and keeps them. <paramref name="keep"/> is what the retention said
    /// (empty when the job keeps every version, null when its retention failed: the reasons stay as they were);
    /// <paramref name="made"/> is the version the run made, compared with the one before it.
    /// </summary>
    private async Task SyncVersionsAsync(
        JobRow row,
        ResticRepository location,
        IReadOnlyDictionary<string, IReadOnlyList<BackupKeepReason>>? keep,
        string? made,
        IReadOnlyList<ResolvedSource> sources,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ResticSnapshot> snapshots;
        try
        {
            snapshots = await restic.SnapshotsAsync(location, cancellationToken);
        }
        catch (Exception exception) when (exception is PithyxException or TimeoutException)
        {
            LogVersionsUnknown(row.Id, exception.Message);
            return;
        }
        ResticDiff? diff = null;
        var newest = made is null ? null : snapshots.FirstOrDefault(snapshot => snapshot.Id == made);
        if (newest is not null && snapshots.Where(snapshot => snapshot.Time < newest.Time).MaxBy(snapshot => snapshot.Time) is { } previous)
        {
            diff = await restic.DiffAsync(location, previous.Id, newest.Id, cancellationToken);
        }
        await using var connection = await connections.OpenAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
        foreach (var snapshot in snapshots)
        {
            var compared = snapshot == newest && diff is not null;
            await connection.ExecuteAsync(
                """
                INSERT INTO backup_versions (job_id, snapshot, origin, time, files, bytes, added_bytes, files_added, files_changed,
                    files_removed, protected, keep, changes, paths)
                VALUES (@jobId, @Id, @Origin, @time, @Files, @Bytes, @AddedBytes, @FilesAdded, @FilesChanged, @removed, @protect, @keep, @changes, @paths)
                ON CONFLICT (job_id, origin) DO UPDATE SET
                    snapshot = excluded.snapshot,
                    time = excluded.time,
                    files = coalesce(excluded.files, files),
                    bytes = coalesce(excluded.bytes, bytes),
                    added_bytes = coalesce(excluded.added_bytes, added_bytes),
                    files_added = coalesce(excluded.files_added, files_added),
                    files_changed = coalesce(excluded.files_changed, files_changed),
                    files_removed = coalesce(excluded.files_removed, files_removed),
                    protected = excluded.protected,
                    keep = CASE WHEN @keepKnown THEN excluded.keep ELSE keep END,
                    changes = coalesce(excluded.changes, changes),
                    paths = excluded.paths
                """,
                new
                {
                    jobId = row.Id,
                    snapshot.Id,
                    snapshot.Origin,
                    time = SessionService.Format(snapshot.Time),
                    snapshot.Files,
                    snapshot.Bytes,
                    snapshot.AddedBytes,
                    snapshot.FilesAdded,
                    snapshot.FilesChanged,
                    removed = compared ? diff!.Removed : (long?)null,
                    protect = snapshot.Protected ? 1 : 0,
                    keep = KeepJson(Kept(keep, snapshot)),
                    keepKnown = keep is not null,
                    changes = compared
                        ? JsonSerializer.Serialize(diff!.Changes.Select(change => new { path = ShareRelative(change.Path, sources), change = Text(change.Change) }), _json)
                        : null,
                    paths = snapshot.Paths.Count > 0 ? JsonSerializer.Serialize(snapshot.Paths) : null,
                },
                transaction);
        }
        // Versions the retention or someone else deleted from the repository.
        await connection.ExecuteAsync(
            "DELETE FROM backup_versions WHERE job_id = @jobId AND origin NOT IN (SELECT value FROM json_each(@origins))",
            new { jobId = row.Id, origins = JsonSerializer.Serialize(snapshots.Select(snapshot => snapshot.Origin)) },
            transaction);
        await transaction.CommitAsync(CancellationToken.None);
    }

    /// <summary>Why the retention kept a version: what restic said, and the tag, which restic only names when it decided.</summary>
    private static List<BackupKeepReason> Kept(IReadOnlyDictionary<string, IReadOnlyList<BackupKeepReason>>? keep, ResticSnapshot snapshot)
    {
        var reasons = keep?.GetValueOrDefault(snapshot.Id)?.ToList() ?? [];
        if (snapshot.Protected && !reasons.Contains(BackupKeepReason.Protected))
        {
            reasons.Add(BackupKeepReason.Protected);
        }
        return reasons;
    }

    /// <summary>The live path restic read, as the share's name and the path below it; unchanged when no source holds it.</summary>
    private static string ShareRelative(string path, IReadOnlyList<ResolvedSource> sources)
    {
        var source = sources
            .Where(source => path == source.Root || path.StartsWith(source.Root + "/", StringComparison.Ordinal))
            .MaxBy(source => source.Root.Length);
        return source is null ? path : (source.Share + path[source.Root.Length..]).TrimEnd('/');
    }

    private static async Task<VersionRow> VersionRowAsync(Microsoft.Data.Sqlite.SqliteConnection connection, long id, long versionId, CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<VersionRow>(new CommandDefinition(
            SelectVersion + " WHERE job_id = @id AND id = @versionId", new { id, versionId }, cancellationToken: cancellationToken))
        ?? throw new PithyxException(ErrorCodes.BackupVersionNotFound, new Dictionary<string, object?> { ["id"] = versionId });

    private static string KeepJson(IEnumerable<BackupKeepReason> reasons) =>
        JsonSerializer.Serialize(reasons.Distinct().Order().Select(Text));

    private static IEnumerable<BackupKeepReason> Reasons(string json) =>
        (JsonSerializer.Deserialize<string[]>(json) ?? [])
            .Select(text => Enum.TryParse<BackupKeepReason>(text, ignoreCase: true, out var reason) ? reason : (BackupKeepReason?)null)
            .OfType<BackupKeepReason>();

    private static BackupVersion View(VersionRow row) => new(
        row.Id,
        ResticVersions.ShortId(row.Snapshot),
        SessionService.Parse(row.Time),
        row.Files,
        row.Bytes,
        row.AddedBytes,
        row.FilesAdded,
        row.FilesChanged,
        row.FilesRemoved,
        row.Protected == 1,
        [.. Reasons(row.Keep).Distinct().Order()],
        row.Changes is null
            ? null
            : [.. (JsonSerializer.Deserialize<StoredChange[]>(row.Changes, _json) ?? [])
                .Select(change => Enum.TryParse<BackupChangeKind>(change.Change, ignoreCase: true, out var kind) ? new BackupVersionChange(change.Path, kind) : null)
                .OfType<BackupVersionChange>()]);

    private sealed record StoredChange(string Path, string Change);

    private sealed class VersionRow
    {
        public long Id { get; init; }
        public string Snapshot { get; init; } = "";
        public string Time { get; init; } = "";
        public long? Files { get; init; }
        public long? Bytes { get; init; }
        public long? AddedBytes { get; init; }
        public long? FilesAdded { get; init; }
        public long? FilesChanged { get; init; }
        public long? FilesRemoved { get; init; }
        public long Protected { get; init; }
        public string Keep { get; init; } = "[]";
        public string? Changes { get; init; }
        public string? Paths { get; init; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Version {Snapshot} of backup job {JobId} protected: {Protected}")]
    private partial void LogVersionProtection(long jobId, string snapshot, bool @protected);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Repository key of backup job {JobId} shown to an administrator")]
    private partial void LogKeyRevealed(long jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Versions of backup job {JobId} could not be read: {Reason}")]
    private partial void LogVersionsUnknown(long jobId, string reason);
}
