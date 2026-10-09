using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Hosting;

namespace Pithyx.Daemon.Network;

/// <summary>Settings > Network. Administrators only.</summary>
public static class NetworkEndpoints
{
    public static IServiceCollection AddPithyxNetwork(this IServiceCollection services, DaemonMode mode)
    {
        services.AddSingleton<NetworkConfirmation>();
        services.AddSingleton(provider => ActivatorUtilities.CreateInstance<NetworkHost>(provider, mode));
        services.AddSingleton<NetworkService>();
        // Resolved per snapshot: the service publishes through the hub, which is built from these definitions.
        services.AddSingleton(provider => NetworkService.Definition(provider.GetRequiredService<NetworkService>));
        if (!BuildTimeDocument.IsGenerating)
        {
            services.AddHostedService<NetworkRecovery>();
            if (mode == DaemonMode.Real)
            {
                services.AddHostedService<NetworkMonitor>();
                services.AddHostedService<NetworkConsole>();
            }
        }
        return services;
    }

    public static void MapNetworkEndpoints(this RouteGroupBuilder api)
    {
        var network = api.MapGroup("/system/network").WithTags("System").RequireAuthorization(Policies.Admin);
        network.MapGet("", async (NetworkService service, HttpContext context) =>
                TypedResults.Ok(await service.StateAsync(LocalAddress(context), context.RequestAborted)))
            .WithName("GetSystemNetwork")
            .WithDescription("Administrators only. What Pithyx configured, every card, bond and VLAN with link, speed, addresses, gateways and name servers, whether Pithyx may change it here, the interface this connection comes in on and the change that waits for its confirmation. The topic system/network sends the same without the connection's interface.");
        network.MapPost("/check", async (NetworkConfig config, NetworkService service, HttpContext context) =>
                TypedResults.Ok(await service.CheckAsync(config, LocalAddress(context), context.RequestAborted)))
            .WithName("CheckNetworkConfig")
            .WithDescription("Administrators only. Checks new settings without applying them: the stored form, the interfaces that change, whether the host name changes, whether this connection's interface changes, and the static addresses to open afterwards.");
        network.MapPut("", async (NetworkConfig config, NetworkService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.ChangeAsync(config, NetworkChangeOrigin.Web, cancellationToken)))
            .WithName("SetNetworkConfig")
            .WithDescription("Administrators only. Applies new settings as one change (systemd-networkd and resolved files, host name). The change is rolled back after 60 seconds unless confirmed; open one of the answer's addresses to confirm when the old address went away.");
        network.MapPost("/changes/{changeId}/confirm", async (Guid changeId, NetworkService service, CancellationToken cancellationToken) =>
            {
                await service.ConfirmAsync(changeId, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("ConfirmNetworkChange")
            .WithDescription("Administrators only. Keeps the change that waits for its confirmation.");
        network.MapPost("/changes/{changeId}/revert", async (Guid changeId, NetworkService service, CancellationToken cancellationToken) =>
            {
                await service.RevertAsync(changeId, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("RevertNetworkChange")
            .WithDescription("Administrators only. Rolls the change that waits for its confirmation back at once.");
    }

    /// <summary>The address the request came in on; an in-process caller has none and counts as loopback.</summary>
    private static System.Net.IPAddress LocalAddress(HttpContext context) => context.Connection.LocalIpAddress ?? System.Net.IPAddress.IPv6Loopback;
}

/// <summary>Rolls back a change that waited for its confirmation when the daemon stopped.</summary>
public sealed class NetworkRecovery(NetworkService network) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => network.RecoverAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
