using Microsoft.AspNetCore.Http.HttpResults;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Snapshots;

namespace Pithyx.Daemon.Shares;

/// <summary>Shares for users, and the shared folders of the Settings app for administrators.</summary>
public static class ShareEndpoints
{
    public static IServiceCollection AddPithyxShares(this IServiceCollection services)
    {
        services.AddSingleton<ShareService>();
        services.AddSingleton<ShareChanges>();
        services.AddSingleton(provider => provider.GetRequiredService<ShareChanges>().Definition());
        services.AddSingleton<SharePlanner>();
        services.AddHostedService<ShareChangePublisher>();
        services.AddSingleton<ShareQuotas>();
        services.AddHostedService(provider => provider.GetRequiredService<ShareQuotas>());
        services.AddSingleton(provider => ShareQuotas.Definition(provider.GetRequiredService<ShareQuotas>));
        return services;
    }

    public static void MapShareEndpoints(this RouteGroupBuilder api)
    {
        var shares = api.MapGroup("/shares").WithTags("Shares");
        shares.MapGet("", ListAsync)
            .WithName("ListShares")
            .WithDescription("The shares the user can use, with the user's access. Administrators get every share and its path on the host. The shares/list topic says when the list may have changed.");
        shares.MapGet("/apps", AppsAsync)
            .WithName("GetAppsShare")
            .WithDescription("The special folder Apps (D409) for a user who may use it: its share id and one folder per app on the app pool with whether the app is installed and whether its backend runs (Files warns before writing into a running app's folder). Administrators also get how many SMB connections use it now. fs.share.not_found without access or before there is an app pool.");
        shares.MapPost("", CreateAsync)
            .RequireAuthorization(Policies.Admin)
            .WithName("CreateShare")
            .WithDescription("Administrators only, and only on a development machine (fs.share.folder_dev_only elsewhere). Registers an existing folder on the host as a share; nobody but administrators has access until it is granted. Shared folders are otherwise created as datasets with POST /api/shares/plans.");
        shares.MapGet("/details", DetailsAsync)
            .RequireAuthorization(Policies.Admin)
            .WithName("ListShareDetails")
            .WithDescription("Administrators only. Every share with its description, dataset, used and available space, quota, how many users may read or write and its SMB and SFTP settings.");
        shares.MapPatch("/{shareId:long}", UpdateAsync)
            .RequireAuthorization(Policies.Admin)
            .WithName("UpdateShare")
            .WithDescription("Administrators only. Changes the name shown, the description, whether SMB, SFTP, WebDAV and FTP offer the share (and whether SMB clients must encrypt) and whether SMB and Files show its daily restore points as previous versions; the folder and the dataset stay where they are.");
        shares.MapGet("/{shareId:long}/grants", GrantsAsync)
            .RequireAuthorization(Policies.Admin)
            .WithName("GetShareGrants")
            .WithDescription("Administrators only. The grants of users and groups on the share (read or readWrite) and every user with the access it ends up with: the union of its own grants and those of its groups, read and write for administrators, nothing while deactivated.");
        shares.MapPut("/{shareId:long}/grants", SetGrantsAsync)
            .RequireAuthorization(Policies.Admin)
            .WithName("SetShareGrants")
            .WithDescription("Administrators only. Replaces every grant of users and groups on the share. Applies to the next request, on disk (the share's Linux groups) and over SMB and SFTP.");
        shares.MapPut("/{shareId:long}/restore-points", SetRestorePointsAsync)
            .RequireAuthorization(Policies.Admin)
            .WithName("SetShareRestorePoints")
            .WithDescription("Administrators only. Switches the shared folder's daily restore points (snapshots of its dataset kept 7 days) on or off. A folder share without a dataset answers fs.share.no_dataset.");
        shares.MapGet("/{shareId:long}/quotas", QuotasAsync)
            .RequireAuthorization(Policies.Admin)
            .WithName("GetShareQuotas")
            .WithDescription("Administrators only. Every user who may write to the shared folder and every user with a quota there, with the space the files they own take and their quota (D411). The space is read after writes and pool events and announced on shares/quotas; null until it was read. Homes answer fs.share.not_found, Apps fs.share.system, a folder share fs.share.no_dataset.");
        shares.MapPut("/{shareId:long}/quotas/{userId:long}", SetQuotaAsync)
            .RequireAuthorization(Policies.Admin)
            .WithName("SetShareQuota")
            .WithDescription("Administrators only. Sets a user's quota on the shared folder (at least 1 MiB, fs.quota.invalid otherwise) or removes it with null, and applies it to the dataset before it answers. ZFS counts every file the user owns there and refuses writes over the quota with fs.quota.exceeded, a few seconds late.");
        shares.MapPost("/plans", PlanAsync)
            .RequireAuthorization(Policies.Admin)
            .WithName("PlanShareAction")
            .WithDescription("Administrators only. Returns the exact commands of creating a shared folder as a dataset (share.create) or deleting one (share.delete: keep its data, or destroy its dataset with every snapshot listed), with a plan id to run them with POST /api/storage/plans/{planId}/run. Runs nothing.");
    }

    private static async Task<Ok<IReadOnlyList<ShareInfo>>> ListAsync(ShareService shares, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Ok(await shares.ListAsync(context.User, cancellationToken));

    private static Ok<Apps.AppsShareState> AppsAsync(Apps.AppsShare apps, HttpContext context) =>
        TypedResults.Ok(apps.State(context.User));

    private static async Task<Created<ShareInfo>> CreateAsync(
        NewShare share,
        ShareService shares,
        ShareChanges changes,
        DeploymentEnvironment environment,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!ShareService.IsAdmin(context.User))
        {
            throw new PithyxException(ErrorCodes.AuthPermissionDenied);
        }
        // Folder shares exist only in development (step 6); simulation reads a dev environment from its fixtures.
        if (environment.Kind != DeploymentKind.Dev)
        {
            throw new PithyxException(ErrorCodes.FsShareFolderDevOnly);
        }
        var created = await shares.CreateAsync(context.User, share, cancellationToken);
        changes.Announce();
        return TypedResults.Created($"/api/shares/{created.Id}", created);
    }

    private static async Task<Ok<IReadOnlyList<ShareDetails>>> DetailsAsync(ShareService shares, PoolInventory pools, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Ok(await shares.ListDetailsAsync(context.User, pools.Current, cancellationToken));

    private static async Task<Ok<ShareDetails>> UpdateAsync(
        long shareId,
        ShareUpdate update,
        ShareService shares,
        ShareChanges changes,
        PoolInventory pools,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        await shares.UpdateAsync(context.User, shareId, update, cancellationToken);
        changes.Announce();
        var details = await shares.ListDetailsAsync(context.User, pools.Current, cancellationToken);
        return TypedResults.Ok(details.Single(share => share.Id == shareId));
    }

    private static async Task<Ok<Permissions.ShareGrants>> GrantsAsync(long shareId, Permissions.GrantService grants, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Ok(await grants.ShareGrantsAsync(context.User, shareId, cancellationToken));

    private static async Task<Ok<Permissions.ShareGrants>> SetGrantsAsync(
        long shareId,
        Permissions.ShareGrantsRequest request,
        Permissions.GrantService grants,
        HttpContext context,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await grants.ReplaceShareGrantsAsync(context.User, shareId, request, cancellationToken));

    private static async Task<NoContent> SetRestorePointsAsync(
        long shareId,
        RestorePointsRequest request,
        ShareService shares,
        ShareChanges changes,
        SnapshotService snapshots,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!ShareService.IsAdmin(context.User))
        {
            throw new PithyxException(ErrorCodes.AuthPermissionDenied);
        }
        var share = await shares.FindAsync(shareId, cancellationToken) ?? throw new PithyxException(ErrorCodes.FsShareNotFound);
        var dataset = share.Dataset ?? throw new PithyxException(ErrorCodes.FsShareNoDataset);
        await snapshots.SetSchedulesAsync(SharePlanner.RestorePoints(dataset, request.Enabled), cancellationToken);
        changes.Announce();
        return TypedResults.NoContent();
    }

    private static async Task<Ok<ShareQuotaList>> QuotasAsync(long shareId, ShareQuotas quotas, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Ok(await quotas.ListAsync(context.User, shareId, cancellationToken));

    private static async Task<Ok<ShareQuotaList>> SetQuotaAsync(long shareId, long userId, ShareQuotaRequest request, ShareQuotas quotas, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Ok(await quotas.SetAsync(context.User, shareId, userId, request, cancellationToken));

    private static async Task<Ok<StoragePlanPreview>> PlanAsync(
        SharePlanRequest request,
        SharePlanner planner,
        CommandPlanStore plans,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!ShareService.IsAdmin(context.User))
        {
            throw new PithyxException(ErrorCodes.AuthPermissionDenied);
        }
        var result = await planner.PlanAsync(request, cancellationToken);
        var plan = result.Plan!;
        return TypedResults.Ok(new StoragePlanPreview(
            plans.Save(CurrentUser.Id(context.User), plan),
            [],
            [.. plan.Steps.Select(step => new PlannedCommand(step.Command.FileName, step.Command.Arguments))],
            result.Datasets,
            plan.ExpiresAt,
            [],
            // Deleting a shared folder always needs its name typed; the request carries it already.
            request.Action == "share.delete",
            []));
    }
}
