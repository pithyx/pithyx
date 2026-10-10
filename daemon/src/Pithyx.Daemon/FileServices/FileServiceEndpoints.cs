using System.Security.Claims;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.FileServices;

/// <summary>The file service settings of the box (SMB and SFTP), for administrators.</summary>
public sealed class FileServiceSettingsService(
    SqliteConnectionFactory connections,
    FileServiceSync sync,
    FileServiceOptions options,
    FileServiceChanges changes,
    Users.LinuxAccounts accounts,
    Ftp.FtpServer ftp,
    Network.Firewall firewall)
{
    public async Task<FileServiceSettings> GetAsync(ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        await using var connection = await connections.OpenAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<(long Smb, string Workgroup, long Sftp, long Ssh, long WebDav, long Ftp, long FtpPlain, long PrivateOnly)>(
            "SELECT smb_enabled, smb_workgroup, sftp_enabled, ssh_enabled, webdav_enabled, ftp_enabled, ftp_plain, private_networks_only FROM file_services WHERE id = 1");
        return new FileServiceSettings(row.Smb == 1, row.Workgroup, row.Sftp == 1, sync.SmbState, sync.SftpState, row.Ssh == 1, row.WebDav == 1, row.Ftp == 1, row.FtpPlain == 1, ftp.State, ftp.Port, ftp.Fingerprint, row.PrivateOnly == 1, firewall.Applies);
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
        if (options.Apply && (request.SftpEnabled == true || request.SshEnabled == true) && !FileServiceSync.Installed("sshd"))
        {
            throw new PithyxException(ErrorCodes.FileServicesServiceUnavailable, new Dictionary<string, object?> { ["service"] = "sftp" });
        }
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync(
                """
                UPDATE file_services SET smb_enabled = coalesce(@smb, smb_enabled), smb_workgroup = coalesce(@workgroup, smb_workgroup),
                    sftp_enabled = coalesce(@sftp, sftp_enabled), ssh_enabled = coalesce(@ssh, ssh_enabled),
                    webdav_enabled = coalesce(@webDav, webdav_enabled), ftp_enabled = coalesce(@ftp, ftp_enabled),
                    ftp_plain = coalesce(@ftpPlain, ftp_plain), private_networks_only = coalesce(@privateOnly, private_networks_only)
                WHERE id = 1
                """,
                new { smb = request.SmbEnabled, workgroup, sftp = request.SftpEnabled, ssh = request.SshEnabled, webDav = request.WebDavEnabled, ftp = request.FtpEnabled, ftpPlain = request.FtpPlainAllowed, privateOnly = request.PrivateNetworksOnly });
        }
        if (request.SshEnabled is not null)
        {
            // The shells of the users with the SSH login right follow.
            changes.Announce();
            await accounts.ReconcileAsync(cancellationToken);
        }
        await sync.ReconcileAsync(force: false, cancellationToken);
        await ftp.ReconcileAsync(cancellationToken);
        await firewall.ApplyAsync(cancellationToken);
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
        services.AddSingleton<ProtocolSignIn>();
        services.AddSingleton<ProtocolFiles>();
        services.AddSingleton<Ftp.FtpServer>();
        return services;
    }

    public static void MapFileServiceEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/file-services").WithTags("FileServices");
        group.MapGet("", async (FileServiceSettingsService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.GetAsync(context.User, cancellationToken)))
            .RequireAuthorization(Policies.Admin)
            .WithName("GetFileServices")
            .WithDescription("Administrators only. Whether SMB, SFTP, SSH logins, WebDAV and FTP are switched on, the SMB workgroup, how each service is doing, the FTP port, the FTPS certificate's fingerprint, whether the file services are limited to private networks and whether the firewall is applied here.");
        group.MapPut("", async (FileServiceSettingsRequest request, FileServiceSettingsService service, HttpContext context, CancellationToken cancellationToken) =>
                TypedResults.Ok(await service.UpdateAsync(context.User, request, cancellationToken)))
            .RequireAuthorization(Policies.Admin)
            .WithName("UpdateFileServices")
            .WithDescription("Administrators only. Switches SMB, SFTP or SSH logins on or off and sets the SMB workgroup; the configuration is checked and applied before the answer. SSH logins give users with the SSH login right a shell. Switching SFTP or SSH logins off never stops sshd: it only stops letting Pithyx users in. WebDAV answers at /webdav/, FTP with TLS (FTPS), and without TLS only while plain FTP is allowed, at the users' own risk. The firewall opens only the ports of the switched-on services, optionally only to private networks (RFC 1918, ULA, link-local).");
    }
}
