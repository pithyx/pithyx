using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Net.Http.Headers;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Files;

namespace Pithyx.Daemon.Backups;

/// <summary>The backup app's API (#392, D414). Administrators only.</summary>
public static class BackupEndpoints
{
    public static IServiceCollection AddPithyxBackups(this IServiceCollection services, BackupBrowserOptions browse) =>
        services.AddSingleton<BackupSsh>()
            .AddSingleton<BackupTargetService>()
            .AddSingleton<BackupRestic>()
            .AddSingleton(browse)
            .AddSingleton<BackupBrowser>()
            .AddHostedService(provider => provider.GetRequiredService<BackupBrowser>())
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
        backups.MapGet("/runs", async Task<Ok<BackupRunList>> (BackupJobService service, CancellationToken cancellationToken, long? jobId, string? kind, string? state, DateTimeOffset? since, DateTimeOffset? until, long? before, int? limit) =>
                TypedResults.Ok(await service.HistoryAsync(new BackupRunQuery(jobId, Filter<BackupRunKind>(kind), Filter<BackupRunState>(state), since, until, before, limit ?? 200), cancellationToken)))
            .WithName("ListBackupHistory")
            .WithDescription("Administrators only. The runs and checks of every job (or of jobId), newest first, optionally only of one kind, one state (the values of kind and state in a run), started at or after since and before until; before is the id of the last run of the previous page. At most limit (default 200, up to 2000).");
        backups.MapGet("/days", async Task<Ok<BackupDayList>> (BackupJobService service, CancellationToken cancellationToken, string? from, string? to) =>
                TypedResults.Ok(await service.DaysAsync(from, to, cancellationToken)))
            .WithName("ListBackupDays")
            .WithDescription("Administrators only. The runs and checks of every job per day in the box's time zone (each with its start and end in UTC) from from to to (yyyy-MM-dd, both included, at most 400 days; left out, the last 30 days): how many succeeded, had warnings or failed, how many jobs ran and how many of them without a failure, the bytes added, and what the repositories took on their targets at the end of the day.");
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
        jobs.MapDelete("/{id:long}", async Task<NoContent> (long id, BackupJobService service, HttpContext context, CancellationToken cancellationToken, bool? data) =>
            {
                await service.DeleteAsync(context.User, id, cancellationToken, data == true);
                return TypedResults.NoContent();
            })
            .WithName("DeleteBackupJob")
            .WithDescription("Administrators only. Deletes the job, its runs and its key. The repository stays on the target; without the key nobody can read it. A server backup's folder stays in its shared folder with its versions, unless data=true destroys it with them.");
        var pulls = backups.MapGroup("/pulls");
        pulls.MapPost("", async Task<Created<BackupJobCreated>> (BackupPullCreateRequest request, BackupJobService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                var created = await service.CreatePullAsync(context.User, request, cancellationToken);
                return TypedResults.Created($"/api/backups/jobs/{created.Job.Id}", created);
            })
            .WithName("CreateBackupPull")
            .WithDescription("Administrators only. Makes a server backup (#396): its folder in the shared folder as a dataset of its own, given the shared folder's owner, mode and ACL, which rsync over SSH fills from the server on its schedule. The server is a backup target of the purpose server. The answer's key is always null.");
        pulls.MapPut("/{id:long}", async Task<Ok<BackupJob>> (long id, BackupPullUpdateRequest request, BackupJobService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.UpdatePullAsync(id, request, cancellationToken)))
            .WithName("UpdateBackupPull")
            .WithDescription("Administrators only. Changes what a server backup pulls and when; its server and its folder stay (shareId and folder are ignored).");
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
        jobs.MapGet("/{id:long}/versions", async Task<Ok<BackupVersionList>> (long id, BackupJobService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.VersionsAsync(id, cancellationToken)))
            .WithName("ListBackupVersions")
            .WithDescription("Administrators only. The versions in the job's repository, newest first, as the box saw them after its last run or change: size, files, what each added, why the retention kept it, whether it is protected, and for the newest the files that changed against the one before (at most 100). Reads nothing from the target.");
        jobs.MapPut("/{id:long}/versions/{versionId:long}/protection", async Task<Ok<BackupVersion>> (long id, long versionId, BackupVersionProtection request, BackupJobService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.ProtectAsync(context.User, id, versionId, request.Protected, cancellationToken)))
            .WithName("ProtectBackupVersion")
            .WithDescription("Administrators only. Protects a version so the retention never deletes it, or releases it again (restic's tag pithyx-protected). Refused with backup.job.busy while a run or check goes on.");
        jobs.MapPost("/{id:long}/browse", async Task<NoContent> (long id, BackupJobService service, CancellationToken cancellationToken) =>
            {
                await service.BrowseAsync(id, cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("BrowseBackupJob")
            .WithDescription("Administrators only. Opens the job's repository for browsing, or keeps an open session for another ten minutes: reads its versions again and mounts it read-only on the box. A session ends ten minutes after its last request and before a run, a check or a change of a version; reading then answers backup.browse.closed. Refused with backup.job.busy while a run or check goes on.");
        jobs.MapGet("/{id:long}/versions/{versionId:long}/files", async Task<Ok<BackupFileList>> (long id, long versionId, string? path, BackupJobService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.FilesAsync(id, versionId, path, cancellationToken)))
            .WithName("ListBackupVersionFiles")
            .WithDescription("Administrators only. A folder of a version (path as the shared folder's or home's name and the path below it; empty for the folders the version holds), each entry marked added or modified against the version before. Needs a browse session.");
        jobs.MapGet("/{id:long}/history", async Task<Ok<BackupFileHistory>> (long id, string path, BackupJobService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.HistoryAsync(id, path, cancellationToken)))
            .WithName("GetBackupFileHistory")
            .WithDescription("Administrators only. A file or folder in every version of the job, newest first, with size, time, owner and mode, and as it is on the box now. Needs a browse session.");
        jobs.MapGet("/{id:long}/versions/{versionId:long}/content", DownloadAsync)
            .WithName("DownloadBackupVersionEntry")
            .WithDescription("Administrators only. A file of a version, or a folder as a zip of its files and folders (symlinks and devices left out). Logged. Needs a browse session.");
        jobs.MapPost("/{id:long}/restore", async Task<Accepted<Jobs.Job>> (long id, BackupRestoreRequest request, BackupJobService service, HttpContext context, CancellationToken cancellationToken) =>
            {
                var job = await service.RestoreAsync(context.User, id, request, cancellationToken);
                return TypedResults.Accepted($"/api/jobs/{job.Id}", job);
            })
            .WithName("RestoreBackupVersion")
            .WithDescription("Administrators only. Restores files and folders of a version as a box job of the caller: where they were (with the folders above them) or into a folder, with owners, modes, ACLs and times. Folders in place are merged; a file in place is kept next to the restored one (keepBoth, the restored one named after the version's time), replaced (overwrite) or left (skip). Logged. Refused with backup.job.busy while a run or check goes on.");
        jobs.MapPost("/{id:long}/key", RevealKeyAsync)
            .WithName("RevealBackupJobKey")
            .WithDescription("Administrators only. The job's repository key, for an administrator who lost the copy shown at creation. Needs the password again and, with two-factor sign-in on, a current authenticator code; failures count like failed sign-ins. Logged without the key.");
    }

    private static async Task<Ok<BackupJobKey>> RevealKeyAsync(
        long id,
        StepUpRequest request,
        BackupJobService service,
        AuthService auth,
        LoginRateLimiter limiter,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var username = context.User.Identity?.Name ?? "";
        limiter.EnsureAllowed(context, username);
        await AuthEndpoints.CountFailuresAsync(limiter, context, username, async () =>
        {
            await auth.ConfirmStepUpAsync(CurrentUser.Id(context.User), request.Password, request.TotpCode, cancellationToken);
            return true;
        });
        return TypedResults.Ok(await service.RevealKeyAsync(context.User, id, cancellationToken));
    }

    private static async Task<IResult> DownloadAsync(long id, long versionId, string path, BackupJobService service, HttpContext context, CancellationToken cancellationToken)
    {
        var (handle, stat, name) = await service.OpenVersionEntryAsync(context.User, id, versionId, path, cancellationToken);
        var headers = context.Response.Headers;
        // A file from a backup must never run with the desktop's origin.
        headers[HeaderNames.ContentSecurityPolicy] = "sandbox; default-src 'none'";
        headers[HeaderNames.XContentTypeOptions] = "nosniff";
        headers[HeaderNames.CacheControl] = "private, no-store";
        if (stat.Kind == FsKind.Folder)
        {
            context.Response.RegisterForDispose(handle);
            return TypedResults.Stream(output => BackupJobService.WriteZipAsync(handle, name, output, cancellationToken), "application/zip", name + ".zip");
        }
        var stream = new FileStream(handle, FileAccess.Read, 1, isAsync: false);
        context.Response.RegisterForDispose(stream);
        return TypedResults.Stream(stream, "application/octet-stream", name, lastModified: stat.Modified);
    }

    // Query values are the lowercase names the API returns; the default binding would want the C# names.
    private static T? Filter<T>(string? value) where T : struct, Enum =>
        value is null ? null
        : Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && !char.IsAsciiDigit(value[0]) ? parsed
        : throw new PithyxException(ErrorCodes.RequestBodyInvalid);
}
