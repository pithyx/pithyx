using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;
using Vyxis.Daemon.Errors;

namespace Vyxis.Daemon.Files;

/// <summary>Files in shares: listings, downloads with range requests, streamed uploads, new folders and renames.</summary>
public static class FileEndpoints
{
    private static readonly FileExtensionContentTypeProvider _types = new();

    public static IServiceCollection AddVyxisFiles(this IServiceCollection services)
    {
        services.AddSingleton<FolderCache>();
        services.AddSingleton<FileService>();
        services.AddSingleton<FileOperations>();
        services.AddSingleton<TrashService>();
        services.AddHostedService<TrashCleanupService>();
        services.AddSingleton<FolderWatches>();
        services.AddSingleton(provider => FolderWatchPublisher.Definition(
            provider.GetRequiredService<FolderWatches>(),
            provider.GetRequiredService<Shares.ShareService>()));
        services.AddHostedService<FolderWatchPublisher>();
        return services;
    }

    public static void MapFileEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/files/operations", StartOperationAsync)
            .WithTags("Files")
            .WithName("StartFileOperation")
            .WithDescription(
                "Starts copying, moving or deleting as a job and answers 202 with it; follow it on jobs/<id>. Access is "
                + "checked now: read on the sources for copy, write on the sources for move and delete, write on the target. "
                + "Deleting moves to the share's recycle bin unless permanent is true. onConflict decides about names that exist in the target: fail (default), skip, "
                + "overwrite (folders are merged) or rename.");
        var files = api.MapGroup("/files/{shareId:long}").WithTags("Files");
        files.MapGet("/list", ListAsync)
            .WithName("ListFolder")
            .WithDescription(
                "One page of a folder. Paths are relative to the share with / as separator; \".\" and \"..\" are refused. "
                + "sort is name (natural order, ignoring case), size or modified, order asc or desc; pass nextCursor as cursor for the next page. "
                + "Subscribe to topic for live changes of the folder.");
        files.MapGet("/entry", StatAsync).WithName("GetFileEntry");
        files.MapGet("/content", DownloadAsync)
            .WithName("DownloadFile")
            .WithDescription(
                "The file's content with range requests (206, 416), ETag and Last-Modified. Sent as an attachment unless "
                + "inline=true; always with Content-Security-Policy: sandbox, so a file is never run as part of the desktop.");
        files.MapPut("/content", UploadAsync)
            .WithName("UploadFile")
            .WithDescription(
                "Streams the request body into the file at path (the body is the raw content). The file appears only when the "
                + "body is complete. Fails with fs.entry.exists if the name is taken, unless overwrite=true.")
            .WithMetadata(new DisableRequestSizeLimitAttribute());
        files.MapPost("/folders", CreateFolderAsync).WithName("CreateFolder");
        files.MapGet("/trash", ListTrashAsync)
            .WithName("ListTrash")
            .WithDescription("The share's recycle bin, newest first, with the share's retention. Deleting a file or folder moves it here unless the delete is permanent.");
        files.MapPost("/trash/restore", RestoreTrashAsync)
            .WithName("RestoreTrash")
            .WithDescription("Starts a job that puts items back where they were, recreating missing folders. onConflict defaults to rename.");
        files.MapPost("/trash/delete", PurgeTrashAsync)
            .WithName("DeleteTrashItems")
            .WithDescription("Starts a job that deletes the given recycle bin items for good.");
        files.MapPost("/trash/empty", EmptyTrashAsync)
            .WithName("EmptyTrash")
            .WithDescription("Starts a job that deletes everything in the share's recycle bin for good.");
        api.MapPut("/shares/{shareId:long}/trash", SetTrashAsync)
            .WithTags("Shares")
            .WithName("SetShareTrash")
            .WithDescription("Administrators only. Days the recycle bin keeps deleted items (0 to 3650; 0 keeps them until emptied). Applies to items already in the bin.");
        files.MapPost("/rename", RenameAsync)
            .WithName("RenameFileEntry")
            .WithDescription("Renames a file or folder within its folder. An existing name is never replaced (fs.entry.exists).");
    }

    private static async Task<Ok<TrashPage>> ListTrashAsync(long shareId, string? cursor, int? limit, TrashService trash, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Ok(await trash.ListAsync(context.User, shareId, cursor, limit, cancellationToken));

    private static async Task<Accepted<Jobs.Job>> RestoreTrashAsync(long shareId, TrashItemsRequest request, FileOperations operations, HttpContext context, CancellationToken cancellationToken) =>
        Accepted(await operations.StartRestoreAsync(context.User, shareId, request, cancellationToken));

    private static async Task<Accepted<Jobs.Job>> PurgeTrashAsync(long shareId, TrashItemsRequest request, FileOperations operations, HttpContext context, CancellationToken cancellationToken) =>
        Accepted(await operations.StartPurgeAsync(context.User, shareId, request.Ids, cancellationToken));

    private static async Task<Accepted<Jobs.Job>> EmptyTrashAsync(long shareId, FileOperations operations, HttpContext context, CancellationToken cancellationToken) =>
        Accepted(await operations.StartPurgeAsync(context.User, shareId, null, cancellationToken));

    private static async Task<NoContent> SetTrashAsync(long shareId, TrashSettingsRequest request, TrashService trash, HttpContext context, CancellationToken cancellationToken)
    {
        await trash.SetRetentionAsync(context.User, shareId, request.RetentionDays, cancellationToken);
        return TypedResults.NoContent();
    }

    private static Accepted<Jobs.Job> Accepted(Jobs.Job job) => TypedResults.Accepted($"/api/jobs/{job.Id}", job);

    private static async Task<Accepted<Jobs.Job>> StartOperationAsync(FileOperationRequest request, FileOperations operations, HttpContext context, CancellationToken cancellationToken)
    {
        var job = await operations.StartAsync(context.User, request, cancellationToken);
        return TypedResults.Accepted($"/api/jobs/{job.Id}", job);
    }

    private static async Task<Ok<FolderPage>> ListAsync(
        long shareId,
        string? path,
        string? sort,
        string? order,
        int? limit,
        string? cursor,
        FileService files,
        HttpContext context,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await files.ListAsync(context.User, shareId, path, Parse(sort, FileSort.Name), Parse(order, SortOrder.Asc), limit, cursor, cancellationToken));

    // Query values are the same lowercase words as in JSON.
    private static T Parse<T>(string? value, T fallback) where T : struct, Enum =>
        value is null ? fallback : Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !char.IsAsciiDigit(value[0])
            ? parsed
            : throw new VyxisException(ErrorCodes.FsOperationInvalid);

    private static async Task<Ok<FileEntry>> StatAsync(long shareId, string path, FileService files, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Ok(await files.StatAsync(context.User, shareId, path, cancellationToken));

    private static async Task<FileStreamHttpResult> DownloadAsync(
        long shareId,
        string path,
        bool? inline,
        FileService files,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var (stream, stat, name) = await files.OpenReadAsync(context.User, shareId, path, cancellationToken);
        context.Response.RegisterForDispose(stream);
        var headers = context.Response.Headers;
        // A file from a share must never run with the desktop's origin: no scripts, no plugins, no sniffing.
        headers[HeaderNames.ContentSecurityPolicy] = "sandbox; default-src 'none'; img-src 'self' data:; media-src 'self'; style-src 'unsafe-inline'";
        headers[HeaderNames.XContentTypeOptions] = "nosniff";
        headers[HeaderNames.CacheControl] = "private, no-cache";
        var disposition = new ContentDispositionHeaderValue(inline == true ? "inline" : "attachment");
        disposition.SetHttpFileName(name);
        headers[HeaderNames.ContentDisposition] = disposition.ToString();
        var type = inline == true && _types.TryGetContentType(name, out var known) ? known : "application/octet-stream";
        var tag = new EntityTagHeaderValue(string.Create(CultureInfo.InvariantCulture, $"\"{stat.Id}-{stat.Modified.UtcTicks:x}-{stat.Size:x}\""));
        return TypedResults.Stream(stream, type, fileDownloadName: null, lastModified: stat.Modified, entityTag: tag, enableRangeProcessing: true);
    }

    private static async Task<Created<FileEntry>> UploadAsync(
        long shareId,
        string path,
        bool? overwrite,
        FileService files,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        // Uploads have no size limit of their own; the pool's free space is the limit.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = null;
        }
        var entry = await files.UploadAsync(context.User, shareId, path, context.Request.Body, context.Request.ContentLength, overwrite == true, cancellationToken);
        return TypedResults.Created((string?)null, entry);
    }

    private static async Task<Created<FileEntry>> CreateFolderAsync(long shareId, NewFolderRequest request, FileService files, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Created((string?)null, await files.CreateFolderAsync(context.User, shareId, request.Path, cancellationToken));

    private static async Task<Ok<FileEntry>> RenameAsync(long shareId, RenameRequest request, FileService files, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Ok(await files.RenameAsync(context.User, shareId, request.Path, request.Name, cancellationToken));
}
