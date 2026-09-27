using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Vyxis.Daemon.Auth;

namespace Vyxis.Daemon.Layouts;

/// <summary>
/// Restoring, saving and copying desktop layouts. The desktop debounces saves and sends the version it last read or
/// saved as <c>If-Match</c>, so a save from another tab of the same browser is never overwritten unnoticed.
/// </summary>
public static class LayoutEndpoints
{
    // A layout is at most LayoutRules.MaxLayoutBytes; this also stays within the 64 KB of a keepalive request.
    private const long MaxRequestBytes = 64 * 1024;

    public static IServiceCollection AddVyxisLayouts(this IServiceCollection services)
    {
        services.AddSingleton<LayoutService>();
        services.AddSingleton<DesktopItemService>();
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

        desktop.MapGet("/items", ListItemsAsync).WithName("ListDesktopItems");
        desktop.MapPost("/items", CreateItemAsync).WithName("CreateDesktopItem");
        desktop.MapPatch("/items", ChangeItemsAsync)
            .WithName("ChangeDesktopItems")
            .WithDescription(
                "Changes several items at once, all or none. Every change names the version it builds on; if an item "
                + "changed in between (for example on another device), the answer is 412 with desktop.item.conflict and "
                + "the item's id and current version in params. Returns the changed items with their new versions.");
        desktop.MapPost("/items/delete", DeleteItemsAsync).WithName("DeleteDesktopItems");
    }

    private static async Task<Ok<IReadOnlyList<DesktopItem>>> ListItemsAsync(DesktopItemService items, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Ok(await items.ListAsync(CurrentUser.Id(context.User), cancellationToken));

    private static async Task<Created<DesktopItem>> CreateItemAsync(NewDesktopItem item, DesktopItemService items, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Created((string?)null, await items.CreateAsync(CurrentUser.Id(context.User), item, cancellationToken));

    private static async Task<Ok<IReadOnlyList<DesktopItem>>> ChangeItemsAsync(IReadOnlyList<DesktopItemChange> changes, DesktopItemService items, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Ok(await items.ChangeAsync(CurrentUser.Id(context.User), changes, cancellationToken));

    private static async Task<NoContent> DeleteItemsAsync(DeleteDesktopItemsRequest request, DesktopItemService items, HttpContext context, CancellationToken cancellationToken)
    {
        await items.DeleteAsync(CurrentUser.Id(context.User), request.Ids, cancellationToken);
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

    // A malformed tag matches no version, so the save fails with the current one.
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

    private static async Task<NoContent> SetPreferenceAsync(string appId, WindowPreferenceRequest request, LayoutService layouts, HttpContext context, CancellationToken cancellationToken)
    {
        await layouts.SetPreferenceAsync(CurrentUser.Id(context.User), appId, request.Width, request.Height, cancellationToken);
        return TypedResults.NoContent();
    }

    // The session start sets the device cookie; a browser that lost it gets a new one with its next save or copy.
    private static string Device(HttpContext context) => DeviceCookie.Ensure(context);
}
