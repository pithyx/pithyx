using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.SupportBundles;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.SupportBundles;

/// <summary>The support bundle (D412): what of the database goes in, and that no secret ever does.</summary>
public sealed class SupportBundleTests : IAsyncLifetime
{
    private const string Plant = "PLANTED";
    private static readonly string[] _everyPartButTheDump = ["system", "storage", "network", "configuration", "logs", "apps"];
    private static readonly string[] _configuration = ["configuration"];
    private static readonly string[] _memoryDump = ["memoryDump"];
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

    [Fact]
    public async Task EveryTableAndColumnOfTheSchemaIsClassified()
    {
        await using var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token);
        var tables = await SupportDatabase.TablesAsync(connection, null, Token);

        foreach (var table in tables)
        {
            Assert.True(SupportDatabase.Included.ContainsKey(table) ^ SupportDatabase.Excluded.Contains(table), $"Decide whether the table {table} goes into a support bundle.");
        }
        Assert.Empty(SupportDatabase.Included.Keys.Concat(SupportDatabase.Excluded).Except(tables));
        foreach (var (table, omitted) in SupportDatabase.Included)
        {
            Assert.Empty(omitted.Except(await SupportDatabase.ColumnsAsync(connection, table, null, Token)));
        }
    }

    [Fact]
    public async Task NoSecretReachesTheBundleWhileNamesStayReadable()
    {
        await using (var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token))
        {
            var anna = await TestUsers.InsertAsync(connection, "anna", hash: $"hash-{Plant}");
            await connection.ExecuteAsync("UPDATE users SET totp_secret = @secret WHERE id = @anna", new { secret = $"totp-{Plant}", anna });
            await connection.ExecuteAsync(
                "INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)",
                new[]
                {
                    new { key = "notifications.smtp", value = $$"""{"host":"mail.example.com","port":587,"username":"box","password":"smtp-{{Plant}}"}""" },
                    new { key = "ups.settings", value = $$"""{"mode":"netClient","host":"ups.lan","Password":"ups-{{Plant}}"}""" },
                    new { key = "updates.source", value = $$"""{"repository":"me/fork","token":"source-{{Plant}}"}""" },
                    new { key = "some.future.key", value = $"future-{Plant}" },
                });
            await connection.ExecuteAsync(
                """
                INSERT INTO catalogs (kind, url, ssh_private_key, registry_token, added_at)
                VALUES ('custom', @url, @key, @token, '2026-01-01T00:00:00Z')
                """,
                new { url = $"https://bob:url-{Plant}@git.example.com/catalog.git", key = $"ssh-{Plant}", token = $"registry-{Plant}" });
            await connection.ExecuteAsync(
                """
                INSERT INTO notification_channels (user_id, name, kind, enabled, config, categories, min_level, created_at, updated_at)
                VALUES (@anna, 'Hook', 'webhook', 1, @config, '[]', 'info', 'x', 'x')
                """,
                new { anna, config = $$"""{"url":"https://hooks.example.com/{{Plant}}"}""" });
            await connection.ExecuteAsync(
                "INSERT INTO app_storage (app_id, user_id, key, value, updated_at) VALUES ('com.example.notes', @anna, 'note', @value, 'x')",
                new { anna, value = $"\"diary-{Plant}\"" });
        }

        var entries = await DownloadAsync(await CreateAsync(_everyPartButTheDump));

        foreach (var (name, content) in entries)
        {
            Assert.DoesNotContain(Plant, content, StringComparison.Ordinal);
            Assert.DoesNotContain("$argon2", content, StringComparison.Ordinal);
        }
        Assert.Contains("\"anna\"", entries["configuration/database/users.json"], StringComparison.Ordinal);
        Assert.Contains("mail.example.com", entries["configuration/database/settings.json"], StringComparison.Ordinal);
        Assert.Contains("git.example.com", entries["configuration/database/catalogs.json"], StringComparison.Ordinal);
        Assert.False(entries.ContainsKey("configuration/database/app_storage.json"));
        Assert.False(entries.ContainsKey("configuration/database/sessions.json"));
        Assert.Contains("manifest.json", entries.Keys);
        Assert.Contains("failures.json", entries.Keys);
        Assert.Contains("logs/log-center.jsonl", entries.Keys);
    }

    [Fact]
    public async Task ANewBundleReplacesTheOldOneWhichExpiresAfterAnHour()
    {
        var first = await CreateAsync(_configuration);
        var second = await CreateAsync(_configuration);
        Assert.NotEqual(first, second);

        using (var gone = await _admin.GetAsync($"/api/system/support-bundle/{first}/download", Token))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
            Assert.Equal("system.support.not_found", await gone.ErrorCodeAsync());
        }

        _factory.Time.Advance(SupportBundleService.Lifetime + TimeSpan.FromMinutes(1));
        using var state = await _admin.GetAsync("/api/system/support-bundle", Token);
        Assert.Equal(JsonValueKind.Null, (await state.JsonAsync()).GetProperty("bundle").ValueKind);
        using var expired = await _admin.GetAsync($"/api/system/support-bundle/{second}/download", Token);
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
    }

    [Fact]
    public async Task AMemoryDumpNeedsAStepUpAndABundleAtLeastOnePart()
    {
        using (var dump = await _admin.PostAsync("/api/system/support-bundle", new { parts = _memoryDump }))
        {
            Assert.Equal("auth.step_up.required", await dump.ErrorCodeAsync());
        }
        using var empty = await _admin.PostAsync("/api/system/support-bundle", new { parts = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task TheBundleIsDeletedOnRequest()
    {
        var id = await CreateAsync(_configuration);

        using (var delete = await _admin.DeleteAsync($"/api/system/support-bundle/{id}", Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }
        using var gone = await _admin.GetAsync($"/api/system/support-bundle/{id}/download", Token);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(2, "standby")]
    [InlineData(4, "exit")]
    [InlineData(1, "exit")]
    [InlineData(64, null)]
    [InlineData(8 | 16 | 128, null)]
    [InlineData(4 | 64, "exit")]
    public void OnlySmartctlsBitsForAnUnreadDiskCountAsAFailure(int exitCode, string? reason) =>
        Assert.Equal(reason, SupportBundleService.SmartctlFailure(exitCode));

    [Theory]
    [InlineData("    wpa-psk hunter2\n", "    wpa-psk (omitted)\n")]
    [InlineData("PrivateKey = abc=\n", "PrivateKey = (omitted)\n")]
    [InlineData("PresharedKey=abc\n", "PresharedKey=(omitted)\n")]
    [InlineData("  \"token\": \"abc\",\n", "  \"token\": (omitted)\n")]
    [InlineData("url = https://bob:hunter2@example.com/x\n", "url = https://(omitted)@example.com/x\n")]
    [InlineData("   workgroup = WORKGROUP\n", "   workgroup = WORKGROUP\n")]
    [InlineData("   passdb backend = tdbsam\n", "   passdb backend = tdbsam\n")]
    public void ConfigurationFilesLoseTheirSecrets(string text, string expected) =>
        Assert.Equal(expected, SupportBundleService.ScrubText(text));

    [Fact]
    public void JsonValuesLoseSecretPropertiesAtEveryDepth()
    {
        var scrubbed = (string)SupportDatabase.Scrub("""{"host":"h","auth":{"Password":"x","user":"u"},"list":[{"apiToken":"t"}]}""")!;

        Assert.Equal("""{"host":"h","auth":{"user":"u"},"list":[{}]}""", scrubbed);
    }

    private async Task<string> CreateAsync(string[] parts)
    {
        using var response = await _admin.PostAsync("/api/system/support-bundle", new { parts });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var jobId = (await response.JsonAsync()).GetProperty("id").GetInt64();
        var job = await _factory.Services.GetRequiredService<JobService>().WaitForStateAsync(jobId, JobState.Succeeded);
        return job.Result!.Value.GetProperty("id").GetString()!;
    }

    private async Task<Dictionary<string, string>> DownloadAsync(string id)
    {
        using var response = await _admin.GetAsync($"/api/system/support-bundle/{id}/download", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync(Token)), ZipArchiveMode.Read);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            entries[entry.FullName] = await reader.ReadToEndAsync(Token);
        }
        return entries;
    }
}
