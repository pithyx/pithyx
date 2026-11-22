using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Backups;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Backups;

/// <summary>Backup targets through the API (#393): saved only with a confirmed host key, each with its own SSH key.</summary>
public sealed class BackupTargetApiTests : IAsyncLifetime
{
    private const string GitHubKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl";
    private const string OtherKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGROjxMz6ZIWYK4dbwW+t0sbyhqFPj5hp9hpkGhgDB1d";

    private readonly DaemonFactory _factory = new();
    private readonly ScriptedCommandRunner _commands = new();
    private WebApplicationFactory<Program> _daemon = null!;
    private HttpClient _admin = null!;
    private CommandResult _sftp = new(0, "", "", TimeSpan.Zero);

    public async ValueTask InitializeAsync()
    {
        _commands.Handler = Answer;
        _daemon = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton(new BackupSsh(_commands))));
        _admin = _daemon.CreateClient();
        _admin.DefaultRequestHeaders.Add("X-Pithyx-Request", "1");
        await _admin.SetupAdminAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        await _daemon.DisposeAsync();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task AScanShowsTheServersKeysWithTheirFingerprints()
    {
        using var response = await _admin.PostAsync("/api/backups/targets/host-keys", new { host = "U1.Example.de", port = 23 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var keys = (await response.JsonAsync()).GetProperty("keys");
        Assert.Equal("ssh-ed25519", keys[0].GetProperty("type").GetString());
        Assert.Equal("SHA256:+k2DS16k//dZzjNhStd/auE6WNdHV8IwySL7UEOyg0o", keys[0].GetProperty("fingerprint").GetString());
        var scan = Assert.Single(_commands.Calls, call => call.FileName == "ssh-keyscan");
        Assert.Equal(["-T", "15", "-t", "ssh-ed25519,ecdsa-sha2-nistp256,ecdsa-sha2-nistp384,ecdsa-sha2-nistp521,ssh-rsa", "-p", "23", "--", "u1.example.de"], scan.Arguments);
    }

    [Fact]
    public async Task AServerWithoutKeysIsAFailedScan()
    {
        using var response = await _admin.PostAsync("/api/backups/targets/host-keys", new { host = "nothing.example.de" });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(ErrorCodes.BackupHostKeyScanFailed, await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task ATargetIsSavedOnlyWithAHostKeyAndGetsItsOwnSshKey()
    {
        using (var missing = await _admin.PostAsync("/api/backups/targets", new { name = "Box", host = "u1.example.de", user = "u1" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Equal("hostKey", (await missing.JsonAsync()).GetProperty("params").GetProperty("field").GetString());
        }

        var first = await CreateAsync("Storage box");
        var second = await CreateAsync("Second");

        Assert.Equal("sftp", first.GetProperty("kind").GetString());
        Assert.Equal(22, first.GetProperty("port").GetInt32());
        Assert.Equal("SHA256:+DiY3wvvV6TuJJhbpZisF/zLDA0zPMSvHdkr4UvCOqU", first.GetProperty("hostKey").GetProperty("fingerprint").GetString());
        Assert.StartsWith("ssh-ed25519 PUBLIC-", first.GetProperty("publicKey").GetString(), StringComparison.Ordinal);
        Assert.NotEqual(first.GetProperty("publicKey").GetString(), second.GetProperty("publicKey").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("lastTest").ValueKind);
        Assert.DoesNotContain("PRIVATE", await _admin.GetStringAsync("/api/backups/targets", Token), StringComparison.Ordinal);

        using var taken = await _admin.PostAsync("/api/backups/targets", new { name = "storage BOX", host = "h", user = "u", hostKey = GitHubKey });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal(ErrorCodes.BackupTargetNameTaken, await taken.ErrorCodeAsync());

        var audit = await AuditAsync();
        Assert.Contains(("backup.target.created", "Storage box"), audit);
    }

    [Fact]
    public async Task ATestConnectsOnlyWithThePinnedKeyAndKeepsItsOutcome()
    {
        var target = await CreateAsync("Box", path: "backups/pithyx");
        var id = target.GetProperty("id").GetInt64();
        _sftp = new CommandResult(255, "", "Host key verification failed.\nConnection closed\n", TimeSpan.Zero);

        var failed = await TestAsync(id);

        var test = failed.GetProperty("lastTest");
        Assert.False(test.GetProperty("succeeded").GetBoolean());
        Assert.Equal(ErrorCodes.BackupTargetHostKeyChanged, test.GetProperty("errorCode").GetString());
        var call = Assert.Single(_commands.Calls, call => call.FileName == "env");
        Assert.Contains("StrictHostKeyChecking=yes", call.Arguments);
        Assert.Contains("HostName=u1.example.de", call.Arguments);
        Assert.Equal(BackupSsh.Alias, call.Arguments[^1]);
        Assert.Contains("cd \"backups/pithyx\"", call.StandardInput, StringComparison.Ordinal);

        _sftp = new CommandResult(0, "", "", TimeSpan.Zero);
        var passed = await TestAsync(id);
        Assert.True(passed.GetProperty("lastTest").GetProperty("succeeded").GetBoolean());
        Assert.Equal(JsonValueKind.Null, passed.GetProperty("lastTest").GetProperty("errorCode").ValueKind);
    }

    [Fact]
    public async Task MovingATargetNeedsTheNewServersKeyAndForgetsTheTest()
    {
        var id = (await CreateAsync("Box")).GetProperty("id").GetInt64();
        await TestAsync(id);

        using (var renamed = await _admin.PutAsJsonAsync($"/api/backups/targets/{id}", new { name = "Renamed", host = "u1.example.de", user = "u1", path = "" }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
            Assert.NotEqual(JsonValueKind.Null, (await renamed.JsonAsync()).GetProperty("lastTest").ValueKind);
        }
        using (var moved = await _admin.PutAsJsonAsync($"/api/backups/targets/{id}", new { name = "Renamed", host = "u2.example.de", user = "u1" }, Token))
        {
            Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);
            Assert.Equal("hostKey", (await moved.JsonAsync()).GetProperty("params").GetProperty("field").GetString());
        }
        using (var moved = await _admin.PutAsJsonAsync($"/api/backups/targets/{id}", new { name = "Renamed", host = "u2.example.de", user = "u1", hostKey = OtherKey }, Token))
        {
            var target = await moved.JsonAsync();
            Assert.Equal("u2.example.de", target.GetProperty("host").GetString());
            Assert.Equal(OtherKey, target.GetProperty("hostKey").GetProperty("key").GetString());
            Assert.Equal(JsonValueKind.Null, target.GetProperty("lastTest").ValueKind);
        }
    }

    [Fact]
    public async Task ANewHostKeyIsPinnedOnlyWhenConfirmedAndAudited()
    {
        var id = (await CreateAsync("Box")).GetProperty("id").GetInt64();

        using (var bad = await _admin.PutAsJsonAsync($"/api/backups/targets/{id}/host-key", new { hostKey = "nope" }, Token))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }
        using var response = await _admin.PutAsJsonAsync($"/api/backups/targets/{id}/host-key", new { hostKey = "u1.example.de " + OtherKey }, Token);

        Assert.Equal(OtherKey, (await response.JsonAsync()).GetProperty("hostKey").GetProperty("key").GetString());
        Assert.Contains(("backup.target.host_key_confirmed", "Box"), await AuditAsync());
    }

    [Fact]
    public async Task ADeletedTargetIsGoneWithItsKey()
    {
        var id = (await CreateAsync("Box")).GetProperty("id").GetInt64();

        using var deleted = await _admin.DeleteAsync($"/api/backups/targets/{id}", Token);
        using var again = await _admin.DeleteAsync($"/api/backups/targets/{id}", Token);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(ErrorCodes.BackupTargetNotFound, await again.ErrorCodeAsync());
        await using var connection = await _daemon.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM backup_targets"));
        Assert.Contains(("backup.target.deleted", "Box"), await AuditAsync());
    }

    private async Task<JsonElement> CreateAsync(string name, string path = "")
    {
        using var response = await _admin.PostAsync("/api/backups/targets", new { name, host = "u1.example.de", user = "u1", path, hostKey = GitHubKey });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return await response.JsonAsync();
    }

    private async Task<JsonElement> TestAsync(long id)
    {
        _commands.ClearCalls();
        using var response = await _admin.PostAsync($"/api/backups/targets/{id}/test", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    private async Task<List<(string Action, string Target)>> AuditAsync()
    {
        await using var connection = await _daemon.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token);
        return [.. await connection.QueryAsync<(string, string)>("SELECT action, target FROM audit_log ORDER BY id")];
    }

    private CommandResult? Answer(CommandRequest request)
    {
        switch (request.FileName)
        {
            case "ssh-keygen":
                var key = request.Arguments[request.Arguments.ToList().IndexOf("-f") + 1];
                var token = Guid.NewGuid().ToString("N");
                File.WriteAllText(key, $"PRIVATE-{token}\n");
                File.WriteAllText(key + ".pub", $"ssh-ed25519 PUBLIC-{token} pithyx-backup@test\n");
                return new CommandResult(0, "", "", TimeSpan.Zero);
            case "ssh-keyscan":
                var stdout = request.Arguments[^1] == "nothing.example.de"
                    ? ""
                    : File.ReadAllText(Path.Combine(RepositoryFiles.Root, "daemon", "tests", "fixtures", "backups", "keyscan-localhost.stdout"));
                return new CommandResult(stdout.Length > 0 ? 0 : 1, stdout, "", TimeSpan.Zero);
            case "env":
                return _sftp;
            default:
                return null;
        }
    }
}
