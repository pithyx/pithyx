using Microsoft.AspNetCore.Http.HttpResults;

namespace Vyxis.Daemon.Jobs;

/// <summary>Reading and cancelling jobs. Modules start jobs through <see cref="JobService"/>, not over the API.</summary>
public static class JobEndpoints
{
    public static IServiceCollection AddVyxisJobs(this IServiceCollection services)
    {
        services.AddSingleton<JobBoard>();
        services.AddSingleton(provider => provider.GetRequiredService<JobBoard>().Definition());
        services.AddSingleton<JobService>();
        services.AddHostedService<JobRunner>();
        services.AddHostedService<JobCleanupService>();
        return services;
    }

    public static void MapJobEndpoints(this RouteGroupBuilder api)
    {
        var jobs = api.MapGroup("/jobs").WithTags("Jobs");
        jobs.MapGet("", ListAsync)
            .WithName("ListJobs")
            .WithDescription(
                "The signed-in user's 100 most recent jobs, newest first. Finished jobs are kept for 7 days. Live "
                + "progress comes on the topic jobs/{id}.");
        jobs.MapGet("/{id:long}", GetAsync)
            .WithName("GetJob")
            .WithDescription("A job of the signed-in user; administrators can read every job. Others get 404 jobs.job.not_found.");
        jobs.MapPost("/{id:long}/cancel", CancelAsync)
            .WithName("CancelJob")
            .WithDescription(
                "Cancels a queued job right away and asks a running one to stop; it ends as cancelled once its work "
                + "gives up. Cancelling a finished job changes nothing.");
    }

    private static async Task<Ok<IReadOnlyList<Job>>> ListAsync(JobService jobs, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Ok(await jobs.ListAsync(context.User, cancellationToken));

    private static async Task<Ok<Job>> GetAsync(long id, JobService jobs, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Ok(await jobs.GetAsync(context.User, id, cancellationToken));

    private static async Task<NoContent> CancelAsync(long id, JobService jobs, HttpContext context, CancellationToken cancellationToken)
    {
        await jobs.CancelAsync(context.User, id, cancellationToken);
        return TypedResults.NoContent();
    }
}
