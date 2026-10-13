using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Pithyx.Daemon.Auth;

namespace Pithyx.Daemon.Tls;

/// <summary>Settings > Certificates. Administrators only, except the root, which any device may download to trust it.</summary>
public static class TlsEndpoints
{
    public static IServiceCollection AddPithyxTls(this IServiceCollection services, TlsOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<BoxCertificates>();
        return services;
    }

    public static void MapTlsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/system/tls/root-ca.crt", (BoxCertificates certificates) =>
                TypedResults.File(Encoding.ASCII.GetBytes(certificates.RootPem), "application/x-x509-ca-cert", "pithyx-root-ca.crt"))
            .Produces<byte[]>(StatusCodes.Status200OK, "application/x-x509-ca-cert")
            .WithName("GetRootCertificate")
            .WithTags("System")
            .WithDescription("Anyone, also before signing in. The root of the box's certificate authority in PEM, to install on a device so it trusts the box.")
            .AllowAnonymous();

        var tls = api.MapGroup("/system/tls").WithTags("System").RequireAuthorization(Policies.Admin);
        tls.MapGet("", async Task<Ok<TlsSettings>> (BoxCertificates certificates, CancellationToken cancellationToken) =>
                TypedResults.Ok(await certificates.SettingsAsync(cancellationToken)))
            .WithName("GetTlsSettings")
            .WithDescription("Administrators only. Whether the web interface speaks HTTPS, the certificate it presents, the box's certificate authority with its scope, the names of the box's own certificate, an uploaded certificate and the certificates issued for other devices.");
        tls.MapPut("/names", async Task<Ok<TlsSettings>> (TlsNamesRequest request, BoxCertificates certificates, CancellationToken cancellationToken) =>
            {
                await certificates.SetExtraNamesAsync(request.ExtraNames ?? [], cancellationToken);
                return TypedResults.Ok(await certificates.SettingsAsync(cancellationToken));
            })
            .WithName("SetTlsNames")
            .WithDescription("Administrators only. Names (such as a domain pointing at the box) the box's own certificate covers besides its host name and addresses; they must lie inside the authority's scope. The certificate is issued again at once.");
        tls.MapPost("/authority", async Task<Ok<TlsSettings>> (CreateAuthorityRequest request, BoxCertificates certificates, CancellationToken cancellationToken) =>
            {
                await certificates.CreateAuthorityAsync(request, cancellationToken);
                return TypedResults.Ok(await certificates.SettingsAsync(cancellationToken));
            })
            .WithName("CreateCertificateAuthority")
            .WithDescription("Administrators only. Replaces the certificate authority with a new root and intermediate and issues the box's certificate from them. Every device has to trust the new root; certificates issued for other devices stay valid only where the old root is still trusted.");
        tls.MapPost("/issued", async Task<Ok<IssuedCertificateFiles>> (IssueCertificateRequest request, BoxCertificates certificates, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await certificates.IssueAsync(request, context.User.Identity?.Name, cancellationToken)))
            .WithName("IssueCertificate")
            .WithDescription("Administrators only. Issues a server certificate for another device from the box's authority. The answer holds the private key; the box does not keep it.");
        tls.MapGet("/issued/{id:long}/certificate.pem", async Task<FileContentHttpResult> (long id, BoxCertificates certificates, CancellationToken cancellationToken) =>
                TypedResults.File(Encoding.ASCII.GetBytes(await certificates.IssuedChainAsync(id, cancellationToken)), "application/x-pem-file", $"certificate-{id}.pem"))
            .Produces<byte[]>(StatusCodes.Status200OK, "application/x-pem-file")
            .WithName("GetIssuedCertificate")
            .WithDescription("Administrators only. An issued certificate with the intermediate it was issued under, without its key.");
        tls.MapDelete("/issued/{id:long}", async Task<NoContent> (long id, BoxCertificates certificates, CancellationToken cancellationToken) =>
            {
                await certificates.DeleteIssuedAsync(id, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("DeleteIssuedCertificate")
            .WithDescription("Administrators only. Removes an issued certificate from the list. The box keeps no revocation list, so the certificate stays valid until it ends.");
        tls.MapPut("/custom", async Task<Ok<TlsSettings>> (CustomCertificateRequest request, BoxCertificates certificates, CancellationToken cancellationToken) =>
            {
                await certificates.SetCustomAsync(request, cancellationToken);
                return TypedResults.Ok(await certificates.SettingsAsync(cancellationToken));
            })
            .WithName("SetCustomCertificate")
            .WithDescription("Administrators only. Presents a certificate of your own (with its chain and unencrypted key, in PEM) on the web ports and for FTPS while it is valid; after it ends the box presents its own again.");
        tls.MapDelete("/custom", async Task<Ok<TlsSettings>> (BoxCertificates certificates, CancellationToken cancellationToken) =>
            {
                await certificates.RemoveCustomAsync(cancellationToken);
                return TypedResults.Ok(await certificates.SettingsAsync(cancellationToken));
            })
            .WithName("RemoveCustomCertificate")
            .WithDescription("Administrators only. Removes the uploaded certificate; the box presents its own again.");
    }
}
