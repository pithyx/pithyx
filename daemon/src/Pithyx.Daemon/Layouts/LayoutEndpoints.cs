using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Files;

namespace Pithyx.Daemon.Layouts;

/// <summary>
/// Restoring, saving and copying desktop layouts, and the desktop items. The desktop debounces layout saves and sends
/// the version it last read or saved as <c>If-Match</c>, so a save from another tab of the same browser is never
/// overwritten unnoticed. Item changes send the item's version the same way.
/// </summary>
public static class LayoutEndpoints
{
    // A layout is at most LayoutRules.MaxLayoutBytes; this also stays within the 64 KB of a keepalive request.
    private const long MaxRequestBytes = 64 * 1024;

    public static IServiceCollection AddPithyxLayouts(this IServiceCollection services)
    {
        services.AddSingleton<LayoutService>();
        services.AddSingleton<DesktopItemService>();
        services.AddSingleton<DesktopFileLinks>();
        services.AddHostedService<LayoutCleanupService>();
        return services;
    }

    public static void MapLayoutEndpoints(this RouteGroupBuilder api)
    {
        var desktop = api.MapGroup("/desktop").WithTags("Desktop");
        desktop.MapGet("", GetStateAsync).WithName("GetDesktop");
        desktop.MapPut("/layout", SaveAsync)
            .WithName("SaveDesktopLayout")
            .WithDescription(
                "Saves this device's layout. Send the version from GET /api/desktop or the ETag of the last save as "
                + "If-Match: \"n\". If another tab saved in between, the answer is 412 with desktop.layout.conflict and "
                + "the current version in params.version. Without If-Match the save is unconditional. The new version "
                + "comes back as ETag.")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes));
        desktop.MapGet("/devices", ListDevicesAsync).WithName("ListDesktopDevices");
        desktop.MapPost("/layout/copy", CopyAsync).WithName("CopyDesktopLayout");
        desktop.MapPut("/preferences/{appId}", SetPreferenceAsync).WithName("SetWindowPreference");
        desktop.MapPut("/appearance", SetAppearanceAsync)
            .WithName("SetDesktopAppearance")
            .WithDescription("Saves the accent color and the wallpaper of the signed-in user, for every device. GET /api/desktop returns them.");

        desktop.MapPut("/file-defaults/{extension}", SetFileDefaultAsync)
            .WithName("SetFileDefault")
            .WithDescription(
                "Saves how the signed-in user opens files of an extension (lowercase, without the dot), for every device: "
                + "viewer, download or app:<id>. GET /api/desktop returns them as fileDefaults.");
        desktop.MapDelete("/file-defaults/{extension}", RemoveFileDefaultAsync)
            .WithName("RemoveFileDefault")
            .WithDescription("Forgets the signed-in user's choice for an extension. Forgetting one that does not exist also answers 204.");

        desktop.MapPut("/startup-apps", SetStartupAppsAsync)
            .WithName("SetStartupApps")
            .WithDescription(
                "Replaces the apps the signed-in user opens after signing in, in order, for every device (at most 10). "
                + "GET /api/desktop returns them as startupApps; the desktop skips apps that are gone or not allowed.");

        desktop.MapGet("/items", ListItemsAsync)
            .WithName("ListDesktopItems")
            .WithDescription(
                "The user's desktop items. A shortcut to a file or folder says in target whether its target is a file, a "
                + "folder or missing (gone, moved past the daemon, or no longer readable for the user).");
        desktop.MapPost("/items", CreateItemAsync)
            .WithName("CreateDesktopItem")
            .WithDescription(
                "Creates an item. The answer is 201 with the item, its URL as Location and its version as ETag. A shortcut "
                + "to a file or folder needs read access to it; otherwise the answer is the file error (for example "
                + "fs.entry.not_found).");
        desktop.MapPatch("/items/{id:long}", ChangeItemAsync)
            .WithName("ChangeDesktopItem")
            .WithDescription(
                "Changes one item; fields left out stay. Send the item's version from GET /api/desktop/items or the ETag "
                + "of its last change as If-Match: \"n\". If the item changed in between (for example on another device), "
                + "the answer is 412 with desktop.item.conflict and the current version in params.version. Without "
                + "If-Match the change is unconditional. The new version comes back as ETag.");
        desktop.MapDelete("/items/{id:long}", DeleteItemAsync)
            .WithName("DeleteDesktopItem")
            .WithDescription("Deletes one item. Deleting an item that does not exist (any more) also answers 204.");
    }

    private static async Task<Ok<IReadOnlyList<DesktopItem>>> ListItemsAsync(DesktopItemService items, FileService files, HttpContext context, CancellationToken cancellationToken)
    {
        var list = await items.ListAsync(CurrentUser.Id(context.User), cancellationToken);
        var result = new List<DesktopItem>(list.Count);
        foreach (var item in list)
        {
            result.Add(await WithTargetAsync(item, files, context.User, cancellationToken));
        }
        return TypedResults.Ok<IReadOnlyList<DesktopItem>>(result);
    }

    private static async Task<Created<DesktopItem>> CreateItemAsync(NewDesktopItem item, DesktopItemService items, FileService files, HttpContext context, CancellationToken cancellationToken)
    {
        // The share's root is no target; the service refuses it as an invalid item.
        if (item.Kind == DesktopItemKind.File && item.Share is { } share && FsPath.Normalize(item.Path).Length > 0)
        {
            // Throws the file error when the user cannot read the target.
            await files.StatAsync(context.User, share, item.Path, cancellationToken);
        }
        var created = await WithTargetAsync(await items.CreateAsync(CurrentUser.Id(context.User), item, cancellationToken), files, context.User, cancellationToken);
        context.Response.Headers.ETag = EntityTag(created.Version);
        return TypedResults.Created(string.Create(CultureInfo.InvariantCulture, $"/api/desktop/items/{created.Id}"), created);
    }

    private static async Task<Ok<DesktopItem>> ChangeItemAsync(
        long id,
        [FromBody] DesktopItemChange change,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        DesktopItemService items,
        FileService files,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var changed = await WithTargetAsync(
            await items.ChangeAsync(
                CurrentUser.Id(context.User),
                id,
                change,
                ifMatch is null ? null : ParseEntityTag(ifMatch),
                cancellationToken),
            files,
            context.User,
            cancellationToken);
        context.Response.Headers.ETag = EntityTag(changed.Version);
        return TypedResults.Ok(changed);
    }

    /// <summary>Adds to a shortcut to a file or folder what its target is now for this user.</summary>
    private static async Task<DesktopItem> WithTargetAsync(DesktopItem item, FileService files, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (item.Kind != DesktopItemKind.File)
        {
            return item;
        }
        if (item.Share is not { } share || item.Path is null)
        {
            return item with { Target = DesktopFileTarget.Missing };
        }
        try
        {
            var entry = await files.StatAsync(user, share, item.Path, cancellationToken);
            return item with { Target = entry.Kind is FileKind.Folder or FileKind.Symlink ? DesktopFileTarget.Folder : DesktopFileTarget.File };
        }
        catch (PithyxException)
        {
            return item with { Target = DesktopFileTarget.Missing };
        }
    }

    private static async Task<NoContent> DeleteItemAsync(long id, DesktopItemService items, HttpContext context, CancellationToken cancellationToken)
    {
        await items.DeleteAsync(CurrentUser.Id(context.User), id, cancellationToken);
        return TypedResults.NoContent();
    }

    // Reading needs no device id: a browser without one has no layout yet.
    private static async Task<Ok<DesktopState>> GetStateAsync(LayoutService layouts, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Ok(await layouts.GetStateAsync(CurrentUser.Id(context.User), DeviceCookie.Read(context.Request) ?? "", cancellationToken));

    private static async Task<Ok<IReadOnlyList<DeviceLayout>>> ListDevicesAsync(LayoutService layouts, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Ok(await layouts.ListDevicesAsync(CurrentUser.Id(context.User), DeviceCookie.Read(context.Request) ?? "", cancellationToken));

    private static async Task<NoContent> SaveAsync(
        [FromBody] JsonElement layout,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        LayoutService layouts,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var version = await layouts.SaveAsync(
            CurrentUser.Id(context.User),
            Device(context),
            context.Request.Headers.UserAgent.ToString(),
            layout,
            ifMatch is null ? null : ParseEntityTag(ifMatch),
            cancellationToken);
        context.Response.Headers.ETag = EntityTag(version);
        return TypedResults.NoContent();
    }

    // A malformed tag matches no version, so the save or change fails with the current one.
    private static long ParseEntityTag(string value) =>
        value.Length > 2 && value[0] == '"' && value[^1] == '"'
            && long.TryParse(value.AsSpan(1, value.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? version
            : -1;

    internal static string EntityTag(long version) => string.Create(CultureInfo.InvariantCulture, $"\"{version}\"");

    private static async Task<Ok<SavedLayout>> CopyAsync(CopyLayoutRequest request, LayoutService layouts, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Ok(await layouts.CopyAsync(
            CurrentUser.Id(context.User),
            request.DeviceId,
            Device(context),
            context.Request.Headers.UserAgent.ToString(),
            cancellationToken));

    private static async Task<NoContent> SetAppearanceAsync(Appearance appearance, LayoutService layouts, HttpContext context, CancellationToken cancellationToken)
    {
        await layouts.SetAppearanceAsync(CurrentUser.Id(context.User), appearance, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> SetFileDefaultAsync(string extension, FileDefaultRequest request, LayoutService layouts, HttpContext context, CancellationToken cancellationToken)
    {
        await layouts.SetFileDefaultAsync(CurrentUser.Id(context.User), new FileDefault(extension, request.Handler), cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> SetStartupAppsAsync(StartupApps request, LayoutService layouts, HttpContext context, CancellationToken cancellationToken)
    {
        await layouts.SetStartupAppsAsync(CurrentUser.Id(context.User), request, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> RemoveFileDefaultAsync(string extension, LayoutService layouts, HttpContext context, CancellationToken cancellationToken)
    {
        await layouts.RemoveFileDefaultAsync(CurrentUser.Id(context.User), extension, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> SetPreferenceAsync(string appId, WindowPreferenceRequest request, LayoutService layouts, HttpContext context, CancellationToken cancellationToken)
    {
        await layouts.SetPreferenceAsync(CurrentUser.Id(context.User), appId, request.Width, request.Height, cancellationToken);
        return TypedResults.NoContent();
    }

    // The session start sets the device cookie; a browser that lost it gets a new one with its next save or copy.
    private static string Device(HttpContext context) => DeviceCookie.Ensure(context);
}
