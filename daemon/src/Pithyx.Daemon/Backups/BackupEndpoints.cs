using Microsoft.AspNetCore.Http.HttpResults;
using Pithyx.Daemon.Auth;

namespace Pithyx.Daemon.Backups;

/// <summary>The backup app's API (#392, D414). Administrators only.</summary>
public static class BackupEndpoints
{
    public static IServiceCollection AddPithyxBackups(this IServiceCollection services) =>
        services.AddSingleton<BackupSsh>().AddSingleton<BackupTargetService>();

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
            .WithDescription("Administrators only. Removes the target and its SSH key. The public key stays in the server's authorized_keys until it is removed there.");
    }
}
