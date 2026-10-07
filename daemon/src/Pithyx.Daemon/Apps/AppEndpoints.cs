using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Pithyx.Daemon.Apps.Packages;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Jobs;

namespace Pithyx.Daemon.Apps;

/// <summary>The app registry (A10): installing apps from a package file, updating, disabling and removing them.</summary>
public static class AppEndpoints
{
    public static IServiceCollection AddPithyxApps(this IServiceCollection services, AppStorageOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<AppRegistry>();
        services.AddSingleton(provider => provider.GetRequiredService<AppRegistry>().Definition());
        services.AddSingleton(provider => provider.GetRequiredService<AppRegistry>().AdminDefinition());
        services.AddSingleton<AppStorage>();
        services.AddSingleton<AppPackages>();
        services.AddSingleton<AppService>();
        services.AddSingleton<AppTokens>();
        services.AddSingleton<AppValueStore>();
        services.AddSingleton<AppFiles>();
        services.AddSingleton<AppContexts>();
        services.AddSingleton(provider => provider.GetRequiredService<AppContexts>().Definition());
        services.AddSingleton<AppLeases>();
        services.AddHostedService(provider => provider.GetRequiredService<AppLeases>());
        services.AddHostedService<AppChangePublisher>();
        services.AddAuthentication()
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, AppTokenAuthenticationHandler>(AppTokenAuthenticationHandler.SchemeName, null);
        return services;
    }

    public static void MapAppEndpoints(this RouteGroupBuilder api)
    {
        var apps = api.MapGroup("/apps").WithTags("Apps");
        apps.MapGet("", (AppService service, HttpContext context) => TypedResults.Ok(service.List(context.User)))
            .RequireAuthorization(Policies.Admin)
            .WithName("ListApps")
            .WithDescription("Administrators only. Every installed app with its manifest, permissions, uid and state, and the app pool. The topic apps/admin carries the same and its changes.");
        apps.MapPut("/packages", UploadAsync)
            .RequireAuthorization(Policies.Admin)
            .WithMetadata(new DisableRequestSizeLimitAttribute())
            .WithName("UploadAppPackage")
            .WithDescription("Administrators only. Streams an app package (<id>-<version>.pithyxapp, a gzip-compressed tar archive) as the raw body, at most 256 MB with 512 MB of files. Unpacks and checks it (archive, layout, manifest, icon; ids under org.pithyx.* are refused) and keeps it for 15 minutes for its installation by the same administrator. Answers with what installing it would do, including the permission changes of an update.");
        apps.MapGet("/packages/{packageId}", (string packageId, AppService service, HttpContext context) => TypedResults.Ok(service.GetPreview(context.User, packageId)))
            .RequireAuthorization(Policies.Admin)
            .WithName("GetAppPackage")
            .WithDescription("Administrators only. The check result of an uploaded package again, while it is kept (app.package.not_found otherwise).");
        apps.MapDelete("/packages/{packageId}", (string packageId, AppService service, HttpContext context) =>
            {
                service.Discard(context.User, packageId);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Policies.Admin)
            .WithName("DiscardAppPackage")
            .WithDescription("Administrators only. Discards an uploaded package without installing it.");
        apps.MapPost("/packages/{packageId}/install", InstallAsync)
            .RequireAuthorization(Policies.Admin)
            .WithName("InstallAppPackage")
            .WithDescription("Administrators only. Installs or updates the app from an uploaded package as a job and answers 202 with it. The first app needs pool, the pool for every app's data; access (everybody by default) decides who may use a newly installed app. An update takes a snapshot of the app's dataset first and returns to it when a step fails.");
        apps.MapPatch("/{appId}", async Task<Ok<AppDetails>> (string appId, UpdateAppRequest request, AppService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.UpdateAsync(context.User, appId, request, cancellationToken)))
            .RequireAuthorization(Policies.Admin)
            .WithName("UpdateApp")
            .WithDescription("Administrators only. Enables or disables an app, switches its debug logging (logLevel) and grants or revokes permissions of its manifest (permissions, by name; request.body.invalid for one the manifest does not ask for). Fields left out stay. A disabled app keeps its data and grants, but nobody can open it, its frontend is not served and its tokens end. A revoked permission applies to the app's next call, and its windows learn it from the topic apps/<id>/context.");
        apps.MapDelete("/{appId}", UninstallAsync)
            .RequireAuthorization(Policies.Admin)
            .WithName("UninstallApp")
            .WithDescription("Administrators only. Removes an app as a job and answers 202 with it: data=keep removes its frontends and keeps its data on the app pool for a later install of the same app, data=delete destroys its dataset with every snapshot. Its grants go either way.");
        apps.MapGet("/{appId}/icon", IconAsync)
            .Produces<byte[]>(StatusCodes.Status200OK, "image/svg+xml")
            .WithName("GetAppIcon")
            .WithDescription("The icon of an app the user may use (any app for administrators), served with Content-Security-Policy: default-src 'none'; sandbox.");
    }

    private static async Task<Ok<AppPackagePreview>> UploadAsync(AppService service, HttpContext context, CancellationToken cancellationToken)
    {
        // The reader stops at its own limit, which is above Kestrel's default.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = PackageLimits.Default.MaxCompressedBytes + 1;
        }
        return TypedResults.Ok(await service.UploadAsync(context.User, context.Request.Body, cancellationToken));
    }

    private static async Task<Accepted<Job>> InstallAsync(string packageId, InstallAppRequest request, AppService service, HttpContext context, CancellationToken cancellationToken)
    {
        var job = await service.InstallAsync(context.User, packageId, request, cancellationToken);
        return TypedResults.Accepted($"/api/jobs/{job.Id}", job);
    }

    private static async Task<Accepted<Job>> UninstallAsync(string appId, [FromQuery] string data, AppService service, HttpContext context, CancellationToken cancellationToken)
    {
        if (!(data is "keep" or "delete" && Enum.TryParse<AppDataChoice>(data, ignoreCase: true, out var choice)))
        {
            throw new Errors.PithyxException(Errors.ErrorCodes.RequestBodyInvalid);
        }
        var job = await service.UninstallAsync(context.User, appId, choice, cancellationToken);
        return TypedResults.Accepted($"/api/jobs/{job.Id}", job);
    }

    private static async Task<FileContentHttpResult> IconAsync(string appId, AppService service, HttpContext context, CancellationToken cancellationToken)
    {
        var icon = await service.IconAsync(context.User, appId, cancellationToken);
        var headers = context.Response.Headers;
        headers[HeaderNames.ContentSecurityPolicy] = "default-src 'none'; sandbox";
        headers[HeaderNames.XContentTypeOptions] = "nosniff";
        // The URL carries the package hash, so a new version is a new URL.
        headers[HeaderNames.CacheControl] = "private, max-age=86400";
        return TypedResults.File(icon, "image/svg+xml");
    }
}
