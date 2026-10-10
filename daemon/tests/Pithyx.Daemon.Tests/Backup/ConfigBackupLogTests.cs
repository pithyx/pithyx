using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Backup;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Backup;

/// <summary>A backup reads every Samba hash with <c>pdbedit -L -w</c>; none of it may reach the Log Center or the log files.</summary>
public sealed class ConfigBackupLogTests : IAsyncDisposable
{
    // The NT hash of "password" in the simulation's pdbedit fixture.
    private const string NtHash = "8846F7EAEE8FB117AD06BDD830B7586C";
    private readonly DaemonFactory _factory = new();

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task ABackupLogsThePdbeditCommandButNoHash()
    {
        var workDirectory = Path.Combine(_factory.DataDirectory, "backup-work");
        await using var applying = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton(new BackupOptions(Apply: true, Path.Combine(_factory.DataDirectory, "tls"), SshDirectory: null, ShadowPath: null, workDirectory))));
        using var admin = applying.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Pithyx-Request", "1");
        await admin.SetupAdminAsync();

        using (var created = await admin.PostAsJsonAsync("/api/system/backup", new { password = "orange tractor sunset" }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        }

        var export = "";
        for (var attempt = 0; attempt < 50 && !export.Contains("pdbedit", StringComparison.Ordinal); attempt++)
        {
            await Task.Delay(100, Token);
            export = await admin.GetStringAsync("/api/logs/export?format=text", Token);
        }
        Assert.Contains("pdbedit", export, StringComparison.Ordinal);
        Assert.DoesNotContain(NtHash, export, StringComparison.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_factory.DataDirectory, "logs"), "*", SearchOption.AllDirectories))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            Assert.DoesNotContain(NtHash, await reader.ReadToEndAsync(Token), StringComparison.OrdinalIgnoreCase);
        }
    }
}
