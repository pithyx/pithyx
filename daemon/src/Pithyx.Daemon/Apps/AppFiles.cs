using System.Globalization;
using System.Security.Claims;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Files;
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
public sealed class AppFiles(FileService files, ShareService shares)
{
    public const string SharesPermission = "files.shares";
    public const string ReadPermission = "files.read";
    public const string WritePermission = "files.write";

    private sealed record Target(long ShareId, string Within, bool MayWrite);

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
        var target = Resolve(app, location, write: false, content: false);
        var page = await files.ListAsync(user, target.ShareId, path, sort, order, limit, cursor, cancellationToken, target.Within);
        return new AppFolderPage(page.Path, page.FolderId, page.Total, page.Entries, page.NextCursor);
    }

    public async Task<AppFileEntry> StatAsync(InstalledApp app, ClaimsPrincipal user, AppFileLocation location, string? path, CancellationToken cancellationToken)
    {
        var target = Resolve(app, location, write: false, content: false);
        var entry = await files.StatAsync(user, target.ShareId, path, cancellationToken, target.Within);
        return await WithWritableAsync(user, target, entry, cancellationToken);
    }

    public async Task<(FileStream Stream, FsStat Stat, string Name)> OpenReadAsync(InstalledApp app, ClaimsPrincipal user, AppFileLocation location, string? path, CancellationToken cancellationToken)
    {
        var target = Resolve(app, location, write: false, content: true);
        return await files.OpenReadAsync(user, target.ShareId, path, cancellationToken, target.Within);
    }

    public async Task<AppFileEntry> WriteAsync(InstalledApp app, ClaimsPrincipal user, AppFileLocation location, string? path, Stream body, long? length, bool overwrite, CancellationToken cancellationToken)
    {
        var target = Resolve(app, location, write: true, content: true);
        var entry = await files.UploadAsync(user, target.ShareId, path, body, length, overwrite, cancellationToken, target.Within);
        return Writable(entry, true);
    }

    public async Task<AppFileEntry> CreateFolderAsync(InstalledApp app, ClaimsPrincipal user, AppFileLocation location, string? path, CancellationToken cancellationToken)
    {
        var target = Resolve(app, location, write: true, content: true);
        var entry = await files.CreateFolderAsync(user, target.ShareId, path, cancellationToken, target.Within);
        return Writable(entry, true);
    }

    /// <summary>The shared folder and folder a call goes to, after the app's own checks; the user's rights come after.</summary>
    private static Target Resolve(InstalledApp app, AppFileLocation location, bool write, bool content)
    {
        ArgumentNullException.ThrowIfNull(location);
        if ((location.Share is null) == (location.Storage is null))
        {
            throw new PithyxException(ErrorCodes.FsOperationInvalid);
        }
        if (location.Share is { } share)
        {
            Require(app, SharesPermission);
            if (content)
            {
                Require(app, write ? WritePermission : ReadPermission);
            }
            if (!long.TryParse(share, NumberStyles.None, CultureInfo.InvariantCulture, out var shareId) || shareId <= 0)
            {
                throw new PithyxException(ErrorCodes.FsShareNotFound);
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
