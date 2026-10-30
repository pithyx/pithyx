using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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

    /// <summary>
    /// Waits until the writer has flushed what is queued. It writes once a second, but on a loaded runner the batches of
    /// the daemon's own start can hold it up for seconds, so the wait is bounded by time, not by a count of attempts.
    /// </summary>
    private static async Task<System.Text.Json.JsonElement> EntriesAsync(HttpClient client, string query, int atLeast)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            using var response = await client.GetAsync($"/api/logs?{query}", Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var entries = (await response.JsonAsync()).GetProperty("entries");
            if (entries.GetArrayLength() >= atLeast || waited.Elapsed > TimeSpan.FromSeconds(30))
            {
                return entries;
            }
            await Task.Delay(100, Token);
        }
    }

    [Fact]
    public async Task CountsHaveTheWarningsAndErrorsOfEachSourceInTheRange()
    {
        await _admin.SetupAdminAsync();
        var store = _factory.Services.GetRequiredService<LogStore>();
        var now = DateTimeOffset.UtcNow;
        store.Enqueue(new PendingLogEntry(now.AddMinutes(-5), LogLevelName.Warn, "app/files", "before the rename"));
        store.Enqueue(new PendingLogEntry(now.AddMinutes(-4), LogLevelName.Error, "app/org.pithyx.files", "after the rename"));
        store.Enqueue(new PendingLogEntry(now.AddMinutes(-3), LogLevelName.Error, "system/smbd", "smbd failed"));
        store.Enqueue(new PendingLogEntry(now.AddMinutes(-3), LogLevelName.Info, "system/smbd", "smbd started"));
        store.Enqueue(new PendingLogEntry(now.AddDays(-2), LogLevelName.Error, "system/sshd", "too old"));
        await EntriesAsync(_admin, "source=system/smbd", 2);

        var from = Uri.EscapeDataString(now.AddHours(-1).ToString("O"));
        var counts = await _admin.GetFromJsonAsync<JsonElement>($"/api/logs/counts?from={from}", Token);

        var bySource = counts.EnumerateArray().ToDictionary(
            count => count.GetProperty("source").GetString()!,
            count => (count.GetProperty("warnings").GetInt64(), count.GetProperty("errors").GetInt64()));
        Assert.Equal((1L, 1L), bySource["app/org.pithyx.files"]);
        Assert.Equal((0L, 1L), bySource["system/smbd"]);
        Assert.False(bySource.ContainsKey("app/files"));
        Assert.False(bySource.ContainsKey("system/sshd"));
    }

    [Fact]
    public async Task TheTopicCarriesNewEntriesToAdministratorsOnly()
    {
        using var setup = await _admin.PostAsync("/api/setup", new { username = "admin", password = AuthApi.AdminPassword });
        var cookie = setup.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("pithyx_session=", StringComparison.Ordinal)).Split(';')[0];
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers.Cookie = cookie;
            request.Headers.Origin = "http://localhost";
        };
        using var socket = await client.ConnectAsync(new Uri("ws://localhost/ws"), Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "sub", id = "l1", topic = LogWriter.Topic })), WebSocketMessageType.Text, endOfMessage: true, Token);
        Assert.Equal("ack", (await ReceiveAsync(socket)).GetProperty("type").GetString());
        var snapshot = await ReceiveAsync(socket);
        Assert.Equal(0, snapshot.GetProperty("data").GetProperty("entries").GetArrayLength());

        _factory.Services.GetRequiredService<LogStore>().Enqueue(new PendingLogEntry(DateTimeOffset.UtcNow, LogLevelName.Warn, "system/smbd", "live line"));

        // The daemon logs on its own as well; wait for the event with the line.
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var message = await ReceiveAsync(socket);
            var entries = message.GetProperty("data").GetProperty("entries").EnumerateArray().ToList();
            var line = entries.FirstOrDefault(entry => entry.GetProperty("message").GetString() == "live line");
            if (line.ValueKind == JsonValueKind.Object)
            {
                Assert.Equal("event", message.GetProperty("type").GetString());
                Assert.True(line.GetProperty("id").GetInt64() > 0);
                Assert.Equal("warn", line.GetProperty("level").GetString());
                break;
            }
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(30), "the line never came");
        }

        // Only administrators may follow it.
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "rita")], "test"));
        Assert.False(LogWriter.Definition.IsAllowed(user, LogWriter.Topic));
    }

    private static async Task<JsonElement> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[1024 * 1024];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement.Clone();
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
        using var admin = await rita.PostAsync("/api/apps/org.pithyx.settings/logs", new { entries = new[] { new { level = "info", message = "hi" } } });
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
