using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Backup;

/// <summary>Creating, checking and restoring a configuration backup through the API in simulation (D293).</summary>
public sealed class ConfigBackupApiTests : IAsyncLifetime
{
    private const string BackupPassword = "orange tractor sunset";
    private static readonly string[] _users = ["users"];
    private static readonly string[] _storage = ["storage"];
    private static readonly string[] _certificates = ["certificates"];
    private static readonly string[] _usersSystemCertificates = ["users", "system", "certificates"];
    private static readonly string[] _storageCertificates = ["storage", "certificates"];
    private readonly DaemonFactory _factory = new();
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync()
    {
        _admin = _factory.CreateApiClient();
        await _admin.SetupAdminAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        await _factory.DisposeAsync();
    }

    // A file of its own in the certificate folder, which the box's certificates never read or replace.
    private string TlsFile => Path.Combine(_factory.DataDirectory, "tls", "probe.bin");

    private async Task<T> QueryAsync<T>(string sql)
    {
        await using var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token);
        return await connection.ExecuteScalarAsync<T>(sql) ?? throw new InvalidOperationException("No value.");
    }

    private async Task<byte[]> CreateAsync()
    {
        using var response = await _admin.PostAsync("/api/system/backup", new { password = BackupPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(".pithyxbackup", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'), StringComparison.Ordinal);
        return await response.Content.ReadAsByteArrayAsync(Token);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> InspectAsync(byte[] archive, string password)
    {
        using var response = await _admin.PostAsync("/api/system/backup/inspect", new { archive = Convert.ToBase64String(archive), password });
        return (response.StatusCode, await response.JsonAsync());
    }

    [Fact]
    public async Task ARestoreBringsBackDeletedUsersAndSettingsAndSignsEverybodyOut()
    {
        await _factory.InsertUserAsync("anna");
        Directory.CreateDirectory(Path.GetDirectoryName(TlsFile)!);
        await File.WriteAllBytesAsync(TlsFile, [1, 2, 3], Token);
        using (var region = await _admin.PutAsJsonAsync("/api/system/region", new { language = "de", region = "AT" }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, region.StatusCode);
        }
        var archive = await CreateAsync();
        // Encrypted: neither the database nor a user name can be read from the file.
        Assert.True(archive.AsSpan().IndexOf("SQLite format"u8) < 0 && archive.AsSpan().IndexOf("anna"u8) < 0);

        await QueryAsync<long>("DELETE FROM users WHERE username = 'anna' RETURNING id");
        using (var region = await _admin.PutAsJsonAsync("/api/system/region", new { language = "en", region = (string?)null }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, region.StatusCode);
        }
        await File.WriteAllBytesAsync(TlsFile, [9], Token);

        var (status, summary) = await InspectAsync(archive, BackupPassword);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, summary.GetProperty("users").GetInt32());
        Assert.Equal(["users", "sharedFolders", "storage", "system", "certificates", "apps", "reverseProxy"], summary.GetProperty("categories").EnumerateArray().Select(category => category.GetString()));
        Assert.StartsWith("1.0.0", summary.GetProperty("version").GetString(), StringComparison.Ordinal);

        using var restore = await _admin.PostAsync("/api/system/backup/restore", new
        {
            restoreId = summary.GetProperty("restoreId").GetString(),
            categories = _usersSystemCertificates,
        });
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        var result = await restore.JsonAsync();
        Assert.True(result.GetProperty("restarting").GetBoolean());
        Assert.Empty(result.GetProperty("problems").EnumerateArray());
        Assert.True(_factory.Services.GetRequiredService<DaemonRestart>().Requested);

        Assert.Equal(1, await QueryAsync<long>("SELECT count(*) FROM users WHERE username = 'anna'"));
        Assert.Equal("AT", await QueryAsync<string>("SELECT region FROM system_region"));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(TlsFile, Token));
        using var session = await _admin.GetAsync("/api/auth/session", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        // The checked backup is used up.
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_factory.DataDirectory, "backup-work")));
    }

    [Fact]
    public async Task AFailedStepAfterTheDatabaseIsReportedAndTheDaemonStillRestarts()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TlsFile)!);
        await File.WriteAllBytesAsync(TlsFile, [1, 2, 3], Token);
        var archive = await CreateAsync();
        var (_, summary) = await InspectAsync(archive, BackupPassword);
        // The certificate folder cannot be written: a file stands where it should be.
        Directory.Delete(Path.GetDirectoryName(TlsFile)!, recursive: true);
        await File.WriteAllBytesAsync(Path.GetDirectoryName(TlsFile)!, [0], Token);

        using var restore = await _admin.PostAsync("/api/system/backup/restore", new { restoreId = summary.GetProperty("restoreId").GetString(), categories = _storageCertificates });

        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        var problems = (await restore.JsonAsync()).GetProperty("problems").EnumerateArray().ToList();
        Assert.Equal(["certificates tls"], problems.Select(problem => $"{problem.GetProperty("category").GetString()} {problem.GetProperty("step").GetString()}"));
        Assert.True(_factory.Services.GetRequiredService<DaemonRestart>().Requested);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_factory.DataDirectory, "backup-work")));
    }

    [Fact]
    public async Task AWrongPasswordOrAnotherFileIsRefused()
    {
        var archive = await CreateAsync();

        Assert.Equal("system.backup.password_wrong", (await InspectAsync(archive, "not the password")).Body.GetProperty("code").GetString());
        Assert.Equal("system.backup.invalid", (await InspectAsync("hello"u8.ToArray(), BackupPassword)).Body.GetProperty("code").GetString());
        using var notBase64 = await _admin.PostAsync("/api/system/backup/inspect", new { archive = "%%%", password = BackupPassword });
        Assert.Equal("system.backup.invalid", await notBase64.ErrorCodeAsync());
        using var tooLarge = await _admin.PostAsync("/api/system/backup/inspect", new { archive = new string('A', 23 * 1024 * 1024), password = BackupPassword });
        Assert.Equal((HttpStatusCode.RequestEntityTooLarge, "system.backup.too_large"), (tooLarge.StatusCode, await tooLarge.ErrorCodeAsync()));
    }

    [Fact]
    public async Task TheBackupPasswordFollowsThePasswordRules()
    {
        using var response = await _admin.PostAsync("/api/system/backup", new { password = "short" });

        Assert.Equal((HttpStatusCode.BadRequest, "auth.password.too_short"), (response.StatusCode, await response.ErrorCodeAsync()));
    }

    [Fact]
    public async Task ARestoreNeedsTheCheckedBackupAndOnlyPartsItHolds()
    {
        // A box always has its certificates (D346); without the files the backup holds no certificates part.
        Directory.Delete(Path.Combine(_factory.DataDirectory, "tls"), recursive: true);
        var archive = await CreateAsync();
        var (_, summary) = await InspectAsync(archive, BackupPassword);
        var restoreId = summary.GetProperty("restoreId").GetString();
        Assert.DoesNotContain("certificates", summary.GetProperty("categories").EnumerateArray().Select(category => category.GetString()));

        using var unknown = await _admin.PostAsync("/api/system/backup/restore", new { restoreId = "unknown", categories = _users });
        Assert.Equal((HttpStatusCode.NotFound, "system.backup.restore_expired"), (unknown.StatusCode, await unknown.ErrorCodeAsync()));
        using var missing = await _admin.PostAsync("/api/system/backup/restore", new { restoreId, categories = _certificates });
        Assert.Equal((HttpStatusCode.BadRequest, "system.backup.category_missing"), (missing.StatusCode, await missing.ErrorCodeAsync()));
        using var nothing = await _admin.PostAsync("/api/system/backup/restore", new { restoreId, categories = Array.Empty<string>() });
        Assert.Equal((HttpStatusCode.BadRequest, "request.body.invalid"), (nothing.StatusCode, await nothing.ErrorCodeAsync()));

        _factory.Time.Advance(TimeSpan.FromMinutes(16));
        using var expired = await _admin.PostAsync("/api/system/backup/restore", new { restoreId, categories = _storage });
        Assert.Equal("system.backup.restore_expired", await expired.ErrorCodeAsync());
        Assert.False(_factory.Services.GetRequiredService<DaemonRestart>().Requested);
    }

    [Fact]
    public async Task OnlyAdministratorsBackUpOrRestore()
    {
        var anna = await _factory.InsertUserAsync("anna");
        using var user = _factory.ClientWithCookie(await _factory.SessionCookieAsync(anna));

        foreach (var (path, body) in new (string, object)[]
        {
            ("/api/system/backup", new { password = BackupPassword }),
            ("/api/system/backup/inspect", new { archive = "", password = BackupPassword }),
            ("/api/system/backup/restore", new { restoreId = "x", categories = _users }),
        })
        {
            using var response = await user.PostAsync(path, body);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }
}
