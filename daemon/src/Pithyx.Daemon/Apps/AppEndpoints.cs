using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Pithyx.Daemon.Apps.Packages;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Permissions;

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
        services.AddHostedService<AppChangePublisher>();
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
                TypedResults.Ok(await service.SetEnabledAsync(context.User, appId, request, cancellationToken)))
            .RequireAuthorization(Policies.Admin)
            .WithName("UpdateApp")
            .WithDescription("Administrators only. Enables or disables an app. A disabled app keeps its data and grants, but nobody can open it and its frontend is not served.");
        apps.MapPut("/{appId}/permissions", async Task<Ok<AppDetails>> (string appId, AppPermissionsRequest request, AppService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.SetPermissionsAsync(context.User, appId, request, cancellationToken)))
            .RequireAuthorization(Policies.Admin)
            .WithName("SetAppPermissions")
            .WithDescription("Administrators only. Grants or revokes permissions the app's manifest asks for (app.permission.not_requested for any other); the listed ones change, the others stay. A revoked permission stays revoked across updates. Users' windows of the app learn it with their next token (A11).");
        apps.MapPut("/{appId}/storage/{storageId}", async Task<Ok<AppDetails>> (string appId, string storageId, AppStorageMappingRequest request, AppService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.MapStorageAsync(context.User, appId, storageId, request, cancellationToken)))
            .RequireAuthorization(Policies.Admin)
            .WithName("MapAppStorage")
            .WithDescription("Administrators only. Maps an existing folder of a shared folder to a storage request of the app's manifest, replacing an earlier mapping: read-only, or read and write when the request asks for it (app.mapping.invalid otherwise). The folder keeps its rights, snapshots and backups and survives an uninstall (D314).");
        apps.MapDelete("/{appId}/storage/{storageId}", async Task<Ok<AppDetails>> (string appId, string storageId, AppService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.UnmapStorageAsync(context.User, appId, storageId, cancellationToken)))
            .RequireAuthorization(Policies.Admin)
            .WithName("UnmapAppStorage")
            .WithDescription("Administrators only. Removes the folder mapped to a storage request of the app; the folder itself stays.");
        apps.MapPut("/{appId}/log-level", async Task<Ok<AppDetails>> (string appId, AppLogLevelRequest request, AppService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.SetLogLevelAsync(context.User, appId, request, cancellationToken)))
            .RequireAuthorization(Policies.Admin)
            .WithName("SetAppLogLevel")
            .WithDescription("Administrators only. Switches the app's debug log entries on (debug) or off (info, the default); info, warn and error always reach the Log Center (D305). The topic apps carries the level to the app's windows.");
        apps.MapGet("/{appId}/grants", async Task<Ok<AppGrants>> (string appId, GrantService grants, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await grants.AppGrantsAsync(context.User, appId, cancellationToken)))
            .RequireAuthorization(Policies.Admin)
            .WithName("GetAppGrants")
            .WithDescription("Administrators only. Who may use an installed third-party app: the grants of users and groups on app:<id> (the built-in group users stands for everybody) and every user's result.");
        apps.MapPut("/{appId}/grants", async Task<Ok<AppGrants>> (string appId, AppGrantsRequest request, GrantService grants, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await grants.ReplaceAppGrantsAsync(context.User, appId, request, cancellationToken)))
            .RequireAuthorization(Policies.Admin)
            .WithName("SetAppGrants")
            .WithDescription("Administrators only. Replaces the grants of users and groups on the app, each with level allow; without any, only administrators use it. Applies at once and is pushed to the users' desktops.");
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
