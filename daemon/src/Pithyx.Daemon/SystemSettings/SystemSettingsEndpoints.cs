using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.SystemSettings;

/// <summary>The Info Center, the network overview and the regional settings of the Settings app. Administrators only.</summary>
public static class SystemSettingsEndpoints
{
    public static IServiceCollection AddPithyxSystemSettings(this IServiceCollection services, DaemonMode mode) =>
        services.AddSingleton(provider => new SystemSettingsService(
            provider.GetRequiredService<ICommandRunner>(),
            mode,
            provider.GetRequiredService<SqliteConnectionFactory>(),
            provider.GetRequiredService<TimeProvider>()));

    public static void MapSystemSettingsEndpoints(this RouteGroupBuilder api)
    {
        var system = api.MapGroup("/system").WithTags("System");
        system.MapGet("/overview", (SystemSettingsService settings, HttpContext context) =>
            {
                RequireAdmin(context.User);
                return TypedResults.Ok(settings.Overview());
            })
            .WithName("GetSystemOverview")
            .WithDescription("Administrators only. Version and commit, operating system, kernel, machine, processor, memory, host name and uptime.");
        system.MapGet("/network", (SystemSettingsService settings, HttpContext context) =>
            {
                RequireAdmin(context.User);
                return TypedResults.Ok(settings.Network());
            })
            .WithName("GetSystemNetwork")
            .WithDescription("Administrators only. The network interfaces with their addresses, gateways, name servers and whether they use DHCP. Read only until host network changes come with their confirmation timer.");
        system.MapGet("/time", (SystemSettingsService settings, HttpContext context) =>
            {
                RequireAdmin(context.User);
                return TypedResults.Ok(settings.Time());
            })
            .WithName("GetSystemTime")
            .WithDescription("Administrators only. Time zone, synchronization state, the configured time servers and every time zone the system knows.");
        system.MapPut("/time", async Task<Ok<TimeSettings>> (TimeSettingsRequest request, SystemSettingsService settings, HttpContext context, CancellationToken cancellationToken) =>
            {
                RequireAdmin(context.User);
                return TypedResults.Ok(await settings.SetTimeAsync(request, cancellationToken));
            })
            .WithName("SetSystemTime")
            .WithDescription("Administrators only. Sets the time zone and the time servers (an empty list means the distribution's defaults).");
        system.MapGet("/region", async Task<Ok<SystemRegion>> (SystemSettingsService settings, HttpContext context, CancellationToken cancellationToken) =>
            {
                RequireAdmin(context.User);
                return TypedResults.Ok(await settings.RegionAsync(cancellationToken));
            })
            .WithName("GetSystemRegion")
            .WithDescription("Administrators only. The system language and the region whose formats every user sees. GET /api/setup carries both for the sign-in page.");
        system.MapPut("/region", async Task<Ok<SystemRegion>> (SystemRegion region, SystemSettingsService settings, HttpContext context, CancellationToken cancellationToken) =>
            {
                RequireAdmin(context.User);
                return TypedResults.Ok(await settings.SetRegionAsync(region, cancellationToken));
            })
            .WithName("SetSystemRegion")
            .WithDescription("Administrators only. Sets the system language (en, de) and the region (an ISO 3166 country code, or null to follow each user's language).");
    }

    private static void RequireAdmin(ClaimsPrincipal user)
    {
        if (!user.IsInRole(SessionAuthenticationHandler.AdminRole))
        {
            throw new PithyxException(ErrorCodes.AuthPermissionDenied);
        }
    }
}
