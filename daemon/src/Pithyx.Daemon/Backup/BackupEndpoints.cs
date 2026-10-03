using Microsoft.AspNetCore.Http.HttpResults;
using Pithyx.Daemon.Auth;

namespace Pithyx.Daemon.Backup;

/// <summary>Creating and restoring the configuration backup in Settings. Administrators only.</summary>
public static class BackupEndpoints
{
    public static IServiceCollection AddPithyxBackup(this IServiceCollection services, BackupOptions options) =>
        services.AddSingleton(options).AddSingleton<ConfigBackupService>();

    public static void MapBackupEndpoints(this RouteGroupBuilder api)
    {
        var backup = api.MapGroup("/system/backup").WithTags("System").RequireAuthorization(Policies.Admin);
        backup.MapPost("", async Task<FileContentHttpResult> (CreateBackupRequest request, ConfigBackupService backups, CancellationToken cancellationToken) =>
            {
                var (archive, fileName) = await backups.CreateAsync(request.Password, cancellationToken);
                return TypedResults.File(archive, "application/octet-stream", fileName);
            })
            .Produces<byte[]>(StatusCodes.Status200OK, "application/octet-stream")
            .WithName("CreateConfigBackup")
            .WithDescription("Administrators only. The configuration of the box (users, groups, passwords, shared folders, rights, file services, storage schedules, regional settings, certificates and SSH host keys) as one file, encrypted with the password. Files on the pools are not part of it.");
        backup.MapPost("/inspect", async Task<Ok<BackupSummary>> (InspectBackupRequest request, ConfigBackupService backups, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await backups.InspectAsync(CurrentUser.Id(context.User), request, cancellationToken)))
            .WithName("InspectConfigBackup")
            .WithDescription("Administrators only. Decrypts and checks a backup file and keeps it for 15 minutes for a restore by the same administrator; returns what it holds.");
        backup.MapPost("/restore", async Task<Ok<RestoreResult>> (RestoreBackupRequest request, ConfigBackupService backups, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await backups.RestoreAsync(CurrentUser.Id(context.User), request, cancellationToken)))
            .WithName("RestoreConfigBackup")
            .WithDescription("Administrators only. Replaces the chosen parts of the configuration with those of the checked backup, then restarts the daemon. Restoring users signs everybody out.");
    }
}
