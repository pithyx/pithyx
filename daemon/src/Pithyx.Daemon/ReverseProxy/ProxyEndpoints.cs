using Microsoft.AspNetCore.Http.HttpResults;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Network;

namespace Pithyx.Daemon.ReverseProxy;

/// <summary>Settings > Reverse proxy (D347), administrators only; and the ticket every signed-in user needs for a host behind the Pithyx sign-in.</summary>
public static class ProxyEndpoints
{
    public static IServiceCollection AddPithyxReverseProxy(this IServiceCollection services)
    {
        services.AddSingleton<ProxyService>();
        services.AddSingleton<ProxySignIn>();
        services.AddSingleton<StreamForwarder>();
        return services;
    }

    public static void MapProxyEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/proxy/tickets", async Task<Ok<ProxyTicket>> (ProxyTicketRequest request, ProxySignIn signIn, ProxyService proxy, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await signIn.IssueAsync(
                    request,
                    CurrentUser.Id(context.User),
                    context.User.FindFirst(SessionAuthenticationHandler.SessionIdClaim)?.Value ?? throw new Errors.PithyxException(Errors.ErrorCodes.AuthSessionExpired),
                    proxy.State,
                    cancellationToken)))
            .WithName("CreateProxyTicket")
            .WithTags("System")
            .WithDescription("Signed-in users. A one-time ticket (30 seconds) into a proxy host behind the Pithyx sign-in, for the address the browser asked for; the answer is the address to open on that host. Refused with proxy.access.denied when the host does not let the user in.");

        var proxy = api.MapGroup("/system/proxy").WithTags("System").RequireAuthorization(Policies.Admin);
        proxy.MapGet("", async Task<Ok<ProxySettings>> (ProxyService service, StreamForwarder streams, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.SettingsAsync(streams.IsListening, cancellationToken)))
            .WithName("GetProxySettings")
            .WithDescription("Administrators only. The ports proxy hosts answer on, what unknown names get, the sign-in address, and every proxy host, redirect host and stream.");
        proxy.MapPut("/options", async Task<Ok<ProxySettings>> (ProxyOptionsRequest request, ProxyService service, StreamForwarder streams, CancellationToken cancellationToken) =>
            {
                await service.SetOptionsAsync(request, cancellationToken);
                return TypedResults.Ok(await service.SettingsAsync(streams.IsListening, cancellationToken));
            })
            .WithName("SetProxyOptions")
            .WithDescription("Administrators only. What a name no enabled host has gets on 443 and 80 (a redirect to 7484, or 404), and where the Pithyx sign-in of a host sends visitors.");

        proxy.MapPost("/hosts", async Task<Ok<ProxySettings>> (ProxyHostRequest request, ProxyService service, StreamForwarder streams, CancellationToken cancellationToken) =>
            {
                await service.SaveHostAsync(null, request, cancellationToken);
                return TypedResults.Ok(await service.SettingsAsync(streams.IsListening, cancellationToken));
            })
            .WithName("CreateProxyHost")
            .WithDescription("Administrators only. A proxy host: domains on 443 and 80 forwarded to a target. Applies to the next request.");
        proxy.MapPut("/hosts/{id:long}", async Task<Ok<ProxySettings>> (long id, ProxyHostRequest request, ProxyService service, StreamForwarder streams, CancellationToken cancellationToken) =>
            {
                await service.SaveHostAsync(id, request, cancellationToken);
                return TypedResults.Ok(await service.SettingsAsync(streams.IsListening, cancellationToken));
            })
            .WithName("UpdateProxyHost")
            .WithDescription("Administrators only. Replaces a proxy host's settings; Basic Auth users without a password keep theirs, an uploaded certificate stays unless a new one comes along.");
        proxy.MapDelete("/hosts/{id:long}", async Task<Ok<ProxySettings>> (long id, ProxyService service, StreamForwarder streams, CancellationToken cancellationToken) =>
            {
                await service.DeleteHostAsync(id, cancellationToken);
                return TypedResults.Ok(await service.SettingsAsync(streams.IsListening, cancellationToken));
            })
            .WithName("DeleteProxyHost")
            .WithDescription("Administrators only. Removes a proxy host with its uploaded certificate; its names become unknown.");
        proxy.MapPost("/hosts/{id:long}/check", async Task<Ok<ProxyTargetStatus>> (long id, ProxyService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.CheckAsync(id, cancellationToken)))
            .WithName("CheckProxyTarget")
            .WithDescription("Administrators only. Whether a proxy host's target answers one request, with the host's TLS settings.");

        proxy.MapPost("/redirects", async Task<Ok<ProxySettings>> (ProxyRedirectRequest request, ProxyService service, StreamForwarder streams, CancellationToken cancellationToken) =>
            {
                await service.SaveRedirectAsync(null, request, cancellationToken);
                return TypedResults.Ok(await service.SettingsAsync(streams.IsListening, cancellationToken));
            })
            .WithName("CreateProxyRedirect")
            .WithDescription("Administrators only. A redirect host: domains on 443 and 80 redirected to an address.");
        proxy.MapPut("/redirects/{id:long}", async Task<Ok<ProxySettings>> (long id, ProxyRedirectRequest request, ProxyService service, StreamForwarder streams, CancellationToken cancellationToken) =>
            {
                await service.SaveRedirectAsync(id, request, cancellationToken);
                return TypedResults.Ok(await service.SettingsAsync(streams.IsListening, cancellationToken));
            })
            .WithName("UpdateProxyRedirect")
            .WithDescription("Administrators only. Replaces a redirect host's settings.");
        proxy.MapDelete("/redirects/{id:long}", async Task<Ok<ProxySettings>> (long id, ProxyService service, StreamForwarder streams, CancellationToken cancellationToken) =>
            {
                await service.DeleteRedirectAsync(id, cancellationToken);
                return TypedResults.Ok(await service.SettingsAsync(streams.IsListening, cancellationToken));
            })
            .WithName("DeleteProxyRedirect")
            .WithDescription("Administrators only. Removes a redirect host.");

        proxy.MapPost("/streams", async Task<Ok<ProxySettings>> (ProxyStreamRequest request, ProxyService service, StreamForwarder streams, Firewall firewall, CancellationToken cancellationToken) =>
            {
                await service.SaveStreamAsync(null, request, streams.PortFree, cancellationToken);
                await ApplyStreamsAsync(streams, firewall, cancellationToken);
                return TypedResults.Ok(await service.SettingsAsync(streams.IsListening, cancellationToken));
            })
            .WithName("CreateProxyStream")
            .WithDescription("Administrators only. A stream: a TCP and/or UDP port of the box forwarded to a host and port; the firewall opens the port.");
        proxy.MapPut("/streams/{id:long}", async Task<Ok<ProxySettings>> (long id, ProxyStreamRequest request, ProxyService service, StreamForwarder streams, Firewall firewall, CancellationToken cancellationToken) =>
            {
                await service.SaveStreamAsync(id, request, streams.PortFree, cancellationToken);
                await ApplyStreamsAsync(streams, firewall, cancellationToken);
                return TypedResults.Ok(await service.SettingsAsync(streams.IsListening, cancellationToken));
            })
            .WithName("UpdateProxyStream")
            .WithDescription("Administrators only. Replaces a stream's settings.");
        proxy.MapDelete("/streams/{id:long}", async Task<Ok<ProxySettings>> (long id, ProxyService service, StreamForwarder streams, Firewall firewall, CancellationToken cancellationToken) =>
            {
                await service.DeleteStreamAsync(id, cancellationToken);
                await ApplyStreamsAsync(streams, firewall, cancellationToken);
                return TypedResults.Ok(await service.SettingsAsync(streams.IsListening, cancellationToken));
            })
            .WithName("DeleteProxyStream")
            .WithDescription("Administrators only. Removes a stream; the firewall closes its port.");
    }

    private static async Task ApplyStreamsAsync(StreamForwarder streams, Firewall firewall, CancellationToken cancellationToken)
    {
        await streams.ApplyAsync(cancellationToken);
        await firewall.ApplyAsync(cancellationToken);
    }
}
