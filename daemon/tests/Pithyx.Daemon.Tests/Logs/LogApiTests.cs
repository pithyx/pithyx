using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Logs;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Support;
using Serilog.Events;
using Serilog.Parsing;

namespace Pithyx.Daemon.Tests.Logs;

public sealed class LogApiTests : IDisposable
{
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _admin;

    public LogApiTests() => _admin = _factory.CreateApiClient();

    public void Dispose()
    {
        _admin.Dispose();
        _factory.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Waits until the writer has flushed what is queued (it writes once a second).</summary>
    private static async Task<System.Text.Json.JsonElement> EntriesAsync(HttpClient client, string query, int atLeast)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var response = await client.GetAsync($"/api/logs?{query}", Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var entries = (await response.JsonAsync()).GetProperty("entries");
            if (entries.GetArrayLength() >= atLeast || attempt > 50)
            {
                return entries;
            }
            await Task.Delay(100, Token);
        }
    }

    [Fact]
    public async Task AppsLogForTheirUserAndAdministratorsReadIt()
    {
        await _admin.SetupAdminAsync();

        using var written = await _admin.PostAsync("/api/apps/org.pithyx.files/logs", new { entries = new[] { new { level = "warn", message = "upload retried", data = new Dictionary<string, string> { ["file"] = "a.jpg" } } } });
        Assert.Equal(1, (await written.JsonAsync()).GetProperty("accepted").GetInt32());

        var entries = await EntriesAsync(_admin, "source=app/org.pithyx.files&level=warn", 1);
        var entry = entries[0];
        Assert.Equal("upload retried", entry.GetProperty("message").GetString());
        Assert.Equal("admin", entry.GetProperty("user").GetString());
        Assert.Equal("a.jpg", entry.GetProperty("data").GetProperty("file").GetString());
        Assert.Contains("app/org.pithyx.files", await _admin.GetFromJsonAsync<string[]>("/api/logs/sources", Token) ?? []);
        var export = await _admin.GetStringAsync("/api/logs/export?format=text&source=app/org.pithyx.files", Token);
        Assert.Contains("WARN  app/org.pithyx.files upload retried", export, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACoreAppsEntriesFromBeforeItsNewIdKeepTheirSourceAndTheFilterFindsBoth()
    {
        await _admin.SetupAdminAsync();
        var store = _factory.Services.GetRequiredService<LogStore>();
        store.Enqueue(new PendingLogEntry(DateTimeOffset.UtcNow.AddMinutes(-5), LogLevelName.Info, "app/files", "written before the rename", null, "admin", null));
        store.Enqueue(new PendingLogEntry(DateTimeOffset.UtcNow.AddMinutes(-5), LogLevelName.Info, "app/com.mueller.files", "another app", null, "admin", null));
        using (var written = await _admin.PostAsync("/api/apps/org.pithyx.files/logs", new { entries = new[] { new { level = "info", message = "written after the rename" } } }))
        {
            Assert.Equal(HttpStatusCode.OK, written.StatusCode);
        }

        var entries = await EntriesAsync(_admin, "source=app/org.pithyx.files", 2);
        Assert.Equal(
            [("app/org.pithyx.files", "written after the rename"), ("app/files", "written before the rename")],
            entries.EnumerateArray().Select(entry => (entry.GetProperty("source").GetString(), entry.GetProperty("message").GetString())));
        var sources = await _admin.GetFromJsonAsync<string[]>("/api/logs/sources", Token) ?? [];
        Assert.Contains("app/org.pithyx.files", sources);
        Assert.DoesNotContain("app/files", sources);
        Assert.Contains("app/com.mueller.files", sources);
        var export = await _admin.GetStringAsync("/api/logs/export?format=text&source=app/org.pithyx.files", Token);
        Assert.Contains("app/files written before the rename", export, StringComparison.Ordinal);
        // The old name alone still finds only the old entries.
        Assert.Equal(1, (await EntriesAsync(_admin, "source=app/files", 1)).GetArrayLength());
    }

    [Fact]
    public async Task ShortAppIdsAreRefused()
    {
        await _admin.SetupAdminAsync();

        using var refused = await _admin.PostAsync("/api/apps/files/logs", new { entries = new[] { new { level = "info", message = "hi" } } });

        Assert.Equal("logs.app.invalid", await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task OnlyAdministratorsReadLogsOrChangeTheirRetention()
    {
        await _admin.SetupAdminAsync();
        var userId = await _factory.InsertUserAsync("rita");
        using var rita = _factory.ClientWithCookie(await _factory.SessionCookieAsync(userId));

        foreach (var path in new[] { "/api/logs", "/api/logs/sources", "/api/logs/settings", "/api/logs/export" })
        {
            using var response = await rita.GetAsync(path, Token);
            Assert.Equal("auth.permission.denied", await response.ErrorCodeAsync());
        }
        // An app writes in the user's name only when the user may open it.
        using (var refused = await rita.PostAsync("/api/apps/org.pithyx.files/logs", new { entries = new[] { new { level = "info", message = "hi" } } }))
        {
            Assert.Equal("auth.permission.denied", await refused.ErrorCodeAsync());
        }
        await using (var connection = await _factory.Services.GetRequiredService<Pithyx.Daemon.Database.SqliteConnectionFactory>().OpenAsync(Token))
        {
            await TestUsers.GrantAsync(connection, userId, "app:org.pithyx.files");
        }
        _factory.Services.GetRequiredService<Pithyx.Daemon.Permissions.PermissionService>().Invalidate();
        using var written = await rita.PostAsync("/api/apps/org.pithyx.files/logs", new { entries = new[] { new { level = "info", message = "hi" } } });
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);
        using var admin = await rita.PostAsync("/api/apps/org.pithyx.storage/logs", new { entries = new[] { new { level = "info", message = "hi" } } });
        Assert.Equal("auth.permission.denied", await admin.ErrorCodeAsync());
    }

    [Fact]
    public async Task BadRequestsAreRefusedWithTheirCodes()
    {
        await _admin.SetupAdminAsync();

        using var tooMany = await _admin.PostAsync("/api/apps/org.pithyx.files/logs", new { entries = Enumerable.Repeat(new { level = "info", message = "x" }, 101) });
        Assert.Equal("logs.batch.too_large", await tooMany.ErrorCodeAsync());
        using var badApp = await _admin.PostAsync("/api/apps/Files!/logs", new { entries = Array.Empty<object>() });
        Assert.Equal("logs.app.invalid", await badApp.ErrorCodeAsync());
        foreach (var query in new[] { "cursor=nope", "limit=0", "limit=1001", "level=loud", "level=2", "from=2026-09-30T12:00:00Z&to=2026-09-30T11:00:00Z" })
        {
            using var response = await _admin.GetAsync($"/api/logs?{query}", Token);
            Assert.Equal("logs.query.invalid", await response.ErrorCodeAsync());
        }
        var settings = LogSettings.Default;
        using var tooBig = await _admin.PutAsJsonAsync("/api/logs/settings", settings with { MaxBytes = LogSettings.MaxMaxBytes + 1 }, Token);
        Assert.Equal("logs.settings.invalid", await tooBig.ErrorCodeAsync());
        using var noPool = await _admin.PutAsJsonAsync("/api/logs/settings", settings with { ArchiveEnabled = true, ArchivePool = "nowhere" }, Token);
        Assert.Equal("logs.archive.pool_unavailable", await noPool.ErrorCodeAsync());
    }

    [Fact]
    public async Task RetentionIsStoredAndReported()
    {
        await _admin.SetupAdminAsync();

        using var saved = await _admin.PutAsJsonAsync("/api/logs/settings", LogSettings.Default with { RetentionDays = 14, MaxBytes = LogSettings.MinMaxBytes }, Token);
        var status = await saved.JsonAsync();

        Assert.Equal(14, status.GetProperty("settings").GetProperty("retentionDays").GetInt32());
        Assert.Equal("off", status.GetProperty("archiveState").GetString());
        var again = await _admin.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/logs/settings", Token);
        Assert.Equal(LogSettings.MinMaxBytes, again.GetProperty("settings").GetProperty("maxBytes").GetInt64());
    }

    [Fact]
    public async Task TheDaemonsOwnEventsArriveWithoutItsRequestLog()
    {
        await _admin.SetupAdminAsync();
        var store = _factory.Services.GetRequiredService<LogStore>();
        var sink = new LogStoreSink(store);
        LogEvent Event(string context, LogEventLevel level, string text) => new(
            DateTimeOffset.UtcNow, level, null, new MessageTemplateParser().Parse(text),
            [new LogEventProperty("SourceContext", new ScalarValue(context)), new LogEventProperty("password", new ScalarValue("***"))]);

        sink.Emit(Event(LogStoreSink.RequestLogging, LogEventLevel.Information, "HTTP GET /api/logs responded 200"));
        sink.Emit(Event(LogStoreSink.RequestLogging, LogEventLevel.Warning, "HTTP GET /api/x responded 500 slowly"));
        sink.Emit(Event("Pithyx.Test", LogEventLevel.Error, "marker entry"));

        var entries = await EntriesAsync(_admin, "source=daemon&search=responded", 1);
        Assert.Single(entries.EnumerateArray());
        var marker = (await EntriesAsync(_admin, "source=daemon&level=error&search=marker", 1))[0];
        Assert.Equal("***", marker.GetProperty("data").GetProperty("password").GetString());
    }
}
