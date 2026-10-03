using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.FileServices.Ftp;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Storage.Integration;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.FileServices;

/// <summary>
/// As root on the dev VM: what WebDAV and FTP write belongs to the writing user's uid and the group users, like an
/// upload in Files (<c>fchownat</c>), because both go through the file service.
/// </summary>
[Trait("Category", FileVdevs.Category)]
public sealed class ProtocolOwnerIntegrationTests : IDisposable
{
    private const string Password = "river-candle-orbit-7";
    private readonly DaemonFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WebDavAndFtpWriteFilesThatBelongToTheUser()
    {
        FileVdevs.RequireEnabled();
        using var admin = _factory.CreateApiClient();
        await admin.SetupAdminAsync();
        (await admin.PutAsJsonAsync("/api/file-services", new { webDavEnabled = true, ftpEnabled = true }, Token)).EnsureSuccessStatusCode();
        var shared = (await admin.GetFromJsonAsync<JsonElement>("/api/shares", Token)).EnumerateArray().Single();
        var sharedId = shared.GetProperty("id").GetInt64();
        var folder = shared.GetProperty("path").GetString()!;
        using var created = await admin.PostAsJsonAsync("/api/users", new { username = "rita", password = Password, grants = new[] { new { resource = "protocol:webdav", level = "allow" }, new { resource = "protocol:ftp", level = "allow" } } }, Token);
        var rita = await created.JsonAsync();
        await admin.SetShareAccessAsync(sharedId, rita.GetProperty("id").GetInt64(), "readWrite");
        var owner = $"{rita.GetProperty("uid").GetInt64()}:100";

        using var dav = _factory.CreateClient();
        dav.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"rita:{Password}")));
        using (var put = await dav.PutAsync("/webdav/Shared/dav.txt", new StringContent("dav"), Token))
        {
            Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        }
        using (var mkcol = await dav.SendAsync(new HttpRequestMessage(new HttpMethod("MKCOL"), "/webdav/Shared/DavFolder"), Token))
        {
            Assert.Equal(HttpStatusCode.Created, mkcol.StatusCode);
        }

        FtpClient.Pinned = (await admin.GetFromJsonAsync<JsonElement>("/api/file-services", Token)).GetProperty("ftpFingerprint").GetString()!;
        await using (var ftp = await FtpClient.ConnectAsync(_factory.Services.GetRequiredService<FtpServer>().Port))
        {
            await ftp.StartTlsAsync();
            Assert.StartsWith("230", await ftp.SignInAsync("rita", Password), StringComparison.Ordinal);
            await ftp.SendAsync("PBSZ 0");
            await ftp.SendAsync("PROT P");
            Assert.StartsWith("226", await ftp.SendDataAsync("STOR /Shared/ftp.txt", "ftp"), StringComparison.Ordinal);
        }

        var commands = new ProcessCommandRunner(NullLogger<ProcessCommandRunner>.Instance);
        foreach (var name in new[] { "dav.txt", "DavFolder", "ftp.txt" })
        {
            var stat = await commands.RunAsync(new CommandRequest("stat", ["-c", "%u:%g", Path.Combine(folder, name)]), Token);
            Assert.Equal(owner, stat.StandardOutput.Trim());
        }
    }
}
