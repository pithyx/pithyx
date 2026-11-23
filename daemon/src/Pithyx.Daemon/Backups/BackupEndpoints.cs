using Microsoft.AspNetCore.Http.HttpResults;
using Pithyx.Daemon.Auth;

namespace Pithyx.Daemon.Backups;

/// <summary>The backup app's API (#392, D414). Administrators only.</summary>
public static class BackupEndpoints
{
    public static IServiceCollection AddPithyxBackups(this IServiceCollection services) =>
        services.AddSingleton<BackupSsh>()
            .AddSingleton<BackupTargetService>()
            .AddSingleton<BackupRestic>()
            .AddSingleton<BackupJobService>()
            .AddSingleton<BackupScheduler>()
            .AddHostedService(provider => provider.GetRequiredService<BackupScheduler>());

    public static void MapBackupTargetEndpoints(this RouteGroupBuilder api)
    {
        var targets = api.MapGroup("/backups/targets").WithTags("Backups").RequireAuthorization(Policies.Admin);
        targets.MapGet("", async Task<Ok<BackupTargetList>> (BackupTargetService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.ListAsync(cancellationToken)))
            .WithName("ListBackupTargets")
            .WithDescription("Administrators only. Every backup target with its confirmed host key, the public half of its SSH key and its last connection test.");
        targets.MapPost("/host-keys", async Task<Ok<BackupHostKeyScan>> (BackupHostKeyScanRequest request, BackupTargetService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.ScanAsync(request, cancellationToken)))
            .WithName("ScanBackupHostKeys")
            .WithDescription("Administrators only. Reads the host keys the server offers, with their SHA-256 fingerprints, for the administrator to compare before a target is saved. Nothing is stored.");
        targets.MapPost("", async Task<Created<BackupTarget>> (BackupTargetRequest request, BackupTargetService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                var target = await service.CreateAsync(context.User, request, cancellationToken);
                return TypedResults.Created($"/api/backups/targets/{target.Id}", target);
            })
            .WithName("CreateBackupTarget")
            .WithDescription("Administrators only. Saves a target with the host key the administrator confirmed and makes its own SSH key; its public half goes into the server's authorized_keys.");
        targets.MapPut("/{id:long}", async Task<Ok<BackupTarget>> (long id, BackupTargetRequest request, BackupTargetService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.UpdateAsync(context.User, id, request, cancellationToken)))
            .WithName("UpdateBackupTarget")
            .WithDescription("Administrators only. Changes a target. A new host or port needs the new server's confirmed host key; the SSH key stays.");
        targets.MapPut("/{id:long}/host-key", async Task<Ok<BackupTarget>> (long id, BackupHostKeyRequest request, BackupTargetService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.SetHostKeyAsync(context.User, id, request, cancellationToken)))
            .WithName("SetBackupTargetHostKey")
            .WithDescription("Administrators only. Pins a new host key after the administrator compared its fingerprint, for example after the server was reinstalled.");
        targets.MapPost("/{id:long}/test", async Task<Ok<BackupTarget>> (long id, BackupTargetService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.TestAsync(id, cancellationToken)))
            .WithName("TestBackupTarget")
            .WithDescription("Administrators only. Signs in to the target with its key, creates its folder if missing, writes and removes a test file, and keeps the outcome in lastTest.");
        targets.MapDelete("/{id:long}", async Task<NoContent> (long id, BackupTargetService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                await service.DeleteAsync(context.User, id, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("DeleteBackupTarget")
            .WithDescription("Administrators only. Removes the target and its SSH key. The public key stays in the server's authorized_keys until it is removed there. Refused with backup.target.in_use while jobs use it.");
    }

    public static void MapBackupJobEndpoints(this RouteGroupBuilder api)
    {
        var backups = api.MapGroup("/backups").WithTags("Backups").RequireAuthorization(Policies.Admin);
        backups.MapGet("/sources", async Task<Ok<BackupSourceList>> (BackupJobService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.SourcesAsync(cancellationToken)))
            .WithName("ListBackupSources")
            .WithDescription("Administrators only. Every shared folder and home, and whether a job can back it up: only datasets of a pool can be backed up from a snapshot.");
        var jobs = backups.MapGroup("/jobs");
        jobs.MapGet("", async Task<Ok<BackupJobList>> (BackupJobService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.ListAsync(cancellationToken)))
            .WithName("ListBackupJobs")
            .WithDescription("Administrators only. Every backup job with its settings, next times, last run, last check and whether it is overdue. Repository keys are never returned.");
        jobs.MapGet("/{id:long}", async Task<Ok<BackupJob>> (long id, BackupJobService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.GetAsync(id, cancellationToken)))
            .WithName("GetBackupJob")
            .WithDescription("Administrators only. One backup job, as in the list.");
        jobs.MapPost("", async Task<Created<BackupJobCreated>> (BackupJobCreateRequest request, BackupJobService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                var created = await service.CreateAsync(context.User, request, cancellationToken);
                return TypedResults.Created($"/api/backups/jobs/{created.Job.Id}", created);
            })
            .WithName("CreateBackupJob")
            .WithDescription("Administrators only. Makes the job's restic repository on the target with a new key, returned only in this answer, or connects to an existing repository with existingKey.");
        jobs.MapPut("/{id:long}", async Task<Ok<BackupJob>> (long id, BackupJobSettings request, BackupJobService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.UpdateAsync(id, request, cancellationToken)))
            .WithName("UpdateBackupJob")
            .WithDescription("Administrators only. Changes what a job backs up and when; its target, repository and key stay.");
        jobs.MapDelete("/{id:long}", async Task<NoContent> (long id, BackupJobService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                await service.DeleteAsync(context.User, id, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("DeleteBackupJob")
            .WithDescription("Administrators only. Deletes the job, its runs and its key. The repository stays on the target; without the key nobody can read it.");
        jobs.MapGet("/{id:long}/runs", async Task<Ok<BackupRunList>> (long id, BackupJobService service, CancellationToken cancellationToken, int? limit) =>
                TypedResults.Ok(await service.RunsAsync(id, limit ?? 50, cancellationToken)))
            .WithName("ListBackupRuns")
            .WithDescription("Administrators only. The job's runs and checks, newest first, at most limit (default 50, up to 500).");
        jobs.MapPost("/{id:long}/run", async Task<Accepted<Jobs.Job>> (long id, BackupJobService service, CancellationToken cancellationToken) =>
            {
                var job = await service.StartAsync(id, BackupRunKind.Backup, BackupRunTrigger.Manual, cancellationToken);
                return TypedResults.Accepted($"/api/jobs/{job.Id}", job);
            })
            .WithName("RunBackupJob")
            .WithDescription("Administrators only. Starts a run now as a box job; its progress is in the job's topic and it is cancelled through the jobs API. Refused with backup.job.busy while a run or check goes on.");
        jobs.MapPost("/{id:long}/check", async Task<Accepted<Jobs.Job>> (long id, BackupJobService service, CancellationToken cancellationToken) =>
            {
                var job = await service.StartAsync(id, BackupRunKind.Check, BackupRunTrigger.Manual, cancellationToken);
                return TypedResults.Accepted($"/api/jobs/{job.Id}", job);
            })
            .WithName("CheckBackupJob")
            .WithDescription("Administrators only. Starts the integrity check of the job's repository now as a box job, reading back the job's share of the data.");
    }
}
