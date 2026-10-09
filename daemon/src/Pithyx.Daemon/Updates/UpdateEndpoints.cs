using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Jobs;

namespace Pithyx.Daemon.Updates;

/// <summary>Settings &gt; Updates (D338). Administrators only.</summary>
public static class UpdateEndpoints
{
    public static IServiceCollection AddPithyxUpdates(this IServiceCollection services, DaemonMode mode, string dataDirectory, string configDirectory)
    {
        var simulated = mode == DaemonMode.Simulated;
        var paths = simulated
            ? new UpdatePaths(
                Path.Combine(dataDirectory, "update"), Path.Combine(dataDirectory, "update-cache"), Path.Combine(dataDirectory, "update-run"),
                Path.Combine(dataDirectory, "update-top"), Path.Combine(dataDirectory, "release"), Path.Combine(dataDirectory, "daemon"))
            : UpdatePaths.Box;
        services.AddSingleton(paths);
        services.AddSingleton(provider => new UpdateOptions(
            simulated,
            Path.Combine(simulated ? dataDirectory : configDirectory, "update-source.json"),
            simulated ? SimulationKeys() : UpdateKeys.Project,
            SystemSettings.BuildInfo.Current.Version,
            simulated
                ? new UpdateSourceConfig(new Uri(Path.Combine(dataDirectory, "update-channels", "channel-{channel}", "channel.json")).AbsoluteUri.Replace("%7B", "{", StringComparison.Ordinal).Replace("%7D", "}", StringComparison.Ordinal), null)
                : UpdateSourceConfig.Default));
        services.AddSingleton<UpdateSystem>();
        services.AddSingleton(_ => new UpdateSource(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, allowFile: simulated));
        services.AddSingleton<UpdateService>();
        services.AddSingleton(provider => UpdateService.Definition(provider.GetRequiredService<UpdateService>));
        if (!BuildTimeDocument.IsGenerating)
        {
            services.AddHostedService(provider => provider.GetRequiredService<UpdateService>());
            if (mode == DaemonMode.Real)
            {
                services.AddHostedService<SystemMirrorMonitor>();
            }
        }
        return services;
    }

    /// <summary>
    /// Simulation (tests and E2E) trusts the keys of the corpus in <c>tools/release/fixtures</c> besides the project's,
    /// so fixtures signed with them install; a box never does.
    /// </summary>
    private static IReadOnlyList<Apps.Catalog.TrustedKey> SimulationKeys() =>
    [
        .. UpdateKeys.Project,
        new("test-release", "sixNtoP5AFf5nH7eNZ5NerpiyWVk5qSoPb6wT5gO64M=", [UpdateDocuments.Stable, UpdateDocuments.Beta]),
        new("test-beta", "JCPDBkCN3VyeHAQXe90zc3jpeo9omqqTJen8tZKaF9c=", [UpdateDocuments.Beta]),
    ];

    public static void MapUpdateEndpoints(this RouteGroupBuilder api)
    {
        var updates = api.MapGroup("/system/updates").WithTags("System").RequireAuthorization(Policies.Admin);
        updates.MapGet("", async (UpdateService service, HttpContext context) => TypedResults.Ok(await service.GetStatusAsync(context.User, context.RequestAborted)))
            .WithName("GetSystemUpdates")
            .WithDescription("Administrators only. The installed version, the channel and the daily check, what the last check found (the channel's newer version with its changelog, the Debian updates with the security fixes, or why the channel could not be read), an uploaded file waiting for its installation, the running update, the history and the snapshots that can be restored. Whether updates apply here at all (a machine not installed by the installer cannot). The topic system/updates sends the same.");
        updates.MapPost("/check", async (UpdateService service, HttpContext context) => TypedResults.Ok(await service.CheckNowAsync(context.User, context.RequestAborted)))
            .WithName("CheckSystemUpdates")
            .WithDescription("Administrators only. Reads the channel and Debian's lists now. Installs nothing.");
        updates.MapPut("/settings", async (UpdateSettingsRequest request, UpdateService service, HttpContext context) =>
                TypedResults.Ok(await service.SetSettingsAsync(context.User, request, context.RequestAborted)))
            .WithName("SetSystemUpdateSettings")
            .WithDescription("Administrators only. Switches the daily check (it only notifies) and the channel (stable or beta).");
        updates.MapPut("/file", UploadAsync)
            .WithMetadata(new DisableRequestSizeLimitAttribute())
            .WithName("UploadSystemUpdate")
            .WithDescription("Administrators only. Streams a .pithyxupdate as the raw body (at most 4 GB) and checks it completely before anything changes: its signature by a key the box trusts for its channel (a development box also takes unsigned files), every package's size and SHA-256, and that it is newer than the installed version and fits it. Keeps it for \"Install\" until another upload or a restart.");
        updates.MapDelete("/file", async (UpdateService service, HttpContext context) => TypedResults.Ok(await service.DiscardUploadAsync(context.User, context.RequestAborted)))
            .WithName("DiscardSystemUpdate")
            .WithDescription("Administrators only. Forgets the uploaded file.");
        updates.MapPost("/install", InstallAsync)
            .WithName("InstallSystemUpdate")
            .WithDescription("Administrators only. Installs the uploaded file or the channel's newest version as a job of the box: a snapshot of the system, the packages (optionally Debian's updates in the same snapshot), GRUB on every disk, then the restart and the health check, which rolls back to the snapshot by itself when the new version does not come up within five minutes.");
        updates.MapPost("/rollback", RollbackAsync)
            .WithName("RollBackSystem")
            .WithDescription("Administrators only. Restores a snapshot (the version typed as the confirmation) and restarts into it. Settings changed since the snapshot are lost; pools and their files are not touched.");
    }

    private static async Task<Ok<UpdateStatus>> UploadAsync(UpdateService service, HttpContext context, CancellationToken cancellationToken)
    {
        // The reader stops at its own limit, which is above Kestrel's default.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = UpdateFile.MaxBytes + 1;
        }
        return TypedResults.Ok(await service.UploadAsync(context.User, context.Request.Body, cancellationToken));
    }

    private static async Task<Accepted<Job>> InstallAsync(InstallUpdateRequest request, UpdateService service, HttpContext context, CancellationToken cancellationToken)
    {
        var job = await service.InstallAsync(context.User, request, cancellationToken);
        return TypedResults.Accepted($"/api/jobs/{job.Id}", job);
    }

    private static async Task<Accepted<Job>> RollbackAsync(RollbackRequest request, UpdateService service, HttpContext context, CancellationToken cancellationToken)
    {
        var job = await service.RollbackAsync(context.User, request, cancellationToken);
        return TypedResults.Accepted($"/api/jobs/{job.Id}", job);
    }
}
