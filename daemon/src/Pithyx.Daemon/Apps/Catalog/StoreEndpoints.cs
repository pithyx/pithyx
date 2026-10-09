using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Jobs;

namespace Pithyx.Daemon.Apps.Catalog;

/// <summary>The Store's catalogs, updates and settings (S3, D328, D329), for administrators.</summary>
public static class StoreEndpoints
{
    public static void MapStoreEndpoints(this RouteGroupBuilder api)
    {
        var store = api.MapGroup("/store").WithTags("Store").RequireAuthorization(Policies.Admin);
        store.MapGet("", async Task<Ok<StoreState>> (StoreService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.GetStateAsync(context.User, cancellationToken)))
            .WithName("GetStore")
            .WithDescription("Administrators only. The Store's settings with the next check time, every catalog with its status, and the newer versions of installed apps. The topic store/admin carries the same and its changes.");
        store.MapPut("/settings", async Task<Ok<StoreSettings>> (StoreSettingsRequest request, StoreService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.SetSettingsAsync(context.User, request, cancellationToken)))
            .WithName("SetStoreSettings")
            .WithDescription("Administrators only. Sets the update policy (automatic, notify, none), the daily check time (HH:mm in the box's time zone; the box adds its own delay of up to an hour) and the channel (stable reads each catalog's branch catalog, beta its branch catalog-next). A new channel fetches every catalog at once.");
        store.MapPost("/check", async Task<Accepted<Job>> (StoreService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                var job = await service.CheckNowAsync(context.User, cancellationToken);
                return TypedResults.Accepted($"/api/jobs/{job.Id}", job);
            })
            .WithName("CheckStore")
            .WithDescription("Administrators only. Checks now as a job: fetches every enabled catalog, then installs automatic updates and announces the others, as the daily check does.");
        store.MapPost("/catalogs", async Task<Ok<CatalogInfo>> (AddCatalogRequest request, StoreService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.AddCatalogAsync(context.User, request, cancellationToken)))
            .WithName("AddCatalog")
            .WithDescription("Administrators only. Adds a custom catalog (unsigned, shown as unverified) by its repository URL: https:// for a public one, which is fetched at once as a job, or SSH (git@host:owner/repository.git) for a private one, for which the box makes a deploy key; add its public key to the repository and fetch the catalog then.");
        store.MapPatch("/catalogs/{key:long}", async Task<Ok<CatalogInfo>> (long key, UpdateCatalogRequest request, StoreService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.UpdateCatalogAsync(context.User, key, request, cancellationToken)))
            .WithName("UpdateCatalog")
            .WithDescription("Administrators only. Switches a catalog on (it is fetched at once) or off, or sets (user:token) or removes (empty) the read token for its private images.");
        store.MapDelete("/catalogs/{key:long}", async Task<NoContent> (long key, StoreService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                await service.RemoveCatalogAsync(context.User, key, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("RemoveCatalog")
            .WithDescription("Administrators only. Removes a custom catalog and its copy; apps installed from it stay and get no more updates from it. The project's catalogs can only be switched off (store.catalog.built_in).");
        store.MapPost("/catalogs/{key:long}/sync", async Task<Accepted<Job>> (long key, StoreService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                var job = await service.SyncAsync(context.User, key, cancellationToken);
                return TypedResults.Accepted($"/api/jobs/{job.Id}", job);
            })
            .WithName("SyncCatalog")
            .WithDescription("Administrators only. Fetches one catalog now as a job. A failure keeps the last verified copy and is shown with the catalog.");
        store.MapGet("/apps", async Task<Ok<StoreAppList>> ([FromQuery] string? catalog, [FromQuery] string? q, StoreService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.SearchAsync(context.User, catalog, q, cancellationToken)))
            .WithName("SearchStoreApps")
            .WithDescription("Administrators only. The apps of the enabled catalogs (or of one, by its id) at the newest version this box can run, whose id, name, description or author contains q.");
        store.MapGet("/catalogs/{catalog}/apps/{appId}", async Task<Ok<StoreAppDetails>> (string catalog, string appId, StoreService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.DetailsAsync(context.User, catalog, appId, cancellationToken)))
            .WithName("GetStoreApp")
            .WithDescription("Administrators only. A catalog app's detail page: its newest version for this box with the manifest, screenshots, homepage, the link to its source in the catalog, and every published version.");
        store.MapGet("/catalogs/{catalog}/apps/{appId}/{version}/icon", MediaAsync)
            .Produces<byte[]>(StatusCodes.Status200OK, "image/svg+xml")
            .WithName("GetStoreAppIcon")
            .WithDescription("Administrators only. The icon of a catalog app's version, checked against the index, with Content-Security-Policy: default-src 'none'; sandbox and nosniff.");
        store.MapGet("/catalogs/{catalog}/apps/{appId}/{version}/screenshots/{name}", MediaAsync)
            .Produces<byte[]>(StatusCodes.Status200OK, "image/png")
            .WithName("GetStoreAppScreenshot")
            .WithDescription("Administrators only. A screenshot of a catalog app's version (PNG, JPEG or WebP), checked against the index, with Content-Security-Policy: default-src 'none'; sandbox and nosniff.");
        store.MapPost("/catalogs/{catalog}/apps/{appId}/{version}/package", async Task<Ok<AppPackagePreview>> (string catalog, string appId, string version, StoreService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.StageAsync(context.User, catalog, appId, version, cancellationToken)))
            .WithName("StageStoreApp")
            .WithDescription("Administrators only. Takes a catalog app's version from the box's verified copy, checks it like an uploaded package plus the index's hashes and images (store.package.hash_mismatch), the box's version (app.package.box_too_old) and, for an older version, the same dataVersion (app.package.downgrade_unsupported), and keeps it for 15 minutes. Install it with POST /api/apps/packages/{packageId}/install.");

        var apps = api.MapGroup("/apps").WithTags("Apps");
        apps.MapPost("/{appId}/rollback", async Task<Accepted<Job>> (string appId, AppService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                var job = await service.RollbackAsync(context.User, appId, cancellationToken);
                return TypedResults.Accepted($"/api/jobs/{job.Id}", job);
            })
            .RequireAuthorization(Policies.Admin)
            .WithName("RollbackApp")
            .WithDescription("Administrators only. Goes back to the version before the app's last update as a job: the app's dataset returns to the snapshot of that update, so its data goes back too, and the previous version runs again (app.app.no_previous without one).");
        apps.MapPut("/{appId}/update-policy", async Task<Ok<AppDetails>> (string appId, AppUpdatePolicyRequest request, AppService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.SetUpdatePolicyAsync(context.User, appId, request, cancellationToken)))
            .RequireAuthorization(Policies.Admin)
            .WithName("SetAppUpdatePolicy")
            .WithDescription("Administrators only. The app's own update policy (automatic, notify, none), or null to follow the box's.");
    }

    private static async Task<FileContentHttpResult> MediaAsync(string catalog, string appId, string version, string? name, StoreService service, HttpContext context, CancellationToken cancellationToken)
    {
        var (bytes, contentType) = await service.MediaAsync(context.User, catalog, appId, version, name, cancellationToken);
        var headers = context.Response.Headers;
        headers[HeaderNames.ContentSecurityPolicy] = "default-src 'none'; sandbox";
        headers[HeaderNames.XContentTypeOptions] = "nosniff";
        headers[HeaderNames.CacheControl] = "private, max-age=86400";
        return TypedResults.File(bytes, contentType);
    }
}
