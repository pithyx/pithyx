using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Net.Http.Headers;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Installer;

public static class InstallerEndpoints
{
    /// <summary>The cookie that marks the browser owning the installation; it lives only as long as the live system.</summary>
    public const string CookieName = "pithyx_installer";

    public static IServiceCollection AddPithyxInstaller(this IServiceCollection services, InstallerEnvironment environment)
    {
        services.AddSingleton(environment);
        services.AddSingleton<SystemInstaller>();
        return services;
    }

    /// <summary>The installer's API; only mapped on the live system.</summary>
    public static void MapInstallerEndpoints(this RouteGroupBuilder api)
    {
        var installer = api.MapGroup("/installer").WithTags("Installer").AllowAnonymous();
        installer.MapGet("", GetStatus).WithName("GetInstallerStatus");
        installer.MapPost("/claim", Claim).WithName("ClaimInstaller");
        installer.MapGet("/options", GetOptions).WithName("GetInstallerOptions");
        installer.MapPost("/install", Install).WithName("Install");
        installer.MapPost("/reboot", Reboot).WithName("RebootInstaller");
    }

    /// <summary>
    /// <c>GET /api/install-id</c>: the installed system answers the id the installer gave it, so the installer page
    /// finds it after the reboot. It is asked over plain HTTP on port 80 from the page on port 7484, so any origin may read it.
    /// </summary>
    public static void MapInstallIdEndpoint(this RouteGroupBuilder api, string dataDirectory)
    {
        api.MapGet("/install-id", Results<Ok<InstallIdResponse>, NotFound> (HttpContext context) =>
        {
            context.Response.Headers[HeaderNames.AccessControlAllowOrigin] = "*";
            context.Response.Headers[HeaderNames.CacheControl] = "no-store";
            var path = Path.Combine(dataDirectory, SystemInstaller.InstallIdFile);
            return File.Exists(path) ? TypedResults.Ok(new InstallIdResponse(File.ReadAllText(path).Trim())) : TypedResults.NotFound();
        })
            .WithName("GetInstallId")
            .WithTags("Installer")
            .AllowAnonymous();
    }

    private static Ok<InstallerStatus> GetStatus(SystemInstaller installer, HttpContext context) =>
        TypedResults.Ok(installer.Status(Token(context)));

    private static Ok<InstallerStatus> Claim(SystemInstaller installer, HttpContext context)
    {
        var token = installer.Claim(Token(context), context.Connection.RemoteIpAddress?.ToString() ?? "unknown")
            ?? throw new PithyxException(ErrorCodes.InstallerClaimTaken);
        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
            Path = "/api/installer",
            IsEssential = true,
        });
        return TypedResults.Ok(installer.Status(token));
    }

    private static Ok<InstallerOptions> GetOptions(SystemInstaller installer, HttpContext context)
    {
        RequireOwner(installer, context);
        return TypedResults.Ok(installer.Options());
    }

    private static Accepted<InstallerStatus> Install(InstallRequest request, SystemInstaller installer, HttpContext context)
    {
        RequireOwner(installer, context);
        installer.Start(request);
        return TypedResults.Accepted("/api/installer", installer.Status(Token(context)));
    }

    private static NoContent Reboot(SystemInstaller installer, HttpContext context)
    {
        RequireOwner(installer, context);
        installer.Reboot();
        return TypedResults.NoContent();
    }

    private static void RequireOwner(SystemInstaller installer, HttpContext context)
    {
        if (!installer.IsOwner(Token(context)))
        {
            throw new PithyxException(ErrorCodes.InstallerClaimRequired);
        }
    }

    private static string? Token(HttpContext context) => context.Request.Cookies.TryGetValue(CookieName, out var token) ? token : null;
}
