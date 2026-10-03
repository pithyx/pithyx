using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;


using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.FileServices.Ftp;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.FileServices;

/// <summary>WebDAV and FTPS against the daemon in simulation, with users who may and may not use them.</summary>
public sealed class ProtocolApiTests : IDisposable
{
    private const string Password = "river-candle-orbit-7";
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _admin;

    public ProtocolApiTests() => _admin = _factory.CreateApiClient();

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The seeded share, rita with WebDAV and FTP and write access, tom with both protocols but read only, eve without the protocols.</summary>
    private async Task<long> PrepareAsync(object services)
    {
        await _admin.SetupAdminAsync();
        (await _admin.PutAsJsonAsync("/api/file-services", services, Token)).EnsureSuccessStatusCode();
        var shared = (await _admin.GetFromJsonAsync<JsonElement>("/api/shares", Token)).EnumerateArray().Single().GetProperty("id").GetInt64();
        foreach (var (name, rights) in new[] { ("rita", new[] { "protocol:webdav", "protocol:ftp" }), ("tom", ["protocol:webdav", "protocol:ftp"]), ("eve", Array.Empty<string>()) })
        {
            using var created = await _admin.PostAsJsonAsync("/api/users", new { username = name, password = Password, grants = rights.Select(resource => new { resource, level = "allow" }) }, Token);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var id = (await created.JsonAsync()).GetProperty("id").GetInt64();
            if (name != "eve")
            {
                await _admin.SetShareAccessAsync(shared, id, name == "rita" ? "readWrite" : "read");
            }
            else
            {
                await _admin.SetShareAccessAsync(shared, id, "readWrite");
            }
        }
        return shared;
    }

    private HttpClient Dav(string user, string password = Password)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        return client;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path, HttpContent? content = null, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = content };
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
        return await client.SendAsync(request, Token);
    }

    [Fact]
    public async Task WebDavServesTheSharedFoldersWithTheRightsOfEachUser()
    {
        var shared = await PrepareAsync(new { webDavEnabled = true });
        using var rita = Dav("rita");

        using (var root = await SendAsync(rita, "PROPFIND", "/webdav/", null, ("Depth", "1")))
        {
            Assert.Equal(HttpStatusCode.MultiStatus, root.StatusCode);
            Assert.Contains("<D:href>/webdav/Shared/</D:href>", await root.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        }
        using (var folder = await SendAsync(rita, "MKCOL", "/webdav/Shared/Notes"))
        {
            Assert.Equal(HttpStatusCode.Created, folder.StatusCode);
        }
        using (var put = await SendAsync(rita, "PUT", "/webdav/Shared/Notes/a%20b.txt", new StringContent("hello")))
        {
            Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        }
        using (var again = await SendAsync(rita, "PUT", "/webdav/Shared/Notes/a%20b.txt", new StringContent("hello again")))
        {
            Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        }
        Assert.Equal("hello again", await rita.GetStringAsync("/webdav/Shared/Notes/a%20b.txt", Token));
        using (var move = await SendAsync(rita, "MOVE", "/webdav/Shared/Notes/a%20b.txt", null, ("Destination", "http://localhost/webdav/Shared/c.txt")))
        {
            Assert.Equal(HttpStatusCode.Created, move.StatusCode);
        }
        using (var copy = await SendAsync(rita, "COPY", "/webdav/Shared/c.txt", null, ("Destination", "/webdav/Shared/Notes/d.txt")))
        {
            Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
        }
        using (var listing = await SendAsync(rita, "PROPFIND", "/webdav/Shared/Notes", null, ("Depth", "1")))
        {
            var xml = await listing.Content.ReadAsStringAsync(Token);
            Assert.Contains("/webdav/Shared/Notes/d.txt", xml, StringComparison.Ordinal);
            Assert.DoesNotContain("a%20b.txt", xml, StringComparison.Ordinal);
        }
        using (var lockResponse = await SendAsync(rita, "LOCK", "/webdav/Shared/new.txt", new StringContent("<?xml version=\"1.0\"?><D:lockinfo xmlns:D=\"DAV:\"/>")))
        {
            Assert.Equal(HttpStatusCode.Created, lockResponse.StatusCode);
            Assert.StartsWith("<opaquelocktoken:", lockResponse.Headers.GetValues("Lock-Token").Single(), StringComparison.Ordinal);
        }

        // A delete lands in the shared folder's recycle bin, like one in Files.
        using (var delete = await SendAsync(rita, "DELETE", "/webdav/Shared/c.txt"))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }
        var trash = await _admin.GetFromJsonAsync<JsonElement>($"/api/files/{shared}/trash", Token);
        Assert.Contains(trash.GetProperty("items").EnumerateArray(), item => item.GetProperty("name").GetString() == "c.txt" && item.GetProperty("deletedBy").GetString() == "rita");

        // Read only, no protocol right, a wrong password, WebDAV off.
        using var tom = Dav("tom");
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(tom, "PUT", "/webdav/Shared/tom.txt", new StringContent("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(tom, "DELETE", "/webdav/Shared/Notes/d.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tom.GetAsync("/webdav/Shared/Notes/d.txt", Token)).StatusCode);
        using var eve = Dav("eve");
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(eve, "PROPFIND", "/webdav/", null, ("Depth", "0"))).StatusCode);
        using var wrong = Dav("rita", "wrong-candle-orbit-7");
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(wrong, "PROPFIND", "/webdav/", null, ("Depth", "0"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(rita, "PROPFIND", "/webdav/Nope/", null, ("Depth", "0"))).StatusCode);
        (await _admin.PutAsJsonAsync("/api/file-services", new { webDavEnabled = false }, Token)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(rita, "PROPFIND", "/webdav/", null, ("Depth", "0"))).StatusCode);
    }

    [Fact]
    public async Task FtpNeedsTlsAndTheRightAndUsesTheFileService()
    {
        var shared = await PrepareAsync(new { ftpEnabled = true });
        var settings = await _admin.GetFromJsonAsync<JsonElement>("/api/file-services", Token);
        Assert.Equal("running", settings.GetProperty("ftp").GetString());
        FtpClient.Pinned = settings.GetProperty("ftpFingerprint").GetString()!;
        Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", FtpClient.Pinned);
        var port = _factory.Services.GetRequiredService<FtpServer>().Port;

        await using (var plain = await FtpClient.ConnectAsync(port))
        {
            Assert.StartsWith("530", await plain.SendAsync("USER rita"), StringComparison.Ordinal);
        }
        await using (var eve = await FtpClient.ConnectAsync(port))
        {
            await eve.StartTlsAsync();
            Assert.StartsWith("530", await eve.SignInAsync("eve", Password), StringComparison.Ordinal);
        }

        await using var rita = await FtpClient.ConnectAsync(port);
        await rita.StartTlsAsync();
        Assert.StartsWith("230", await rita.SignInAsync("rita", Password), StringComparison.Ordinal);
        Assert.StartsWith("521", await rita.SendAsync("LIST"), StringComparison.Ordinal);
        Assert.StartsWith("200", await rita.SendAsync("PBSZ 0"), StringComparison.Ordinal);
        Assert.StartsWith("200", await rita.SendAsync("PROT P"), StringComparison.Ordinal);
        Assert.Contains("Shared", await rita.ReceiveDataAsync("NLST"), StringComparison.Ordinal);
        Assert.StartsWith("250", await rita.SendAsync("CWD /Shared"), StringComparison.Ordinal);
        Assert.StartsWith("257 \"/Shared\"", await rita.SendAsync("PWD"), StringComparison.Ordinal);
        Assert.StartsWith("226", await rita.SendDataAsync("STOR hello.txt", "hello over ftps"), StringComparison.Ordinal);
        Assert.Equal("hello over ftps", await rita.ReceiveDataAsync("RETR hello.txt"));
        Assert.StartsWith("213 15", await rita.SendAsync("SIZE hello.txt"), StringComparison.Ordinal);
        Assert.Contains("type=file;size=15;", await rita.ReceiveDataAsync("MLSD"), StringComparison.Ordinal);
        Assert.StartsWith("257", await rita.SendAsync("MKD Inbox"), StringComparison.Ordinal);
        Assert.StartsWith("350", await rita.SendAsync("RNFR hello.txt"), StringComparison.Ordinal);
        Assert.StartsWith("250", await rita.SendAsync("RNTO Inbox/hi.txt"), StringComparison.Ordinal);
        Assert.StartsWith("250", await rita.SendAsync("DELE Inbox/hi.txt"), StringComparison.Ordinal);
        Assert.StartsWith("502", await rita.SendAsync("PORT 127,0,0,1,4,1"), StringComparison.Ordinal);
        var trash = await _admin.GetFromJsonAsync<JsonElement>($"/api/files/{shared}/trash", Token);
        Assert.Contains(trash.GetProperty("items").EnumerateArray(), item => item.GetProperty("name").GetString() == "hi.txt");

        await using var tom = await FtpClient.ConnectAsync(port);
        await tom.StartTlsAsync();
        Assert.StartsWith("230", await tom.SignInAsync("tom", Password), StringComparison.Ordinal);
        await tom.SendAsync("PBSZ 0");
        await tom.SendAsync("PROT P");
        Assert.StartsWith("550", await tom.SendDataAsync("STOR /Shared/tom.txt", "x"), StringComparison.Ordinal);
        Assert.StartsWith("550", await tom.SendAsync("MKD /Shared/TomsFolder"), StringComparison.Ordinal);

        // Switching FTP off ends every session.
        (await _admin.PutAsJsonAsync("/api/file-services", new { ftpEnabled = false }, Token)).EnsureSuccessStatusCode();
        Assert.Null(await rita.TrySendAsync("NOOP"));
    }

    [Fact]
    public async Task PlainFtpWorksOnlyWhileAllowedAndEndsWhenRefused()
    {
        await PrepareAsync(new { ftpEnabled = true });
        var settings = await _admin.GetFromJsonAsync<JsonElement>("/api/file-services", Token);
        Assert.False(settings.GetProperty("ftpPlainAllowed").GetBoolean());
        FtpClient.Pinned = settings.GetProperty("ftpFingerprint").GetString()!;
        var port = _factory.Services.GetRequiredService<FtpServer>().Port;

        (await _admin.PutAsJsonAsync("/api/file-services", new { ftpPlainAllowed = true }, Token)).EnsureSuccessStatusCode();
        Assert.True((await _admin.GetFromJsonAsync<JsonElement>("/api/file-services", Token)).GetProperty("ftpPlainAllowed").GetBoolean());

        await using var plain = await FtpClient.ConnectAsync(port);
        Assert.StartsWith("530", await plain.SignInAsync("eve", Password), StringComparison.Ordinal);
        Assert.StartsWith("230", await plain.SignInAsync("rita", Password), StringComparison.Ordinal);
        Assert.StartsWith("503", await plain.SendAsync("PROT P"), StringComparison.Ordinal);
        Assert.StartsWith("226", await plain.SendDataAsync("STOR /Shared/plain.txt", "hello in the clear"), StringComparison.Ordinal);
        Assert.Equal("hello in the clear", await plain.ReceiveDataAsync("RETR /Shared/plain.txt"));

        // An FTPS session may choose clear data connections too, and keeps working when plain FTP is refused again.
        await using var tls = await FtpClient.ConnectAsync(port);
        await tls.StartTlsAsync();
        Assert.StartsWith("230", await tls.SignInAsync("rita", Password), StringComparison.Ordinal);
        Assert.StartsWith("200", await tls.SendAsync("PROT C"), StringComparison.Ordinal);
        Assert.Equal("hello in the clear", await tls.ReceiveDataAsync("RETR /Shared/plain.txt"));

        (await _admin.PutAsJsonAsync("/api/file-services", new { ftpPlainAllowed = false }, Token)).EnsureSuccessStatusCode();
        Assert.Null(await plain.TrySendAsync("NOOP"));
        Assert.StartsWith("521", await tls.SendAsync("NLST"), StringComparison.Ordinal);
        Assert.StartsWith("536", await tls.SendAsync("PROT C"), StringComparison.Ordinal);
        Assert.StartsWith("200", await tls.SendAsync("PROT P"), StringComparison.Ordinal);
        Assert.Contains("plain.txt", await tls.ReceiveDataAsync("NLST /Shared"), StringComparison.Ordinal);
        await using var refused = await FtpClient.ConnectAsync(port);
        Assert.StartsWith("530", await refused.SendAsync("USER rita"), StringComparison.Ordinal);
    }
}
