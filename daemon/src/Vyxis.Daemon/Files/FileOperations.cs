using System.Globalization;
using System.Security.Claims;
using Microsoft.Win32.SafeHandles;
using Vyxis.Daemon.Errors;
using Vyxis.Daemon.Jobs;
using Vyxis.Daemon.Shares;

namespace Vyxis.Daemon.Files;

public enum FileOperationKind
{
    Copy,
    Move,
    Delete,
}

/// <summary>What happens when the target folder already has an entry of the same name.</summary>
public enum ConflictPolicy
{
    /// <summary>The job fails with <c>fs.entry.exists</c> and stops.</summary>
    Fail,
    /// <summary>The entry is left out and listed in the result.</summary>
    Skip,
    /// <summary>Files are replaced; folders are merged, replacing files inside them.</summary>
    Overwrite,
    /// <summary>The new entry gets a free name such as "Report (2).pdf".</summary>
    Rename,
}

/// <param name="ShareId">The share.</param>
/// <param name="Path">Path relative to the share.</param>
public sealed record FileLocation(long ShareId, string Path);

/// <param name="Kind">copy, move or delete.</param>
/// <param name="Items">The files and folders to work on, at most 1000.</param>
/// <param name="Target">The folder to copy or move into; not used for delete.</param>
/// <param name="OnConflict">For copy and move; fail when left out.</param>
/// <param name="Permanent">For delete: remove for good instead of moving to the share's recycle bin. Only when the user asked for it.</param>
public sealed record FileOperationRequest(FileOperationKind Kind, IReadOnlyList<FileLocation> Items, FileLocation? Target = null, ConflictPolicy? OnConflict = null, bool Permanent = false);

/// <summary>The result of a finished file operation job.</summary>
/// <param name="Done">Entries copied, moved or deleted (folders count once, plus their contents).</param>
/// <param name="Skipped">Paths left out: name conflicts with <c>skip</c>, and symlinks, which are never copied.</param>
public sealed record FileOperationResult(long Done, IReadOnlyList<string> Skipped);

/// <summary>
/// Copying, moving and deleting as jobs with progress. Access is checked when the job is started; every path is
/// opened below its share root like any other file call. A copy writes each file under a hidden temporary name and
/// renames it into place when complete, so a cancelled or failed copy never leaves half a file under the real name.
/// Moves within one file system are renames; across file systems they copy, then delete the source.
/// </summary>
public sealed class FileOperations(ShareService shares, JobService jobs, TrashService trash)
{
    public const int MaxItems = 1000;
    private const long CopyChunk = 16L * 1024 * 1024;

    public async Task<Job> StartAsync(ClaimsPrincipal user, FileOperationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Items is not { Count: > 0 and <= MaxItems } || (request.Kind != FileOperationKind.Delete) != (request.Target is not null))
        {
            throw new VyxisException(ErrorCodes.FsOperationInvalid);
        }
        var writesSource = request.Kind != FileOperationKind.Copy;
        var sources = new List<(ResolvedShare Share, string Path)>(request.Items.Count);
        var resolved = new Dictionary<long, ResolvedShare>();
        foreach (var item in request.Items)
        {
            var path = FsPath.Normalize(item.Path);
            if (path.Length == 0)
            {
                throw new VyxisException(ErrorCodes.FsOperationInvalid);
            }
            sources.Add((await ResolveAsync(user, item.ShareId, writesSource, resolved, cancellationToken), path));
        }
        (ResolvedShare Share, string Path)? target = null;
        if (request.Target is { } destination)
        {
            var share = await shares.ResolveAsync(user, destination.ShareId, write: true, cancellationToken);
            var path = FsPath.Normalize(destination.Path);
            foreach (var (source, sourcePath) in sources)
            {
                if (source.Id == share.Id && FsPath.IsSameOrInside(path, sourcePath))
                {
                    throw new VyxisException(ErrorCodes.FsOperationIntoItself);
                }
            }
            using var root = FileService.OpenRoot(share);
            using var folder = FileService.Open(root, path, LinuxFs.Path | LinuxFs.Directory);
            target = (share, path);
        }

        var policy = request.OnConflict ?? ConflictPolicy.Fail;
        var toTrash = request.Kind == FileOperationKind.Delete && !request.Permanent;
        var kind = toTrash ? "trash" : request.Kind.ToString().ToLowerInvariant();
        var userId = CurrentUserId(user);
        return await jobs.StartAsync(userId, $"files.{kind}", request, context => Task.Run(() => Run(request.Kind, toTrash, userId, sources, target, policy, context), context.Cancellation), cancellationToken);
    }

    /// <summary>Puts recycle bin items back where they were, recreating missing parent folders.</summary>
    public async Task<Job> StartRestoreAsync(ClaimsPrincipal user, long shareId, TrashItemsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (share, items) = await ResolveTrashAsync(user, shareId, request.Ids, cancellationToken);
        var policy = request.OnConflict ?? ConflictPolicy.Rename;
        return await jobs.StartAsync(CurrentUserId(user), "files.restore", request, context => Task.Run(() => Restore(share, items, policy, context), context.Cancellation), cancellationToken);
    }

    /// <summary>Deletes recycle bin items for good; all of them when <paramref name="ids"/> is null (emptying the bin).</summary>
    public async Task<Job> StartPurgeAsync(ClaimsPrincipal user, long shareId, IReadOnlyList<long>? ids, CancellationToken cancellationToken)
    {
        var (share, items) = await ResolveTrashAsync(user, shareId, ids, cancellationToken);
        return await jobs.StartAsync(CurrentUserId(user), ids is null ? "files.empty_trash" : "files.purge", new { shareId, ids }, context => Task.Run(() => Purge(share, items, context), context.Cancellation), cancellationToken);
    }

    private async Task<(ResolvedShare Share, IReadOnlyList<TrashItem> Items)> ResolveTrashAsync(ClaimsPrincipal user, long shareId, IReadOnlyList<long>? ids, CancellationToken cancellationToken)
    {
        if (ids is not null and not { Count: > 0 and <= MaxItems })
        {
            throw new VyxisException(ErrorCodes.FsOperationInvalid);
        }
        var share = await shares.ResolveAsync(user, shareId, write: true, cancellationToken);
        return (share, await trash.GetAsync(share.Id, ids, cancellationToken));
    }

    private void Restore(ResolvedShare share, IReadOnlyList<TrashItem> items, ConflictPolicy policy, JobContext context)
    {
        var run = new Progress(context);
        using var root = FileService.OpenRoot(share);
        using var bin = TrashService.OpenFolder(root);
        foreach (var item in items)
        {
            run.Count(root, FsPath.Join(TrashService.Folder, Stored(item)));
        }
        run.Report(null);
        foreach (var item in items)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var stored = Stored(item);
            if (FileService.TryStat(bin, stored) is null)
            {
                // Gone from the bin (removed outside Vyxis): nothing to restore.
                trash.Forget(item.Id);
                continue;
            }
            var (parent, name) = FsPath.Split(item.OriginalPath);
            using var folder = MakeParents(root, parent);
            var targetName = name;
            if (FileService.TryStat(folder, name) is not null)
            {
                switch (policy)
                {
                    case ConflictPolicy.Skip:
                        run.Skip(item.OriginalPath);
                        continue;
                    case ConflictPolicy.Fail:
                        throw new VyxisException(ErrorCodes.FsEntryExists, new Dictionary<string, object?> { ["name"] = name });
                    case ConflictPolicy.Rename:
                        targetName = FreeName(folder, name);
                        break;
                }
            }
            try
            {
                LinuxFs.Rename(bin, stored, folder, targetName, replace: policy == ConflictPolicy.Overwrite);
            }
            catch (FsError error) when (error.Errno == LinuxFs.EXDEV)
            {
                // The item came from another file system (a nested dataset): copy it back.
                Copy(bin, stored, item.OriginalPath, folder, ConflictPolicy.Fail, run, targetName);
                TrashService.RemoveTree(bin, stored);
            }
            catch (FsError error)
            {
                throw FsErrors.From(error, targetName);
            }
            trash.Forget(item.Id);
            run.Add(run.CountOf(FsPath.Join(TrashService.Folder, stored)));
        }
        context.SetResult(new FileOperationResult(run.Done, run.Skipped));
        run.Report(null);
    }

    private void Purge(ResolvedShare share, IReadOnlyList<TrashItem> items, JobContext context)
    {
        var run = new Progress(context);
        using var root = FileService.OpenRoot(share);
        using var bin = TrashService.OpenFolder(root);
        foreach (var item in items)
        {
            if (FileService.TryStat(bin, Stored(item)) is not null)
            {
                run.Count(root, FsPath.Join(TrashService.Folder, Stored(item)));
            }
        }
        run.Report(null);
        foreach (var item in items)
        {
            Delete(bin, Stored(item), FsPath.Join(TrashService.Folder, Stored(item)), run);
            trash.Forget(item.Id);
        }
        context.SetResult(new FileOperationResult(run.Done, run.Skipped));
        run.Report(null);
    }

    private static string Stored(TrashItem item) => item.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>Opens a folder below the root, creating every missing folder on the way.</summary>
    private static SafeFileHandle MakeParents(SafeFileHandle root, string path)
    {
        var folder = FileService.Open(root, "", LinuxFs.Path | LinuxFs.Directory);
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                LinuxFs.MakeDirectory(folder, segment);
            }
            catch (FsError error) when (error.Errno == LinuxFs.EEXIST)
            {
                // Already there.
            }
            catch (FsError error)
            {
                folder.Dispose();
                throw FsErrors.From(error, segment);
            }
            var next = Child(folder, segment, LinuxFs.Path | LinuxFs.Directory);
            folder.Dispose();
            folder = next;
        }
        return folder;
    }

    /// <summary>Moves an entry into its share's recycle bin; across file systems it copies, then deletes.</summary>
    private void MoveToTrash(ResolvedShare share, SafeFileHandle root, SafeFileHandle folder, string name, string path, long userId, Progress run)
    {
        var stat = FileService.TryStat(folder, name);
        if (stat is null)
        {
            return;
        }
        using var bin = TrashService.OpenFolder(root);
        var id = trash.Reserve(share.Id, path, stat.Kind, run.BytesOf(path), userId);
        var stored = id.ToString(CultureInfo.InvariantCulture);
        try
        {
            try
            {
                LinuxFs.Rename(folder, name, bin, stored);
            }
            catch (FsError error) when (error.Errno == LinuxFs.EXDEV)
            {
                Copy(folder, name, path, bin, ConflictPolicy.Fail, run, stored);
                Delete(folder, name, path, run, counted: false);
                return;
            }
            catch (FsError error)
            {
                throw FsErrors.From(error, name);
            }
            run.Add(run.CountOf(path));
            run.Report(path);
        }
        catch
        {
            trash.Forget(id);
            throw;
        }
    }

    private async Task<ResolvedShare> ResolveAsync(ClaimsPrincipal user, long shareId, bool write, Dictionary<long, ResolvedShare> resolved, CancellationToken cancellationToken)
    {
        if (!resolved.TryGetValue(shareId, out var share))
        {
            share = await shares.ResolveAsync(user, shareId, write, cancellationToken);
            resolved[shareId] = share;
        }
        return share;
    }

    private static long CurrentUserId(ClaimsPrincipal user) => Auth.CurrentUser.Id(user);

    private void Run(FileOperationKind kind, bool toTrash, long userId, List<(ResolvedShare Share, string Path)> sources, (ResolvedShare Share, string Path)? target, ConflictPolicy policy, JobContext context)
    {
        var run = new Progress(context);
        foreach (var (share, path) in sources)
        {
            using var root = FileService.OpenRoot(share);
            run.Count(root, path);
        }
        run.Report(null);

        foreach (var (share, path) in sources)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            using var root = FileService.OpenRoot(share);
            var (parent, name) = FsPath.Split(path);
            using var sourceFolder = FileService.Open(root, parent, LinuxFs.Path | LinuxFs.Directory);
            if (kind == FileOperationKind.Delete)
            {
                if (toTrash)
                {
                    MoveToTrash(share, root, sourceFolder, name, path, userId, run);
                }
                else
                {
                    Delete(sourceFolder, name, path, run);
                }
                continue;
            }
            var (targetShare, targetPath) = target!.Value;
            using var targetRoot = FileService.OpenRoot(targetShare);
            using var targetFolder = FileService.Open(targetRoot, targetPath, LinuxFs.Path | LinuxFs.Directory);
            if (kind == FileOperationKind.Move && TryRename(sourceFolder, name, targetFolder, path, policy, run))
            {
                continue;
            }
            Copy(sourceFolder, name, path, targetFolder, policy, run);
            if (kind == FileOperationKind.Move && !run.SkippedBelow(path))
            {
                Delete(sourceFolder, name, path, run, counted: false);
            }
        }
        context.SetResult(new FileOperationResult(run.Done, run.Skipped));
        run.Report(null);
    }

    /// <summary>A move within one file system. False when it has to copy instead (another file system, or a folder merge).</summary>
    private static bool TryRename(SafeFileHandle fromFolder, string name, SafeFileHandle toFolder, string path, ConflictPolicy policy, Progress run)
    {
        var stat = FileService.Stat(fromFolder, name);
        var targetName = name;
        if (FileService.TryStat(toFolder, name) is { } existing)
        {
            if (SameEntry(stat, existing))
            {
                // Moving into the folder it is already in.
                run.Add(run.CountOf(path));
                return true;
            }
            switch (policy)
            {
                case ConflictPolicy.Skip:
                    run.Skip(path);
                    return true;
                case ConflictPolicy.Rename:
                    targetName = FreeName(toFolder, name);
                    break;
                case ConflictPolicy.Overwrite when stat.Kind == FsKind.Folder || existing.Kind == FsKind.Folder:
                    return false;
                case ConflictPolicy.Fail:
                    throw new VyxisException(ErrorCodes.FsEntryExists, new Dictionary<string, object?> { ["name"] = name });
            }
        }
        try
        {
            LinuxFs.Rename(fromFolder, name, toFolder, targetName, replace: policy == ConflictPolicy.Overwrite);
            run.Add(run.CountOf(path));
            return true;
        }
        catch (FsError error) when (error.Errno == LinuxFs.EXDEV)
        {
            return false;
        }
        catch (FsError error)
        {
            throw FsErrors.From(error, targetName);
        }
    }

    private static void Copy(SafeFileHandle fromFolder, string name, string path, SafeFileHandle toFolder, ConflictPolicy policy, Progress run, string? asName = null)
    {
        run.Cancellation.ThrowIfCancellationRequested();
        var stat = FileService.Stat(fromFolder, name);
        if (stat.Kind is not (FsKind.File or FsKind.Folder))
        {
            run.Skip(path);
            return;
        }
        var targetName = asName ?? name;
        var existing = FileService.TryStat(toFolder, targetName);
        if (existing is not null)
        {
            switch (policy)
            {
                case ConflictPolicy.Skip:
                    run.Skip(path);
                    return;
                case ConflictPolicy.Rename:
                    targetName = FreeName(toFolder, name);
                    existing = null;
                    break;
                case ConflictPolicy.Fail:
                    throw new VyxisException(ErrorCodes.FsEntryExists, new Dictionary<string, object?> { ["name"] = name });
            }
        }

        run.Report(path);
        if (stat.Kind == FsKind.Folder)
        {
            if (existing is null)
            {
                Make(toFolder, targetName);
            }
            else if (existing.Kind != FsKind.Folder)
            {
                throw new VyxisException(ErrorCodes.FsEntryExists, new Dictionary<string, object?> { ["name"] = name });
            }
            using var source = Child(fromFolder, name, LinuxFs.ReadOnly | LinuxFs.Directory);
            using var destination = Child(toFolder, targetName, LinuxFs.Path | LinuxFs.Directory);
            foreach (var (child, _) in Names(source))
            {
                Copy(source, child, FsPath.Join(path, child), destination, policy == ConflictPolicy.Rename ? ConflictPolicy.Fail : policy, run);
            }
            run.Add(1);
            return;
        }

        var temporary = $"{FsPath.UploadPrefix}{Guid.NewGuid():N}";
        try
        {
            using (var input = Child(fromFolder, name, LinuxFs.ReadOnly))
            using (var output = Child(toFolder, temporary, LinuxFs.WriteOnly | LinuxFs.Create | LinuxFs.Exclusive, 0x1a4))
            {
                CopyContent(input, output, run);
            }
            try
            {
                LinuxFs.Rename(toFolder, temporary, toFolder, targetName, replace: policy == ConflictPolicy.Overwrite);
            }
            catch (FsError error)
            {
                throw FsErrors.From(error, targetName);
            }
            run.Add(1);
        }
        finally
        {
            FileService.TryUnlink(toFolder, temporary);
        }
    }

    private static void CopyContent(SafeFileHandle input, SafeFileHandle output, Progress run)
    {
        var offset = 0L;
        while (true)
        {
            run.Cancellation.ThrowIfCancellationRequested();
            long copied;
            try
            {
                copied = LinuxFs.CopyRange(input, output, CopyChunk);
            }
            catch (FsError error)
            {
                throw FsErrors.From(error);
            }
            if (copied == 0)
            {
                return;
            }
            if (copied < 0)
            {
                // The file systems cannot copy in the kernel: copy through a buffer from where it stopped.
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = RandomAccess.Read(input, buffer, offset)) > 0)
                {
                    run.Cancellation.ThrowIfCancellationRequested();
                    RandomAccess.Write(output, buffer.AsSpan(0, read), offset);
                    offset += read;
                    run.AddBytes(read);
                }
                return;
            }
            offset += copied;
            run.AddBytes(copied);
        }
    }

    private static void Delete(SafeFileHandle folder, string name, string path, Progress run, bool counted = true)
    {
        run.Cancellation.ThrowIfCancellationRequested();
        var stat = FileService.TryStat(folder, name);
        if (stat is null)
        {
            return;
        }
        if (stat.Kind == FsKind.Folder)
        {
            using (var child = Child(folder, name, LinuxFs.ReadOnly | LinuxFs.Directory))
            {
                foreach (var (entry, _) in Names(child))
                {
                    Delete(child, entry, FsPath.Join(path, entry), run, counted);
                }
            }
            Unlink(folder, name, isFolder: true);
        }
        else
        {
            Unlink(folder, name, isFolder: false);
        }
        if (counted)
        {
            run.Add(1);
            run.Report(path);
        }
    }

    private static void Unlink(SafeFileHandle folder, string name, bool isFolder)
    {
        try
        {
            LinuxFs.Unlink(folder, name, isFolder);
        }
        catch (FsError error) when (error.Errno != LinuxFs.ENOENT)
        {
            throw FsErrors.From(error, name);
        }
        catch (FsError)
        {
            // Deleted in the meantime.
        }
    }

    private static void Make(SafeFileHandle folder, string name)
    {
        try
        {
            LinuxFs.MakeDirectory(folder, name);
        }
        catch (FsError error)
        {
            throw FsErrors.From(error, name);
        }
    }

    private static SafeFileHandle Child(SafeFileHandle folder, string name, int flags, uint mode = 0)
    {
        try
        {
            return LinuxFs.OpenBeneath(folder, name, flags, mode);
        }
        catch (FsError error)
        {
            throw FsErrors.From(error, name);
        }
    }

    private static List<(string Name, FsKind Kind)> Names(SafeFileHandle folder)
    {
        try
        {
            var names = LinuxFs.ReadNames(folder);
            names.RemoveAll(entry => entry.Name.StartsWith(FsPath.UploadPrefix, StringComparison.Ordinal));
            return names;
        }
        catch (FsError error)
        {
            throw FsErrors.From(error);
        }
    }

    private static bool SameEntry(FsStat a, FsStat b) => a.Id == b.Id;

    /// <summary>"Report.pdf" becomes "Report (2).pdf", "Report (3).pdf" and so on, whichever is free first.</summary>
    internal static string FreeName(SafeFileHandle folder, string name)
    {
        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        for (var number = 2; number < 10_000; number++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{stem} ({number}){extension}");
            if (FsPath.IsName(candidate) && FileService.TryStat(folder, candidate) is null)
            {
                return candidate;
            }
        }
        throw new VyxisException(ErrorCodes.FsEntryExists, new Dictionary<string, object?> { ["name"] = name });
    }

    /// <summary>Totals, progress and the skipped paths of one job.</summary>
    private sealed class Progress(JobContext context)
    {
        private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _sizes = new(StringComparer.Ordinal);
        private long _items;
        private long _bytes;
        private long _bytesDone;

        public CancellationToken Cancellation => context.Cancellation;

        public long Done { get; private set; }

        public List<string> Skipped { get; } = [];

        public void Count(SafeFileHandle root, string path)
        {
            var (parent, name) = FsPath.Split(path);
            using var folder = FileService.Open(root, parent, LinuxFs.Path | LinuxFs.Directory);
            var before = _items;
            var bytesBefore = _bytes;
            Walk(folder, name);
            _counts[path] = _items - before;
            _sizes[path] = _bytes - bytesBefore;
        }

        public long CountOf(string path) => _counts.GetValueOrDefault(path, 1);

        public long BytesOf(string path) => _sizes.GetValueOrDefault(path);

        private void Walk(SafeFileHandle folder, string name)
        {
            Cancellation.ThrowIfCancellationRequested();
            var stat = FileService.Stat(folder, name);
            _items++;
            if (stat.Kind == FsKind.File)
            {
                _bytes += stat.Size;
            }
            else if (stat.Kind == FsKind.Folder)
            {
                using var child = Child(folder, name, LinuxFs.ReadOnly | LinuxFs.Directory);
                foreach (var (entry, _) in Names(child))
                {
                    Walk(child, entry);
                }
            }
        }

        public void Add(long items) => Done += items;

        public void AddBytes(long bytes)
        {
            _bytesDone += bytes;
            Report(null);
        }

        public void Skip(string path)
        {
            Skipped.Add(path);
            Done += CountOf(path);
        }

        public bool SkippedBelow(string path) => Skipped.Any(skipped => FsPath.IsSameOrInside(skipped, path));

        public void Report(string? current) =>
            context.Report(new JobProgress(Math.Min(Done, _items), _items, Math.Min(_bytesDone, _bytes), _bytes, current));
    }
}
