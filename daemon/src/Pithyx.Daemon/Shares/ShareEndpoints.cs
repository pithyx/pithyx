using Microsoft.AspNetCore.Http.HttpResults;

namespace Pithyx.Daemon.Shares;

/// <summary>Shares for users, and share management for administrators until M3 brings the full storage settings.</summary>
public static class ShareEndpoints
{
    public static IServiceCollection AddPithyxShares(this IServiceCollection services) => services.AddSingleton<ShareService>();

    public static void MapShareEndpoints(this RouteGroupBuilder api)
    {
        var shares = api.MapGroup("/shares").WithTags("Shares");
        shares.MapGet("", ListAsync)
            .WithName("ListShares")
            .WithDescription("The shares the user can use, with the user's access. Administrators get every share and its path on the host.");
        shares.MapPost("", CreateAsync)
            .WithName("CreateShare")
            .WithDescription("Administrators only. Registers an existing folder on the host as a share; nobody but administrators has access until it is granted.");
        shares.MapDelete("/{shareId:long}", DeleteAsync)
            .WithName("DeleteShare")
            .WithDescription("Administrators only. Removes the share from Pithyx; its files stay on the host.");
        shares.MapPut("/{shareId:long}/access/{userId:long}", SetAccessAsync)
            .WithName("SetShareAccess")
            .WithDescription("Administrators only. Sets a user's access to a share: none, read or readWrite. Applies to the user's next request.");
    }

    private static async Task<Ok<IReadOnlyList<ShareInfo>>> ListAsync(ShareService shares, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Ok(await shares.ListAsync(context.User, cancellationToken));

    private static async Task<Created<ShareInfo>> CreateAsync(NewShare share, ShareService shares, HttpContext context, CancellationToken cancellationToken)
    {
        var created = await shares.CreateAsync(context.User, share, cancellationToken);
        return TypedResults.Created($"/api/shares/{created.Id}", created);
    }

    private static async Task<NoContent> DeleteAsync(long shareId, ShareService shares, HttpContext context, CancellationToken cancellationToken)
    {
        await shares.DeleteAsync(context.User, shareId, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> SetAccessAsync(long shareId, long userId, ShareAccessRequest request, ShareService shares, HttpContext context, CancellationToken cancellationToken)
    {
        await shares.SetAccessAsync(context.User, shareId, userId, request.Access, cancellationToken);
        return TypedResults.NoContent();
    }
}
