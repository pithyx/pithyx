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

        using var written = await _admin.PostAsync("/api/apps/files/logs", new { entries = new[] { new { level = "warn", message = "upload retried", data = new Dictionary<string, string> { ["file"] = "a.jpg" } } } });
        Assert.Equal(1, (await written.JsonAsync()).GetProperty("accepted").GetInt32());

        var entries = await EntriesAsync(_admin, "source=app/files&level=warn", 1);
        var entry = entries[0];
        Assert.Equal("upload retried", entry.GetProperty("message").GetString());
        Assert.Equal("admin", entry.GetProperty("user").GetString());
        Assert.Equal("a.jpg", entry.GetProperty("data").GetProperty("file").GetString());
        Assert.Contains("app/files", await _admin.GetFromJsonAsync<string[]>("/api/logs/sources", Token) ?? []);
        var export = await _admin.GetStringAsync("/api/logs/export?format=text&source=app/files", Token);
        Assert.Contains("WARN  app/files upload retried", export, StringComparison.Ordinal);
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
        using var written = await rita.PostAsync("/api/apps/files/logs", new { entries = new[] { new { level = "info", message = "hi" } } });
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);
    }

    [Fact]
    public async Task BadRequestsAreRefusedWithTheirCodes()
    {
        await _admin.SetupAdminAsync();

        using var tooMany = await _admin.PostAsync("/api/apps/files/logs", new { entries = Enumerable.Repeat(new { level = "info", message = "x" }, 101) });
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
