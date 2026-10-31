using Microsoft.AspNetCore.Http.HttpResults;
using Pithyx.Daemon.Auth;

namespace Pithyx.Daemon.Ups;

/// <summary>Settings &gt; UPS (D377). Administrators only.</summary>
public static class UpsEndpoints
{
    public static IServiceCollection AddPithyxUps(this IServiceCollection services, DaemonMode mode, bool watch)
    {
        services.AddSingleton(provider => ActivatorUtilities.CreateInstance<UpsService>(provider, mode));
        services.AddSingleton(provider => UpsService.Definition(provider.GetRequiredService<UpsService>));
        if (watch)
        {
            services.AddHostedService(provider => provider.GetRequiredService<UpsService>());
        }
        return services;
    }

    public static void MapUpsEndpoints(this RouteGroupBuilder api)
    {
        var ups = api.MapGroup("/system/ups").WithTags("System").RequireAuthorization(Policies.Admin);
        ups.MapGet("", async Task<Ok<UpsStatus>> (UpsService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.StatusAsync(cancellationToken)))
            .WithName("GetSystemUps")
            .WithDescription("Administrators only. The UPS settings (without the password), what the box knows about the UPS right now and whether a UPS on USB is possible. The topic system/ups sends the same.");
        ups.MapPut("", async Task<Ok<UpsStatus>> (UpsSettingsRequest request, UpsService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.UpdateAsync(request, cancellationToken)))
            .WithName("SetSystemUps")
            .WithDescription("Administrators only. Saves the UPS settings and watches the UPS with them at once: off, a UPS on USB through the box's own NUT server, or a UPS of another NUT server. On battery the box shuts down at the charge or runtime limit, when the UPS says its battery is low or when the server's primary forces it.");
    }
}
