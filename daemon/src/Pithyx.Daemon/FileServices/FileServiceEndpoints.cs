using System.Security.Claims;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.FileServices;

/// <summary>The file service settings of the box (SMB and SFTP), for administrators.</summary>
public sealed class FileServiceSettingsService(SqliteConnectionFactory connections, FileServiceSync sync, FileServiceOptions options)
{
    public async Task<FileServiceSettings> GetAsync(ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        await using var connection = await connections.OpenAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<(long Smb, string Workgroup, long Sftp)>("SELECT smb_enabled, smb_workgroup, sftp_enabled FROM file_services WHERE id = 1");
        return new FileServiceSettings(row.Smb == 1, row.Workgroup, row.Sftp == 1, sync.SmbState, sync.SftpState);
    }

    /// <summary>Saves the changes and applies them before it answers, so the states in the answer are current.</summary>
    public async Task<FileServiceSettings> UpdateAsync(ClaimsPrincipal caller, FileServiceSettingsRequest request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        var workgroup = request.Workgroup?.Trim().ToUpperInvariant();
        if (workgroup is not null && !SmbConfig.IsValidWorkgroup(workgroup))
        {
            throw new PithyxException(ErrorCodes.FileServicesWorkgroupInvalid, new Dictionary<string, object?> { ["max"] = SmbConfig.MaxWorkgroupLength });
        }
        if (options.Apply && request.SmbEnabled == true && !FileServiceSync.Installed("smbd"))
        {
            throw new PithyxException(ErrorCodes.FileServicesServiceUnavailable, new Dictionary<string, object?> { ["service"] = "smb" });
        }
        if (options.Apply && request.SftpEnabled == true && !FileServiceSync.Installed("sshd"))
        {
            throw new PithyxException(ErrorCodes.FileServicesServiceUnavailable, new Dictionary<string, object?> { ["service"] = "sftp" });
        }
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync(
                """
                UPDATE file_services SET smb_enabled = coalesce(@smb, smb_enabled), smb_workgroup = coalesce(@workgroup, smb_workgroup),
                    sftp_enabled = coalesce(@sftp, sftp_enabled)
                WHERE id = 1
                """,
                new { smb = request.SmbEnabled, workgroup, sftp = request.SftpEnabled });
        }
        await sync.ReconcileAsync(force: false, cancellationToken);
        return await GetAsync(caller, cancellationToken);
    }

    private static void RequireAdmin(ClaimsPrincipal user)
    {
        if (!user.IsInRole(SessionAuthenticationHandler.AdminRole))
        {
            throw new PithyxException(ErrorCodes.AuthPermissionDenied);
        }
    }
}

/// <summary>SMB and SFTP.</summary>
public static class FileServiceEndpoints
{
    public static IServiceCollection AddPithyxFileServices(this IServiceCollection services, FileServiceOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<FileServiceChanges>();
        services.AddSingleton<FileServicePasswords>();
        services.AddSingleton<FileServiceActivity>();
        services.AddSingleton<SftpViews>();
        services.AddSingleton<SmbRecycle>();
        services.AddSingleton<FileServiceSync>();
        services.AddSingleton<FileServiceSettingsService>();
        return services;
    }

    public static void MapFileServiceEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/file-services").WithTags("FileServices");
        group.MapGet("", async (FileServiceSettingsService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.GetAsync(context.User, cancellationToken)))
            .WithName("GetFileServices")
            .WithDescription("Administrators only. Whether SMB and SFTP are switched on, the SMB workgroup and how each service is doing.");
        group.MapPut("", async (FileServiceSettingsRequest request, FileServiceSettingsService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.UpdateAsync(context.User, request, cancellationToken)))
            .WithName("UpdateFileServices")
            .WithDescription("Administrators only. Switches SMB or SFTP on or off and sets the SMB workgroup; the configuration is checked and applied before the answer. Switching SFTP off never stops sshd: it only stops letting Pithyx users in.");
    }
}
