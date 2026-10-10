using System.Globalization;
using System.Security.Claims;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Files;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Shares;

namespace Pithyx.Daemon.Apps;

/// <summary>Where an app's file call starts: a shared folder by id, or a storage request of its manifest.</summary>
/// <param name="Share">The shared folder's id, as the app got it from <c>files/shares</c>.</param>
/// <param name="Storage">The id of a storage request of the manifest.</param>
public sealed record AppFileLocation(string? Share, string? Storage);

/// <summary>
/// Files for third-party apps (D69, D70, D318): always as the user, through the same file service as the desktop, so
/// the user's own rights on the shared folder and the kernel's beneath-the-root check apply. On top of that the app
/// needs its own permission: a shared folder needs <c>files.shares</c>, and reading or writing its files
/// <c>files.read</c> or <c>files.write</c>. A storage request needs no <c>files.*</c> permission (the administrator
/// mapped it), but never reaches beyond the mapped folder or beyond the access of the request and the mapping.
/// </summary>
public sealed class AppFiles(
    FileService files,
    ShareService shares,
    FileOperations operations,
    TrashService trash,
    PreviousVersions versions,
    JobService jobs,
    AppHandles handles)
{
    public const string SharesPermission = "files.shares";
    public const string ReadPermission = "files.read";
    public const string WritePermission = "files.write";

    private sealed record Target(long ShareId, string Within, bool MayWrite);

    /// <summary>Jobs apps started and for whom, so an app can cancel only its own; the oldest are forgotten first.</summary>
    private readonly Queue<long> _jobOrder = new();
    private readonly Dictionary<long, (string AppId, long UserId)> _jobOwners = [];
    private const int MaxRememberedJobs = 4096;

    public async Task<IReadOnlyList<AppShareInfo>> SharesAsync(InstalledApp app, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        Require(app, SharesPermission);
        return [.. (await shares.ListAsync(user, cancellationToken)).Select(share => new AppShareInfo(
            share.Id.ToString(CultureInfo.InvariantCulture),
            share.Name,
            share.Access,
            share.Unavailable switch
            {
                ShareAvailability.PoolMissing => "poolMissing",
                ShareAvailability.PoolUnavailable => "poolUnavailable",
                ShareAvailability.NotMounted => "notMounted",
                _ => null,
            }))];
    }

    public async Task<AppFolderPage> ListAsync(InstalledApp app, ClaimsPrincipal user, AppFileLocation location, string? path, FileSort sort, SortOrder order, int? limit, string? cursor, CancellationToken cancellationToken)
    {
        var target = Resolve(app, null, location, null, write: false, content: false);
        var page = await files.ListAsync(user, target.ShareId, path, sort, order, limit, cursor, cancellationToken, target.Within);
        return new AppFolderPage(page.Path, page.FolderId, page.Total, page.Entries, page.NextCursor, page.Topic);
    }

    public async Task<AppFileEntry> StatAsync(InstalledApp app, ClaimsPrincipal user, AppFileLocation location, string? path, CancellationToken cancellationToken)
    {
        var target = Resolve(app, null, location, null, write: false, content: false);
        var entry = await files.StatAsync(user, target.ShareId, path, cancellationToken, target.Within);
        return await WithWritableAsync(user, target, entry, cancellationToken);
    }

    public async Task<(FileStream Stream, FsStat Stat, string Name)> OpenReadAsync(InstalledApp app, AppTokenGrant grant, ClaimsPrincipal user, AppFileLocation location, string? path, CancellationToken cancellationToken)
    {
        var target = Resolve(app, grant, location, path, write: false, content: true);
        return await files.OpenReadAsync(user, target.ShareId, path, cancellationToken, target.Within);
    }

    public async Task<AppFileEntry> WriteAsync(InstalledApp app, AppTokenGrant grant, ClaimsPrincipal user, AppFileLocation location, string? path, Stream body, long? length, bool overwrite, CancellationToken cancellationToken)
    {
        var target = Resolve(app, grant, location, path, write: true, content: true);
        var entry = await files.UploadAsync(user, target.ShareId, path, body, length, overwrite, cancellationToken, target.Within);
        return Writable(entry, true);
    }

    public async Task<AppFileEntry> CreateFolderAsync(InstalledApp app, ClaimsPrincipal user, AppFileLocation location, string? path, CancellationToken cancellationToken)
    {
        var target = Resolve(app, null, location, null, write: true, content: true);
        var entry = await files.CreateFolderAsync(user, target.ShareId, path, cancellationToken, target.Within);
        return Writable(entry, true);
    }

    public async Task<AppFileEntry> RenameAsync(InstalledApp app, ClaimsPrincipal user, AppFileLocation location, string? path, string? name, CancellationToken cancellationToken)
    {
        var target = Resolve(app, null, location, null, write: true, content: true);
        var entry = await files.RenameAsync(user, target.ShareId, path, name, cancellationToken, target.Within);
        return Writable(entry, true);
    }

    /// <summary>The app hands the user's file to the desktop's dialog result: this window may read it, or write it too.</summary>
    public void Hand(InstalledApp app, AppTokenGrant grant, string? share, string? path, bool write)
    {
        Require(app, write ? WritePermission : ReadPermission);
        handles.Add(grant, ParseShare(share), FsPath.Normalize(path), write);
    }

    /// <summary>Checks that the file can be read now, for the short-lived URL <c>files.url</c> hands out.</summary>
    public async Task CheckReadableAsync(InstalledApp app, AppTokenGrant grant, ClaimsPrincipal user, AppFileLocation location, string? path, CancellationToken cancellationToken)
    {
        var (stream, _, _) = await OpenReadAsync(app, grant, user, location, path, cancellationToken);
        await stream.DisposeAsync();
    }

    public async Task<Job> StartOperationAsync(InstalledApp app, ClaimsPrincipal user, FileOperationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(app, SharesPermission);
        if (request.Kind != FileOperationKind.Delete)
        {
            Require(app, ReadPermission);
        }
        Require(app, WritePermission);
        return Started(app, user, await operations.StartAsync(user, request, cancellationToken));
    }

    public async Task<FileVersionList> VersionsAsync(InstalledApp app, ClaimsPrincipal user, string? share, string? path, CancellationToken cancellationToken)
    {
        RequireShare(app, ReadPermission);
        return await versions.ListAsync(user, ParseShare(share), path, cancellationToken);
    }

    public async Task<Job> RestoreVersionAsync(InstalledApp app, ClaimsPrincipal user, string? share, FileVersionRestoreRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireShare(app, ReadPermission);
        Require(app, WritePermission);
        return Started(app, user, await versions.StartRestoreAsync(user, ParseShare(share), request, cancellationToken));
    }

    public async Task<TrashPage> TrashAsync(InstalledApp app, ClaimsPrincipal user, string? share, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        RequireShare(app, ReadPermission);
        return await trash.ListAsync(user, ParseShare(share), cursor, limit, cancellationToken);
    }

    public async Task<Job> RestoreTrashAsync(InstalledApp app, ClaimsPrincipal user, string? share, TrashItemsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireShare(app, WritePermission);
        return Started(app, user, await operations.StartRestoreAsync(user, ParseShare(share), request, cancellationToken));
    }

    /// <summary>Deletes items of the recycle bin for good; without <paramref name="ids"/> all of them.</summary>
    public async Task<Job> PurgeTrashAsync(InstalledApp app, ClaimsPrincipal user, string? share, IReadOnlyList<long>? ids, CancellationToken cancellationToken)
    {
        RequireShare(app, WritePermission);
        return Started(app, user, await operations.StartPurgeAsync(user, ParseShare(share), ids, cancellationToken));
    }

    /// <summary>Cancels a job this app started for the user; any other job is <c>jobs.job.not_found</c>.</summary>
    public async Task CancelJobAsync(InstalledApp app, ClaimsPrincipal user, long jobId, CancellationToken cancellationToken)
    {
        bool own;
        lock (_jobOwners)
        {
            own = _jobOwners.TryGetValue(jobId, out var owner) && owner == (app.Id, CurrentUser.Id(user));
        }
        if (!own)
        {
            throw new PithyxException(ErrorCodes.JobsJobNotFound);
        }
        await jobs.CancelAsync(user, jobId, cancellationToken);
    }

    private Job Started(InstalledApp app, ClaimsPrincipal user, Job job)
    {
        lock (_jobOwners)
        {
            if (_jobOwners.TryAdd(job.Id, (app.Id, CurrentUser.Id(user))))
            {
                _jobOrder.Enqueue(job.Id);
                if (_jobOrder.Count > MaxRememberedJobs)
                {
                    _jobOwners.Remove(_jobOrder.Dequeue());
                }
            }
        }
        return job;
    }

    private static long ParseShare(string? share) =>
        long.TryParse(share, NumberStyles.None, CultureInfo.InvariantCulture, out var shareId) && shareId > 0
            ? shareId
            : throw new PithyxException(ErrorCodes.FsShareNotFound);

    /// <summary>The permissions of the file manager methods: a shared folder, and for what is done there read or write.</summary>
    private static void RequireShare(InstalledApp app, string permission)
    {
        Require(app, SharesPermission);
        Require(app, permission);
    }

    /// <summary>The shared folder and folder a call goes to, after the app's own checks; the user's rights come after.</summary>
    private Target Resolve(InstalledApp app, AppTokenGrant? grant, AppFileLocation location, string? path, bool write, bool content)
    {
        ArgumentNullException.ThrowIfNull(location);
        if ((location.Share is null) == (location.Storage is null))
        {
            throw new PithyxException(ErrorCodes.FsOperationInvalid);
        }
        if (location.Share is { } share)
        {
            var shareId = ParseShare(share);
            // A file the user handed to this window needs no files.shares: the choice in the dialog was the grant.
            if (!(content && grant is not null && handles.Allows(grant, shareId, FsPath.Normalize(path), write)))
            {
                Require(app, SharesPermission);
            }
            if (content)
            {
                Require(app, write ? WritePermission : ReadPermission);
            }
            return new Target(shareId, "", app.Holds(WritePermission));
        }
        var storage = location.Storage!;
        var request = app.StorageRequests.FirstOrDefault(candidate => candidate.Id == storage);
        var mapping = app.Storage.FirstOrDefault(candidate => candidate.StorageId == storage);
        if (request.Id is null || mapping is null)
        {
            throw new PithyxException(ErrorCodes.AppStorageNotFound, new Dictionary<string, object?> { ["storage"] = storage.Length > 64 ? storage[..64] : storage });
        }
        var mayWrite = request.Access == "readWrite" && mapping.Access == AppStorageAccess.ReadWrite;
        if (write && !mayWrite)
        {
            throw new PithyxException(ErrorCodes.FsShareReadOnly);
        }
        return new Target(mapping.ShareId, FsPath.Normalize(mapping.Path), mayWrite);
    }

    private async Task<AppFileEntry> WithWritableAsync(ClaimsPrincipal user, Target target, FileEntry entry, CancellationToken cancellationToken)
    {
        if (!target.MayWrite)
        {
            return Writable(entry, false);
        }
        var share = await shares.ResolveAsync(user, target.ShareId, write: false, cancellationToken);
        return Writable(entry, share.Access == ShareAccess.ReadWrite);
    }

    private static AppFileEntry Writable(FileEntry entry, bool writable) =>
        new(entry.Name, entry.Kind, entry.Size, entry.Modified, writable && entry.Kind == FileKind.File);

    internal static void Require(InstalledApp app, string permission)
    {
        if (!app.Holds(permission))
        {
            throw new PithyxException(ErrorCodes.AppPermissionDenied, new Dictionary<string, object?> { ["permission"] = permission });
        }
    }
}
