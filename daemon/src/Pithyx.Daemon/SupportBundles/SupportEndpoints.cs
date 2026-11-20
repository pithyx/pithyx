using Microsoft.AspNetCore.Http.HttpResults;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Jobs;

namespace Pithyx.Daemon.SupportBundles;

/// <summary>The support bundle (D412) the Log Center makes. Administrators only.</summary>
public static class SupportEndpoints
{
    public static IServiceCollection AddPithyxSupport(this IServiceCollection services, SupportBundleOptions options) =>
        services.AddSingleton(options).AddSingleton<SupportBundleService>();

    public static void MapSupportEndpoints(this RouteGroupBuilder api)
    {
        var support = api.MapGroup("/system/support-bundle").WithTags("System").RequireAuthorization(Policies.Admin);
        support.MapGet("", Ok<SupportBundleState> (SupportBundleService service) => TypedResults.Ok(service.State()))
            .WithName("GetSupportBundle")
            .WithDescription("Administrators only. The support bundle on the box, kept for an hour, and the job making a new one.");
        support.MapPost("", CreateAsync)
            .WithName("CreateSupportBundle")
            .WithDescription("Administrators only. Starts the job that makes a support bundle of the chosen parts; it replaces the bundle on the box. Secrets never go in, except in the memory dump, which needs a step-up.");
        support.MapGet("/{id}/download", PhysicalFileHttpResult (string id, SupportBundleService service) =>
            {
                var (path, fileName) = service.File(id);
                return TypedResults.PhysicalFile(path, "application/zip", fileName);
            })
            .Produces<byte[]>(StatusCodes.Status200OK, "application/zip")
            .WithName("DownloadSupportBundle")
            .WithDescription("Administrators only. The support bundle as a zip file.");
        support.MapDelete("/{id}", NoContent (string id, SupportBundleService service) =>
            {
                service.Delete(id);
                return TypedResults.NoContent();
            })
            .WithName("DeleteSupportBundle")
            .WithDescription("Administrators only. Deletes the support bundle from the box.");
    }

    private static async Task<Accepted<Job>> CreateAsync(CreateSupportBundleRequest request, SupportBundleService service, StepUps stepUps, HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Parts?.Contains(SupportBundlePart.MemoryDump) == true)
        {
            await stepUps.EnsureAsync(context, request.Password, request.TotpCode, cancellationToken);
        }
        var job = await service.StartAsync(context.User, request.Parts ?? [], cancellationToken);
        return TypedResults.Accepted($"/api/jobs/{job.Id}", job);
    }
}
