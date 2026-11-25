using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Microsoft.Win32.SafeHandles;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Files;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Users;

namespace Pithyx.Daemon.Backups;

/// <summary>
/// Browsing and restoring versions (#395, D419). Browsing reads a <see cref="BackupBrowser"/> session's mount by
/// handles; a restore is a box job of the administrator that lets restic restore into a private folder of the target
/// share and then moves the entries in place with <see cref="BackupRestoreMerge"/>.
/// </summary>
public sealed partial class BackupJobService
{
    public const string RestoreKind = "backup.restore";
    public const string VersionRestored = "backup.version.restored";
    public const string VersionDownloaded = "backup.version.downloaded";

    /// <summary>The most entries one restore takes.</summary>
    public const int MaxRestorePaths = 1000;

    /// <summary>The private folder a restore makes in the share; the job's id follows.</summary>
    public const string StagingPrefix = ".pithyx-restore-";

    /// <summary>Restores going on, by job id; they hold off runs, checks, changes of versions and deleting the job.</summary>
    private readonly ConcurrentDictionary<long, int> _restoring = new();

    /// <summary>
    /// Opens a browse session of the job unless one is open: reads the versions again, so a session sees what the
    /// repository holds, and mounts it. Refused while a run or check goes on.
    /// </summary>
    public async Task BrowseAsync(long id, CancellationToken cancellationToken)
    {
        JobRow row;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            row = await RowAsync(connection, id, cancellationToken);
        }
        if (row.Kind == PullKind)
        {
            throw Unsupported("browse");
        }
        if (browser.IsOpen(id))
        {
            await browser.OpenAsync(id, _ => LocationAsync(row, null, cancellationToken), cancellationToken);
            return;
        }
        if (!_active.TryAdd(id, 0))
        {
            throw Busy(row.Name);
        }
        try
        {
            var cacheDir = await CacheDirOfAsync(row, cancellationToken);
            var location = await LocationAsync(row, cacheDir, cancellationToken);
            try
            {
                await SyncVersionsAsync(row, location, null, null, [], cancellationToken);
                await browser.OpenAsync(id, _ => Task.FromResult(location), cancellationToken);
            }
            catch (PithyxException error) when (error.Code.StartsWith("backup.target.", StringComparison.Ordinal))
            {
                await NoteConnectionFailureAsync(row.TargetId, error, CancellationToken.None);
                throw;
            }
        }
        finally
        {
            _active.TryRemove(id, out _);
        }
    }

    /// <summary>A folder of a version, with what changed against the version before. Needs a browse session.</summary>
    public async Task<BackupFileList> FilesAsync(long id, long versionId, string? path, CancellationToken cancellationToken)
    {
        var name = BackupRestorePaths.Clean(path);
        var (version, previous, sources) = await VersionWithSourcesAsync(id, versionId, cancellationToken);
        using var root = browser.Version(id, version.Snapshot);
        if (name.Length == 0)
        {
            return new BackupFileList("", false, [.. sources
                .Select(source => StatBeneath(root, source.Path) is { } stat ? Entry(source.Name, stat, null) : null)
                .OfType<BackupFileEntry>()
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)]);
        }
        var stored = BackupRestorePaths.Stored(name, sources) ?? throw new PithyxException(ErrorCodes.FsEntryNotFound);
        using var folder = OpenStored(root, stored, LinuxFs.ReadOnly | LinuxFs.Directory);
        using var before = previous is null ? null : browser.Version(id, previous.Snapshot);
        using var compared = before is null ? null : TryOpenStored(before, previous!.Sources, name);
        var entries = new List<BackupFileEntry>();
        foreach (var (entryName, _) in LinuxFs.ReadNames(folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stat = FileService.TryStat(folder, entryName);
            if (stat is null)
            {
                continue;
            }
            BackupChangeKind? change = null;
            if (before is not null)
            {
                var old = compared is null ? null : FileService.TryStat(compared, entryName);
                change = old is null ? BackupChangeKind.Added
                    : old.Kind != stat.Kind || old.Modified != stat.Modified || (stat.Kind == FsKind.File && old.Size != stat.Size) ? BackupChangeKind.Modified
                    : null;
            }
            entries.Add(Entry(entryName, stat, change));
        }
        return new BackupFileList(name, before is not null, [.. entries
            .OrderBy(entry => entry.Kind != BackupFileKind.Folder)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>A file or folder in every version, newest first, and on the box now. Needs a browse session.</summary>
    public async Task<BackupFileHistory> HistoryAsync(long id, string? path, CancellationToken cancellationToken)
    {
        var name = BackupRestorePaths.Clean(path);
        if (name.Length == 0)
        {
            throw new PithyxException(ErrorCodes.FsEntryNotFound);
        }
        var (versions, roots) = await BrowseVersionsAsync(id, cancellationToken);
        var owners = await OwnerNamesAsync(cancellationToken);
        var result = new List<BackupFileVersion>();
        string? stored = null;
        foreach (var version in versions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var at = BackupRestorePaths.Stored(name, version.Sources);
            FsStat? stat = null;
            if (at is not null)
            {
                stored ??= at;
                using var root = browser.Version(id, version.Snapshot);
                stat = StatBeneath(root, at);
            }
            result.Add(stat is null
                ? new BackupFileVersion(version.Id, version.Time, null, null, null, null, null)
                : new BackupFileVersion(
                    version.Id,
                    version.Time,
                    Kind(stat.Kind),
                    stat.Kind == FsKind.File ? stat.Size : null,
                    stat.Modified,
                    owners.GetValueOrDefault(stat.Uid) ?? stat.Uid.ToString(CultureInfo.InvariantCulture),
                    "0" + Convert.ToString(stat.Mode & 0x1FF, 8).PadLeft(3, '0')));
        }
        BackupFileCurrent? current = null;
        if (stored is not null && BackupRestorePaths.Place(stored, roots) is { } place)
        {
            try
            {
                using var share = LinuxFs.OpenRoot(place.Root.Path);
                if ((place.Relative.Length == 0 ? LinuxFs.Stat(share, "") : StatBeneath(share, place.Relative)) is { } live)
                {
                    current = new BackupFileCurrent(Kind(live.Kind), live.Kind == FsKind.File ? live.Size : null, live.Modified);
                }
            }
            catch (FsError)
            {
                // The share's folder is not there now: nothing to compare with.
            }
        }
        return new BackupFileHistory(name, current, result);
    }

    /// <summary>
    /// A file of a version for download, or a folder, which the endpoint sends as a zip. Needs a browse session.
    /// The caller disposes the handle.
    /// </summary>
    public async Task<(SafeFileHandle Handle, FsStat Stat, string Name)> OpenVersionEntryAsync(ClaimsPrincipal caller, long id, long versionId, string? path, CancellationToken cancellationToken)
    {
        var name = BackupRestorePaths.Clean(path);
        var (version, _, sources) = await VersionWithSourcesAsync(id, versionId, cancellationToken);
        var stored = BackupRestorePaths.Stored(name, sources) ?? throw new PithyxException(ErrorCodes.FsEntryNotFound);
        using var root = browser.Version(id, version.Snapshot);
        var stat = StatBeneath(root, stored) ?? throw new PithyxException(ErrorCodes.FsEntryNotFound);
        if (stat.Kind is not (FsKind.File or FsKind.Folder))
        {
            throw new PithyxException(ErrorCodes.FsEntryNotFound);
        }
        var handle = OpenStored(root, stored, stat.Kind == FsKind.Folder ? LinuxFs.ReadOnly | LinuxFs.Directory : LinuxFs.ReadOnly);
        await audit.RecordAsync(caller, VersionDownloaded, version.JobName, new Dictionary<string, string>
        {
            ["version"] = ResticVersions.ShortId(version.Snapshot),
            ["path"] = name,
        }, CancellationToken.None);
        return (handle, stat, BackupRestorePaths.Last(name));
    }

    /// <summary>Writes a folder of a version into a zip, files and folders only; symlinks and devices are left out.</summary>
    public static async Task WriteZipAsync(SafeFileHandle folder, string name, Stream output, CancellationToken cancellationToken)
    {
        await using var held = new HeldWrites(output);
        await using (var zip = new ZipArchive(held, ZipArchiveMode.Create, leaveOpen: true))
        {
            await AddFolderAsync(zip, folder, name + "/", cancellationToken);
        }
        await held.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// ZipArchive writes each entry's header synchronously, which the response refuses: the header is held here and
    /// goes out with the next asynchronous write or flush.
    /// </summary>
    private sealed class HeldWrites(Stream inner) : Stream
    {
        private readonly MemoryStream _held = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await SendHeldAsync(cancellationToken);
            await inner.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _held.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _held.Write(buffer);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await SendHeldAsync(cancellationToken);
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _held.Dispose();
            }
            base.Dispose(disposing);
        }

        private async Task SendHeldAsync(CancellationToken cancellationToken)
        {
            if (_held.Length > 0)
            {
                await inner.WriteAsync(_held.GetBuffer().AsMemory(0, (int)_held.Length), cancellationToken);
                _held.SetLength(0);
            }
        }
    }

    /// <summary>
    /// Starts restoring entries of a version as a box job of the caller. Refused while a run or check goes on; runs,
    /// checks and changes of versions of the job wait for it.
    /// </summary>
    public async Task<Job> RestoreAsync(ClaimsPrincipal caller, long id, BackupRestoreRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Paths is not { Count: > 0 and <= MaxRestorePaths } || !Enum.IsDefined(request.Conflict))
        {
            throw new PithyxException(ErrorCodes.RequestBodyInvalid);
        }
        var (version, _, sources) = await VersionWithSourcesAsync(id, request.VersionId, cancellationToken);
        var names = request.Paths.Select(BackupRestorePaths.Clean).Distinct(StringComparer.Ordinal).ToList();
        var stored = names.Select(name => BackupRestorePaths.Stored(name, sources) ?? throw new PithyxException(ErrorCodes.FsEntryNotFound)).ToList();
        ResolvedRestoreTarget? into = null;
        if (request.Destination is { } destination)
        {
            var share = await shares.ResolveAsync(caller, destination.ShareId, write: true, cancellationToken);
            into = new ResolvedRestoreTarget(share.Path, BackupRestorePaths.Clean(destination.Path), share.Name);
            using var root = LinuxFs.OpenRoot(share.Path);
            using var _ = FileService.Open(root, into.Folder, LinuxFs.ReadOnly | LinuxFs.Directory);
        }
        var userId = CurrentUser.Id(caller);
        _restoring.AddOrUpdate(id, 1, (_, count) => count + 1);
        try
        {
            if (_active.ContainsKey(id))
            {
                throw Busy(version.JobName);
            }
            return await jobs.StartAsync(
                userId,
                RestoreKind,
                new { jobId = id, name = version.JobName, version = ResticVersions.ShortId(version.Snapshot), count = names.Count },
                context => RestoreWorkAsync(caller, id, version, names, stored, into, request.Conflict, context),
                cancellationToken,
                new JobOptions(OwnSlot: true));
        }
        catch
        {
            EndRestore(id);
            throw;
        }
    }

    private async Task RestoreWorkAsync(
        ClaimsPrincipal caller,
        long id,
        BrowseVersion version,
        List<string> names,
        List<string> stored,
        ResolvedRestoreTarget? into,
        BackupRestoreConflict conflict,
        JobContext context)
    {
        try
        {
            var row = await JobRowAsync(id);
            var location = await LocationAsync(row, await CacheDirOfAsync(row, context.Cancellation), context.Cancellation);
            var tally = new BackupRestoreTally();
            var roots = await ShareRootsAsync(context.Cancellation);
            // Each group goes through a staging folder in the share it ends up in, so the moves are renames.
            var entries = stored.Select((path, index) => (Path: path, Name: names[index])).ToList();
            List<(string Root, List<(string Path, string Name)> Items)> groups = into is not null
                ? [(into.Root, entries)]
                : [.. entries
                    .GroupBy(item => BackupRestorePaths.Place(item.Path, roots)?.Root.Path
                        ?? throw new PithyxException(ErrorCodes.BackupRestoreFailed, new Dictionary<string, object?> { ["reason"] = "place", ["path"] = item.Name }))
                    .Select(group => (group.Key, group.ToList()))];
            var errors = 0;
            foreach (var (shareRoot, items) in groups)
            {
                errors += await RestoreGroupAsync(row, location, version, shareRoot, items, into, conflict, tally, context);
            }
            await audit.RecordAsync(caller, VersionRestored, row.Name, new Dictionary<string, string>
            {
                ["version"] = ResticVersions.ShortId(version.Snapshot),
                ["paths"] = string.Join(", ", names.Take(20)) + (names.Count > 20 ? ", ..." : ""),
                ["destination"] = into is null ? "original" : (into.Share + "/" + into.Folder).TrimEnd('/'),
                ["conflict"] = JsonNamingPolicy.CamelCase.ConvertName(conflict.ToString()),
            }, CancellationToken.None);
            var shown = ResticVersions.ShortId(version.Snapshot);
            LogRestored(id, shown, names.Count, tally.Restored, tally.Replaced, tally.Renamed, tally.Skipped, errors);
            if (errors > 0)
            {
                throw new PithyxException(ErrorCodes.BackupRestoreFailed, new Dictionary<string, object?> { ["reason"] = "errors", ["count"] = errors, ["path"] = "" });
            }
            context.SetResult(new BackupRestoreResult(tally.Restored, tally.Replaced, tally.Renamed, tally.Skipped));
        }
        finally
        {
            EndRestore(id);
        }
    }

    /// <summary>Restores the entries of one share through its staging folder; returns how many entries restic could not restore.</summary>
    private async Task<int> RestoreGroupAsync(
        JobRow row,
        ResticRepository location,
        BrowseVersion version,
        string shareRoot,
        List<(string Path, string Name)> items,
        ResolvedRestoreTarget? into,
        BackupRestoreConflict conflict,
        BackupRestoreTally tally,
        JobContext context)
    {
        using var root = LinuxFs.OpenRoot(shareRoot);
        var stagingName = StagingPrefix + context.JobId.ToString(CultureInfo.InvariantCulture);
        LinuxFs.MakeDirectory(root, stagingName, 0x1C0);
        using var staging = LinuxFs.OpenBeneath(root, stagingName, LinuxFs.ReadOnly | LinuxFs.Directory | LinuxFs.NoFollow);
        try
        {
            // The share's default ACL would let its users into the folder.
            LinuxFs.RemoveAcls(staging);
            LinuxFs.ChangeMode(staging, 0x1C0);
            LinuxFs.MakeDirectory(staging, "r", 0x1C0);
            // restic reaches the folder through the daemon's handle, so a rename or a symlink in the share cannot redirect it.
            var target = string.Create(CultureInfo.InvariantCulture, $"/proc/{Environment.ProcessId}/fd/{staging.DangerousGetHandle()}/r");
            var report = new ResticReport();
            Commands.CommandResult result;
            try
            {
                result = await restic.RestoreAsync(location, version.Snapshot, target, [.. items.Select(item => item.Path)], report, context.Report, context.Cancellation);
            }
            catch (TimeoutException)
            {
                throw new PithyxException(ErrorCodes.BackupRunFailed, new Dictionary<string, object?> { ["reason"] = "timeout" });
            }
            if (result.ExitCode != 0 && report.Warnings == 0)
            {
                LogResticFailed(row.Id, result.ExitCode, result.StandardError.Length > 4000 ? result.StandardError[..4000] : result.StandardError);
                var failure = ResticOutput.Failure(result.ExitCode, result.StandardError, location.RemotePath)
                    ?? new PithyxException(ErrorCodes.BackupRunFailed, new Dictionary<string, object?> { ["reason"] = "exit", ["exitCode"] = result.ExitCode });
                if (failure.Code.StartsWith("backup.target.", StringComparison.Ordinal))
                {
                    await NoteConnectionFailureAsync(row.TargetId, failure, CancellationToken.None);
                }
                throw failure;
            }
            if (report.Warnings > 0)
            {
                LogRestoreErrors(row.Id, report.Warnings, string.Join(", ", report.WarningItems.Take(20).Select(item => item.Item + ": " + item.Message)));
            }
            using var restored = LinuxFs.OpenBeneath(staging, "r", LinuxFs.ReadOnly | LinuxFs.Directory | LinuxFs.NoFollow);
            try
            {
                if (into is null)
                {
                    // Back in place: the staged image of the share's folder merges into it, with the folders above each entry.
                    using var image = BackupRestoreMerge.OpenStaged(restored, shareRoot);
                    if (image is not null)
                    {
                        foreach (var (child, _) in LinuxFs.ReadNames(image))
                        {
                            BackupRestoreMerge.Entry(image, child, root, child, conflict, version.Time, tally, context.Cancellation);
                        }
                    }
                }
                else
                {
                    using var folder = FileService.Open(root, into.Folder, LinuxFs.ReadOnly | LinuxFs.Directory);
                    foreach (var (path, name) in items)
                    {
                        var slash = path.LastIndexOf('/');
                        using var parent = BackupRestoreMerge.OpenStaged(restored, slash <= 0 ? "/" : path[..slash]);
                        if (parent is not null && FileService.TryStat(parent, path[(slash + 1)..]) is not null)
                        {
                            BackupRestoreMerge.Entry(parent, path[(slash + 1)..], folder, BackupRestorePaths.Last(name), conflict, version.Time, tally, context.Cancellation);
                        }
                    }
                }
            }
            catch (FsError error) when (error.Errno == LinuxFs.EXDEV)
            {
                throw new PithyxException(ErrorCodes.BackupRestoreFailed, new Dictionary<string, object?> { ["reason"] = "file_system", ["path"] = items[0].Name }, error);
            }
            catch (FsError error)
            {
                throw FsErrors.From(error);
            }
            return report.Warnings;
        }
        finally
        {
            RemoveStaging(root, stagingName, staging);
        }
    }

    /// <summary>Removes what is left in the staging folder, then the folder, unless a user put something else under its name.</summary>
    private void RemoveStaging(SafeFileHandle root, string name, SafeFileHandle staging)
    {
        try
        {
            foreach (var (child, _) in LinuxFs.ReadNames(staging))
            {
                TrashService.RemoveTree(staging, child);
            }
            if (FileService.TryStat(root, name) is { } there && there.Id == LinuxFs.Stat(staging, "").Id)
            {
                LinuxFs.Unlink(root, name, folder: true);
            }
        }
        catch (FsError error)
        {
            LogStagingLeft(name, error.Errno);
        }
    }

    private void EndRestore(long id)
    {
        while (_restoring.TryGetValue(id, out var count))
        {
            if (count <= 1 ? _restoring.TryRemove(new KeyValuePair<long, int>(id, count)) : _restoring.TryUpdate(id, count - 1, count))
            {
                return;
            }
        }
    }

    private bool Restoring(long id) => _restoring.ContainsKey(id);

    /// <summary>The cache of the job on the pool of its first source that is a dataset; null without one.</summary>
    private async Task<string?> CacheDirOfAsync(JobRow row, CancellationToken cancellationToken)
    {
        var roots = await shares.ListIndexRootsAsync(cancellationToken);
        return Settings(row).Sources.Select(source => roots.FirstOrDefault(root => root.Id == source.ShareId)?.Dataset).FirstOrDefault(dataset => dataset is not null) is { } dataset
            ? await CacheDirAsync(dataset.Split('/')[0], row.Id, cancellationToken)
            : null;
    }

    private async Task<JobRow> JobRowAsync(long id)
    {
        await using var connection = await connections.OpenAsync(CancellationToken.None);
        return await RowAsync(connection, id, CancellationToken.None);
    }

    /// <summary>Every shared folder and home now, with the names the app shows.</summary>
    private async Task<List<BackupShareRoot>> ShareRootsAsync(CancellationToken cancellationToken)
    {
        var names = (await shares.ListPathsAsync(cancellationToken)).ToDictionary(share => share.Id, share => DisplayName(share.Name));
        return [.. (await shares.ListIndexRootsAsync(cancellationToken))
            .Where(root => names.ContainsKey(root.Id))
            .Select(root => new BackupShareRoot(root.Id, names[root.Id], root.Path))];
    }

    /// <summary>A version with its folders, and the version before it.</summary>
    private async Task<(BrowseVersion Version, BrowseVersion? Previous, IReadOnlyList<BackupVersionSource> Sources)> VersionWithSourcesAsync(long id, long versionId, CancellationToken cancellationToken)
    {
        var (versions, _) = await BrowseVersionsAsync(id, cancellationToken);
        var index = versions.FindIndex(version => version.Id == versionId);
        if (index < 0)
        {
            throw new PithyxException(ErrorCodes.BackupVersionNotFound, new Dictionary<string, object?> { ["id"] = versionId });
        }
        return (versions[index], index + 1 < versions.Count ? versions[index + 1] : null, versions[index].Sources);
    }

    /// <summary>Every version of the job, newest first, with its folders as the app shows them now.</summary>
    private async Task<(List<BrowseVersion> Versions, List<BackupShareRoot> Roots)> BrowseVersionsAsync(long id, CancellationToken cancellationToken)
    {
        JobRow row;
        List<VersionRow> rows;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            row = await RowAsync(connection, id, cancellationToken);
            if (row.Kind == PullKind)
            {
                throw Unsupported("restore");
            }
            rows = [.. await connection.QueryAsync<VersionRow>(new CommandDefinition(
                SelectVersion + " WHERE job_id = @id ORDER BY time DESC, id DESC", new { id }, cancellationToken: cancellationToken))];
        }
        var roots = await ShareRootsAsync(cancellationToken);
        // Versions read before the box kept their folders: the job's folders now.
        var fallback = Settings(row).Sources
            .Select(source => roots.FirstOrDefault(root => root.Id == source.ShareId) is { } root
                ? (root.Path.TrimEnd('/') + "/" + source.Path).TrimEnd('/')
                : null)
            .OfType<string>()
            .ToList();
        var versions = rows.Select(version =>
        {
            var paths = version.Paths is not null && JsonSerializer.Deserialize<List<string>>(version.Paths) is { Count: > 0 } stored ? stored : fallback;
            return new BrowseVersion(
                version.Id,
                version.Snapshot,
                SessionService.Parse(version.Time),
                row.Name,
                [.. paths.Select(path => new BackupVersionSource(BackupRestorePaths.Name(path, roots), path))]);
        }).ToList();
        return (versions, roots);
    }

    /// <summary>User names by uid from the accounts file.</summary>
    private async Task<Dictionary<uint, string>> OwnerNamesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = new Dictionary<uint, string>();
            foreach (var entry in PasswdParser.Parse(await File.ReadAllTextAsync(accounts.PasswdPath, cancellationToken)))
            {
                if (entry.Uid is >= 0 and <= uint.MaxValue)
                {
                    result.TryAdd((uint)entry.Uid, entry.Name);
                }
            }
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static async Task AddFolderAsync(ZipArchive zip, SafeFileHandle folder, string prefix, CancellationToken cancellationToken)
    {
        await (await zip.CreateEntry(prefix).OpenAsync(cancellationToken)).DisposeAsync();
        foreach (var (name, _) in LinuxFs.ReadNames(folder).OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stat = FileService.TryStat(folder, name);
            if (stat?.Kind == FsKind.Folder)
            {
                using var child = LinuxFs.OpenBeneath(folder, name, LinuxFs.ReadOnly | LinuxFs.Directory | LinuxFs.NoFollow);
                await AddFolderAsync(zip, child, prefix + name + "/", cancellationToken);
            }
            else if (stat?.Kind == FsKind.File)
            {
                using var file = LinuxFs.OpenBeneath(folder, name, LinuxFs.ReadOnly | LinuxFs.NoFollow);
                await using var input = new FileStream(file, FileAccess.Read, 1, isAsync: false);
                var entry = zip.CreateEntry(prefix + name, CompressionLevel.Fastest);
                entry.LastWriteTime = stat.Modified;
                await using var output = await entry.OpenAsync(cancellationToken);
                await input.CopyToAsync(output, 81920, cancellationToken);
            }
        }
    }

    private static BackupFileEntry Entry(string name, FsStat stat, BackupChangeKind? change) =>
        new(name, Kind(stat.Kind), stat.Kind == FsKind.File ? stat.Size : null, stat.Modified, change);

    private static BackupFileKind Kind(FsKind kind) => kind switch
    {
        FsKind.File => BackupFileKind.File,
        FsKind.Folder => BackupFileKind.Folder,
        FsKind.Symlink => BackupFileKind.Symlink,
        _ => BackupFileKind.Other,
    };

    /// <summary>A stored path below a version's folder, opened one name at a time without following a symlink.</summary>
    private static SafeFileHandle OpenStored(SafeFileHandle root, string stored, int flags)
    {
        try
        {
            return LinuxFs.OpenBeneath(root, stored.TrimStart('/'), flags | LinuxFs.NoFollow);
        }
        catch (FsError error)
        {
            throw FsErrors.From(error);
        }
    }

    private static SafeFileHandle? TryOpenStored(SafeFileHandle root, IReadOnlyList<BackupVersionSource> sources, string name)
    {
        if (BackupRestorePaths.Stored(name, sources) is not { } stored)
        {
            return null;
        }
        try
        {
            return LinuxFs.OpenBeneath(root, stored.TrimStart('/'), LinuxFs.ReadOnly | LinuxFs.Directory | LinuxFs.NoFollow);
        }
        catch (FsError)
        {
            return null;
        }
    }

    /// <summary>Metadata of a path below a folder without following its last part; null when it is not there.</summary>
    private static FsStat? StatBeneath(SafeFileHandle root, string path)
    {
        var relative = path.Trim('/');
        if (relative.Length == 0)
        {
            return LinuxFs.Stat(root, "");
        }
        var slash = relative.LastIndexOf('/');
        try
        {
            using var parent = LinuxFs.OpenBeneath(root, slash < 0 ? "." : relative[..slash], LinuxFs.Path | LinuxFs.Directory);
            return LinuxFs.Stat(parent, relative[(slash + 1)..]);
        }
        catch (FsError error) when (error.Errno is LinuxFs.ENOENT or LinuxFs.ENOTDIR or LinuxFs.ELOOP or LinuxFs.EXDEV)
        {
            return null;
        }
    }

    private sealed record BrowseVersion(long Id, string Snapshot, DateTimeOffset Time, string JobName, IReadOnlyList<BackupVersionSource> Sources);

    private sealed record ResolvedRestoreTarget(string Root, string Folder, string Share);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup job {JobId} restored {Count} entries of version {Snapshot}: {Restored} restored, {Replaced} replaced, {Renamed} kept next to one, {Skipped} skipped, {Errors} errors")]
    private partial void LogRestored(long jobId, string snapshot, int count, long restored, long replaced, long renamed, long skipped, int errors);

    [LoggerMessage(Level = LogLevel.Warning, Message = "restic could not restore {Count} entries of backup job {JobId}: {Items}")]
    private partial void LogRestoreErrors(long jobId, int count, string items);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Restore folder {Name} could not be removed: errno {Errno}")]
    private partial void LogStagingLeft(string name, int errno);
}
