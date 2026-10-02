using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Pithyx.Daemon.FileServices;
using Pithyx.Daemon.Logs;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.FileServices;

public sealed class FileServiceApiTests : IDisposable
{
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _admin;

    public FileServiceApiTests() => _admin = _factory.CreateApiClient();

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AdministratorsSwitchTheServicesAndSetTheWorkgroup()
    {
        await _admin.SetupAdminAsync();

        var initial = await _admin.GetFromJsonAsync<JsonElement>("/api/file-services", Token);
        Assert.True(initial.GetProperty("smbEnabled").GetBoolean());
        Assert.False(initial.GetProperty("sftpEnabled").GetBoolean());
        Assert.Equal("WORKGROUP", initial.GetProperty("workgroup").GetString());

        using var changed = await _admin.PutAsJsonAsync("/api/file-services", new { sftpEnabled = true, workgroup = "home-lab" }, Token);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var body = await changed.JsonAsync();
        Assert.True(body.GetProperty("sftpEnabled").GetBoolean());
        Assert.Equal("running", body.GetProperty("sftp").GetString());
        Assert.Equal("HOME-LAB", body.GetProperty("workgroup").GetString());

        using var invalid = await _admin.PutAsJsonAsync("/api/file-services", new { workgroup = "far too long a workgroup" }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("fileservices.workgroup.invalid", (await invalid.JsonAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task UsersCarryTheirProtocolRightsAndWhetherTheServicesKnowTheirPassword()
    {
        await _admin.SetupAdminAsync();
        using (var created = await _admin.PostAsJsonAsync("/api/users", new { username = "rita", password = "river-candle-orbit-7", sftp = true }, Token))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var rita = await created.JsonAsync();
            Assert.True(rita.GetProperty("smb").GetBoolean());
            Assert.True(rita.GetProperty("sftp").GetBoolean());
            Assert.True(rita.GetProperty("fileServicePassword").GetBoolean());
        }
        var users = await _admin.GetFromJsonAsync<JsonElement>("/api/users", Token);
        var ritaId = users.EnumerateArray().Single(user => user.GetProperty("username").GetString() == "rita").GetProperty("id").GetInt64();

        using var update = await _admin.PatchAsJsonAsync($"/api/users/{ritaId}", new { smb = false }, Token);
        var updated = await update.JsonAsync();
        Assert.False(updated.GetProperty("smb").GetBoolean());
        Assert.True(updated.GetProperty("sftp").GetBoolean());

        // Administrators have every right, whatever is stored.
        var admin = users.EnumerateArray().Single(user => user.GetProperty("username").GetString() == "admin");
        Assert.True(admin.GetProperty("smb").GetBoolean());
        Assert.True(admin.GetProperty("sftp").GetBoolean());
    }

    [Fact]
    public async Task OnlyAdministratorsSeeTheFileServices()
    {
        await _admin.SetupAdminAsync();
        using (await _admin.PostAsJsonAsync("/api/users", new { username = "tom", password = "river-candle-orbit-7" }, Token))
        {
        }
        using var tom = _factory.CreateApiClient();
        using (await tom.LoginAsync("tom", "river-candle-orbit-7"))
        {
        }
        using var response = await tom.GetAsync("/api/file-services", Token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public void AuditLinesAskForOnePoolReadAndReportDeletes()
    {
        var time = new FakeTimeProvider();
        var usage = new StorageUsage();
        var reads = 0;
        usage.Changed += () => reads++;
        using var activity = new FileServiceActivity(usage, time);
        var deleted = new List<string>();
        activity.SmbDeleted += deleted.Add;
        PendingLogEntry Entry(string source, string message) => new(time.GetUtcNow(), LogLevelName.Debug, source, message);

        Assert.False(activity.Observe(Entry("system/smbd", "rita|Media|pwrite_send|ok|/tank/pithyx/shares/media/a.txt"), SmbConfig.AuditIdentifier));
        Assert.False(activity.Observe(Entry("system/smbd", "rita|Media|unlinkat|ok|/tank/pithyx/shares/media/a.txt"), SmbConfig.AuditIdentifier));
        // Samba's own session lines under the audit identifier stay in the Log Center.
        Assert.True(activity.Observe(Entry("system/smbd", "pam_unix(samba:session): session closed for user rita"), SmbConfig.AuditIdentifier));
        Assert.Equal(["Media"], deleted);
        Assert.Equal(0, reads);
        time.Advance(FileServiceActivity.Settle);
        Assert.Equal(1, reads);

        // An SFTP session of a Pithyx user reads the pools once a minute while it is open and once at its end.
        activity.SftpUsers = new HashSet<string>(["rita"]);
        Assert.True(activity.Observe(Entry("system/ssh", "pam_unix(sshd:session): session opened for user rita(uid=20001) by rita(uid=0)"), "sshd-session"));
        Assert.True(activity.Observe(Entry("system/ssh", "pam_unix(sshd:session): session opened for user gabriel(uid=1000) by gabriel(uid=0)"), "sshd-session"));
        time.Advance(FileServiceActivity.SessionInterval);
        Assert.Equal(2, reads);
        activity.Observe(Entry("system/ssh", "pam_unix(sshd:session): session closed for user rita"), "sshd-session");
        time.Advance(FileServiceActivity.Settle);
        Assert.Equal(3, reads);
        time.Advance(FileServiceActivity.SessionInterval * 3);
        Assert.Equal(3, reads);
    }
}
