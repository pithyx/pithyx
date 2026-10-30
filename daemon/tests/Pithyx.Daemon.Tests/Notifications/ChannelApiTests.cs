using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Notifications;
using Pithyx.Daemon.Tests.Apps.Packages;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;
using static Pithyx.Daemon.Tests.Jobs.JobTestSupport;

namespace Pithyx.Daemon.Tests.Notifications;

/// <summary>
/// Email and webhook channels end to end (D379): a webhook listener and a small SMTP server on loopback take what the
/// box sends for the users' channels and for its own.
/// </summary>
public sealed class ChannelApiTests : IAsyncLifetime
{
    private readonly ChannelDaemon _factory = new();
    private readonly WebhookListener _hook = new();
    private NotificationService _notifications = null!;
    private long _adminId;
    private long _bobId;
    private HttpClient _admin = null!;
    private HttpClient _bob = null!;
    private HttpClient _carol = null!;

    public async ValueTask InitializeAsync()
    {
        _notifications = _factory.Services.GetRequiredService<NotificationService>();
        _adminId = await _factory.InsertUserAsync("admin", admin: true);
        await _factory.InsertUserAsync("ann", admin: true);
        _bobId = await _factory.InsertUserAsync("bob");
        var carolId = await _factory.InsertUserAsync("carol");
        _admin = _factory.ClientWithCookie(await _factory.SessionCookieAsync(_adminId));
        _bob = _factory.ClientWithCookie(await _factory.SessionCookieAsync(_bobId));
        _carol = _factory.ClientWithCookie(await _factory.SessionCookieAsync(carolId));
    }

    public async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        _bob.Dispose();
        _carol.Dispose();
        await _factory.DisposeAsync();
        _hook.Dispose();
    }

    private static readonly object _mail = new { name = "Mail", kind = "email", recipients = new[] { "bob@example.com" }, categories = new[] { "storage" } };

    private static object Hook(string url, string[] categories, string level = "warning", string format = "json", object[]? headers = null) =>
        new { name = "Phone", kind = "webhook", url, format, headers = headers ?? [], categories, minLevel = level };

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call)
    {
        using var response = await call;
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
        return await response.JsonAsync();
    }

    [Fact]
    public async Task AUsersWebhookGetsTheirNotificationsInTheirLanguage()
    {
        using (var language = await _bob.PutAsJsonAsync("/api/notifications/language", new { language = "de" }, Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, language.StatusCode);
        }
        var channel = await OkAsync(_bob.PostAsJsonAsync("/api/notifications/channels", Hook(_hook.Url, ["storage"], headers: [new { name = "X-Token", value = "secret" }]), Token));
        Assert.Equal(["X-Token"], channel.GetProperty("headers").EnumerateArray().Select(header => header.GetString()));

        await _notifications.CreateAsync(_bobId, new NotificationDraft("update.available", new Dictionary<string, object?> { ["version"] = "1.2.0", ["installed"] = "1.1.0" }, NotificationTone.Info), Token);
        await _notifications.CreateAsync(_bobId, new NotificationDraft("storage.alert.poolDegraded", new Dictionary<string, object?> { ["pool"] = "tank" }, NotificationTone.Critical), Token);

        var request = await _hook.NextAsync();
        Assert.Equal("secret", request.Headers["X-Token"]);
        var body = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal("Pool tank ist beeinträchtigt", body.GetProperty("title").GetString());
        Assert.Equal(("error", "storage", 8), (body.GetProperty("level").GetString(), body.GetProperty("category").GetString(), body.GetProperty("priority").GetInt32()));
        Assert.Equal("tank", body.GetProperty("params").GetProperty("pool").GetString());
        Assert.True(_hook.IsEmpty, "The update is not in the channel's categories.");

        // The channel notes the delivery right after the webhook answered.
        await WaitUntilAsync(
            async () => (await OkAsync(_bob.GetAsync("/api/notifications/channels", Token))).GetProperty("items")[0].GetProperty("lastSentAt").ValueKind != JsonValueKind.Null,
            "the channel to note its delivery");
        Assert.Equal(0, (await OkAsync(_carol.GetAsync("/api/notifications/channels", Token))).GetProperty("items").GetArrayLength());
        using var foreign = await _carol.DeleteAsync($"/api/notifications/channels/{channel.GetProperty("id").GetInt64()}", Token);
        Assert.Equal((HttpStatusCode.NotFound, "notifications.channel.not_found"), (foreign.StatusCode, await foreign.ErrorCodeAsync()));
    }

    [Fact]
    public async Task TheBoxsChannelsGetASystemAlertOnceAndAnAppsTextNeedsThePermission()
    {
        using (var refused = await _bob.GetAsync("/api/system/notifications/channels", Token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }
        await OkAsync(_admin.PostAsJsonAsync("/api/system/notifications/channels", Hook(_hook.Url, ["system", "apps"], "info", "ntfy"), Token));

        await _notifications.CreateForAdministratorsAsync(new NotificationDraft("system.ups.on_battery", new Dictionary<string, object?> { ["charge"] = 80, ["minutes"] = 20 }, NotificationTone.Warning), Token);
        await _notifications.CreateAsync(_adminId, new NotificationDraft(NotificationService.AppMessageCode, new Dictionary<string, object?> { ["title"] = "Hi", ["body"] = "" }, NotificationTone.Info, "com.example.chat"), Token);

        var request = await _hook.NextAsync();
        Assert.Equal("The UPS runs on battery", request.Headers["Title"]);
        Assert.Equal("4", request.Headers["Priority"]);
        Assert.StartsWith("The power failed; the battery is at 80 %.", request.Body, StringComparison.Ordinal);
        await Task.Delay(300, Token);
        Assert.True(_hook.IsEmpty, "One alert for two administrators, and no app text without notifications.external.");
    }

    [Fact]
    public async Task AMutedAppCategoryReachesNoChannel()
    {
        const string appId = "com.example.alpha";
        var manifest = TarBuilder.Manifest(appId, "1.0.0", "\"notifications\", \"notifications.external\"",
            ",\n  \"notifications\": { \"categories\": [{ \"id\": \"messages\", \"name\": { \"en\": \"Messages\" } }, { \"id\": \"mentions\", \"name\": { \"en\": \"Mentions\" } }] }");
        using (var upload = await _admin.PutAsync("/api/apps/packages", new ByteArrayContent(TarBuilder.MinimalPackage(appId, "1.0.0", manifest).End().Gzip()), Token))
        {
            var packageId = (await upload.JsonAsync()).GetProperty("packageId").GetString();
            using var install = await _admin.PostAsJsonAsync($"/api/apps/packages/{packageId}/install", new { pool = "pithyxtestm" }, Token);
            Assert.Equal(HttpStatusCode.Accepted, install.StatusCode);
            await WaitUntilAsync(async () => (await OkAsync(_admin.GetAsync("/api/apps", Token))).GetProperty("apps").EnumerateArray().Any(app => app.GetProperty("id").GetString() == appId), "the install");
        }
        await OkAsync(_admin.PostAsJsonAsync("/api/notifications/channels", Hook(_hook.Url, ["apps"], "info"), Token));
        await _notifications.SetMutedAsync(_adminId, appId, "mentions", true, Token);

        string[] categories = ["messages", "mentions"];
        await _notifications.CreateForAppAsync(_adminId, appId, new AppNotifyRequest("You were mentioned", Category: "mentions"), categories, Token);
        await _notifications.CreateForAppAsync(_adminId, appId, new AppNotifyRequest("New message", Category: "messages"), categories, Token);

        // The install's own notification may reach the channel too, before or after the others (deliveries run apart);
        // the muted one never does, so everything that arrives in a while after the unmuted one is checked.
        var bodies = new List<string>();
        while (!bodies.Any(body => body.Contains("New message", StringComparison.Ordinal)))
        {
            bodies.Add((await _hook.NextAsync()).Body);
        }
        await Task.Delay(300, Token);
        bodies.AddRange(_hook.Drain().Select(request => request.Body));
        Assert.DoesNotContain(bodies, body => body.Contains("mentioned", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATestReportsWhatTheServerSaidAndKeepsIt()
    {
        _hook.Status = HttpStatusCode.Forbidden;
        var channel = await OkAsync(_bob.PostAsJsonAsync("/api/notifications/channels", Hook(_hook.Url, ["storage"]), Token));
        var id = channel.GetProperty("id").GetInt64();

        using var failed = await _bob.PostAsync($"/api/notifications/channels/{id}/test", null, Token);
        var problem = await failed.JsonAsync();
        var body = (await _hook.NextAsync()).Body;

        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Equal("notifications.channel.failed", problem.GetProperty("code").GetString());
        Assert.Equal("HTTP 403", problem.GetProperty("params").GetProperty("detail").GetString());
        Assert.Contains("Phone", body, StringComparison.Ordinal);
        var kept = (await OkAsync(_bob.GetAsync("/api/notifications/channels", Token))).GetProperty("items")[0];
        Assert.Equal("notifications.channel.failed", kept.GetProperty("errorCode").GetString());

        _hook.Status = HttpStatusCode.OK;
        var fixedChannel = await OkAsync(_bob.PostAsync($"/api/notifications/channels/{id}/test", null, Token));
        Assert.Equal(JsonValueKind.Null, fixedChannel.GetProperty("errorCode").ValueKind);
    }

    [Fact]
    public async Task EmailGoesThroughTheBoxsSmtpServer()
    {
        using (var missing = await _bob.PostAsJsonAsync("/api/notifications/channels", _mail, Token))
        {
            Assert.Equal((HttpStatusCode.Conflict, "notifications.smtp.missing"), (missing.StatusCode, await missing.ErrorCodeAsync()));
        }
        using var smtp = new SmtpListener();
        var saved = await OkAsync(_admin.PutAsJsonAsync("/api/system/notifications/smtp", new { host = "127.0.0.1", port = smtp.Port, security = "none", username = "", password = "pw", from = "nas@example.com" }, Token));
        Assert.Equal((true, true), (saved.GetProperty("configured").GetBoolean(), saved.GetProperty("passwordSet").GetBoolean()));
        Assert.False(saved.TryGetProperty("password", out _));
        using (var invalid = await _admin.PutAsJsonAsync("/api/system/notifications/smtp", new { host = "127.0.0.1", port = smtp.Port, security = "none", from = "nas" }, Token))
        {
            Assert.Equal("notifications.smtp.invalid", await invalid.ErrorCodeAsync());
        }

        var channel = await OkAsync(_bob.PostAsJsonAsync("/api/notifications/channels", _mail, Token));
        await OkAsync(_bob.PostAsync($"/api/notifications/channels/{channel.GetProperty("id").GetInt64()}/test", null, Token));

        var mail = await smtp.NextAsync();
        Assert.Contains("RCPT TO:<bob@example.com>", mail.Commands);
        Assert.Contains("Subject: Test notification", mail.Data, StringComparison.Ordinal);
        Assert.Contains("If you read this, the channel Mail works.", mail.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSmtpServerCanBeTriedBeforeItIsSaved()
    {
        using var smtp = new SmtpListener();
        var server = new { host = "127.0.0.1", port = smtp.Port, security = "none", from = "nas@example.com" };
        using (var noAddress = await _admin.PostAsJsonAsync("/api/system/notifications/smtp/test", new { to = "nobody", server.host, server.port, server.security, server.from }, Token))
        {
            Assert.Equal((HttpStatusCode.BadRequest, "notifications.smtp.invalid"), (noAddress.StatusCode, await noAddress.ErrorCodeAsync()));
        }
        using (var notAdmin = await _bob.PostAsJsonAsync("/api/system/notifications/smtp/test", new { to = "bob@example.com", server.host, server.port, server.security, server.from }, Token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, notAdmin.StatusCode);
        }

        using (var sent = await _admin.PostAsJsonAsync("/api/system/notifications/smtp/test", new { to = "admin@example.com", server.host, server.port, server.security, server.from }, Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, sent.StatusCode);
        }

        var mail = await smtp.NextAsync();
        Assert.Contains("RCPT TO:<admin@example.com>", mail.Commands);
        Assert.Contains("Subject: Test email", mail.Data, StringComparison.Ordinal);
        Assert.Contains("If you read this, the box can send email through 127.0.0.1.", mail.Data, StringComparison.Ordinal);
        // Trying the server stores nothing.
        var status = await OkAsync(_admin.GetAsync("/api/system/notifications/smtp", Token));
        Assert.False(status.GetProperty("configured").GetBoolean());
    }

    /// <summary>The daemon with webhooks allowed to reach loopback, where the test listeners are.</summary>
    private sealed class ChannelDaemon : DaemonFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton(new NotificationDeliveryOptions(AllowLoopback: true)));
        }
    }

    private sealed record HookRequest(Dictionary<string, string> Headers, string Body);

    private sealed class WebhookListener : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly ConcurrentQueue<HookRequest> _requests = new();
        private readonly SemaphoreSlim _arrived = new(0);

        public WebhookListener()
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}/hook";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string Url { get; }

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public bool IsEmpty => _requests.IsEmpty;

        /// <summary>Takes every request that arrived and was not read yet.</summary>
        public List<HookRequest> Drain()
        {
            var drained = new List<HookRequest>();
            while (_requests.TryDequeue(out var request))
            {
                drained.Add(request);
            }
            return drained;
        }

        public async Task<HookRequest> NextAsync()
        {
            Assert.True(await _arrived.WaitAsync(TimeSpan.FromSeconds(10), Token), "The webhook got nothing.");
            Assert.True(_requests.TryDequeue(out var request));
            return request;
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                var headers = context.Request.Headers.AllKeys.Where(key => key is not null).ToDictionary(key => key!, key => context.Request.Headers[key]!, StringComparer.OrdinalIgnoreCase);
                _requests.Enqueue(new HookRequest(headers, body));
                context.Response.StatusCode = (int)Status;
                context.Response.Close();
                _arrived.Release();
            }
        }

        public void Dispose()
        {
            _listener.Close();
            _arrived.Dispose();
        }
    }

    private sealed record Mail(List<string> Commands, string Data);

    /// <summary>Just enough SMTP for one client: no TLS, no sign-in, every mail accepted.</summary>
    private sealed class SmtpListener : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly ConcurrentQueue<Mail> _mails = new();
        private readonly SemaphoreSlim _arrived = new(0);

        public SmtpListener()
        {
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public async Task<Mail> NextAsync()
        {
            Assert.True(await _arrived.WaitAsync(TimeSpan.FromSeconds(10), Token), "The SMTP server got nothing.");
            Assert.True(_mails.TryDequeue(out var mail));
            return mail;
        }

        private async Task ServeAsync()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
                {
                    return;
                }
                using (client)
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII);
                    await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                    await writer.WriteLineAsync("220 test ESMTP");
                    var commands = new List<string>();
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        commands.Add(line);
                        if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync("250 test");
                        }
                        else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync("354 go on");
                            var data = new StringBuilder();
                            while (await reader.ReadLineAsync() is { } dataLine && dataLine != ".")
                            {
                                data.AppendLine(dataLine);
                            }
                            await writer.WriteLineAsync("250 queued");
                            _mails.Enqueue(new Mail(commands, data.ToString()));
                            _arrived.Release();
                        }
                        else if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync("221 bye");
                            break;
                        }
                        else
                        {
                            await writer.WriteLineAsync("250 ok");
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _arrived.Dispose();
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
