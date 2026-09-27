using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Vyxis.Daemon.Auth;

namespace Vyxis.Daemon.Layouts;

/// <summary>
/// Restoring and copying desktop layouts. Saving goes over the WebSocket (<c>layout</c> message), debounced by the
/// desktop.
/// </summary>
public static class LayoutEndpoints
{
    public static IServiceCollection AddVyxisLayouts(this IServiceCollection services)
    {
        services.AddSingleton<LayoutService>();
        services.AddHostedService<LayoutCleanupService>();
        return services;
    }

    public static void MapLayoutEndpoints(this RouteGroupBuilder api)
    {
        var desktop = api.MapGroup("/desktop").WithTags("Desktop");
        desktop.MapGet("", GetStateAsync).WithName("GetDesktop");
        desktop.MapGet("/devices", ListDevicesAsync).WithName("ListDesktopDevices");
        desktop.MapPost("/layout/copy", CopyAsync).WithName("CopyDesktopLayout");
        desktop.MapPut("/preferences/{appId}", SetPreferenceAsync).WithName("SetWindowPreference");
    }

    // Reading needs no device id: a browser without one has no layout yet. Only the WebSocket handshake and the
    // session start create ids, so this request and the handshake cannot create two at once.
    private static async Task<Ok<DesktopState>> GetStateAsync(LayoutService layouts, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Ok(await layouts.GetStateAsync(CurrentUser.Id(context.User), DeviceCookie.Read(context.Request) ?? "", cancellationToken));

    private static async Task<Ok<IReadOnlyList<DeviceLayout>>> ListDevicesAsync(LayoutService layouts, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Ok(await layouts.ListDevicesAsync(CurrentUser.Id(context.User), DeviceCookie.Read(context.Request) ?? "", cancellationToken));

    private static async Task<Ok<JsonElement>> CopyAsync(CopyLayoutRequest request, LayoutService layouts, HttpContext context, IHostEnvironment environment, CancellationToken cancellationToken) =>
        TypedResults.Ok(await layouts.CopyAsync(
            CurrentUser.Id(context.User),
            request.DeviceId,
            Device(context, environment),
            context.Request.Headers.UserAgent.ToString(),
            cancellationToken));

    private static async Task<NoContent> SetPreferenceAsync(string appId, WindowPreferenceRequest request, LayoutService layouts, HttpContext context, CancellationToken cancellationToken)
    {
        await layouts.SetPreferenceAsync(CurrentUser.Id(context.User), appId, request.Width, request.Height, cancellationToken);
        return TypedResults.NoContent();
    }

    // Sessions and the WebSocket handshake set the device cookie; copying may still be the first to need one.
    private static string Device(HttpContext context, IHostEnvironment environment) =>
        DeviceCookie.Ensure(context, secure: !environment.IsDevelopment());
}
