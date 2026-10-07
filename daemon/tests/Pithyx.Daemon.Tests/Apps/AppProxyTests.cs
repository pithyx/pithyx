using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Apps;
using Pithyx.Daemon.Apps.Proxy;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Tests.Apps.Packages;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Apps;

/// <summary>
/// The proxy to app backends (A13, D303, D316, D323) with the daemon on Kestrel and two Kestrel backends that answer with
/// what they got: tokens and tickets, the headers in both directions, CORS, the errors, and that sockets close when what
/// opened them ends.
/// </summary>
public sealed class AppProxyTests : IAsyncLifetime
{
    private const string AppA = "com.example.alpha";
    private const string AppB = "com.example.beta";
    private const string Pool = "pithyxtestm";
    private const string Backend = """{"compose":"backend/compose.yaml","endpoints":{"api":{"service":"web","port":8080},"metrics":{"service":"web","port":9090}},"lifecycle":"onDemand"}""";

    private readonly KestrelDaemonFactory _factory = new();
    private readonly List<HttpClient> _clients = [];
    private readonly List<WebApplication> _backends = [];
    private HttpClient _admin = null!;
    private Uri _api = null!;
    private Uri _metrics = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private AppBackends Backends => _factory.Services.GetRequiredService<AppBackends>();

    public async ValueTask InitializeAsync()
    {
        _factory.UseKestrel();
        _factory.StartServer();
        _admin = Client(cookies: true);
        _admin.DefaultRequestHeaders.Add("X-Pithyx-Request", "1");
        await _admin.SetupAdminAsync();
        _api = await StartBackendAsync("api");
        _metrics = await StartBackendAsync("metrics");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }
        foreach (var backend in _backends)
        {
            await backend.DisposeAsync();
        }
        await _factory.DisposeAsync();
    }

    /// <summary>A backend that echoes what it got as JSON, sets a cookie, and echoes WebSocket messages.</summary>
    private async Task<Uri> StartBackendAsync(string name)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var backend = builder.Build();
        backend.UseWebSockets();
        backend.Run(async context =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var greeting = Encoding.UTF8.GetBytes($"{name} {context.Request.Headers["X-Pithyx-User"]} {context.Request.Path}{context.Request.QueryString}");
                await socket.SendAsync(greeting, WebSocketMessageType.Text, true, CancellationToken.None);
                var buffer = new byte[1024];
                while (true)
                {
                    var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        return;
                    }
                    await socket.SendAsync(buffer.AsMemory(0, received.Count), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            context.Response.Headers.SetCookie = "session=evil; Path=/";
            context.Response.Headers.ContentSecurityPolicy = "default-src *";
            context.Response.Headers["Clear-Site-Data"] = "\"cookies\", \"storage\"";
            context.Response.Headers.StrictTransportSecurity = "max-age=31536000";
            context.Response.Headers.AccessControlAllowCredentials = "true";
            context.Response.Headers["X-Backend"] = name;
            await context.Response.WriteAsJsonAsync(new
            {
                backend = name,
                method = context.Request.Method,
                path = context.Request.Path.Value,
                query = context.Request.QueryString.Value,
                headers = context.Request.Headers.ToDictionary(header => header.Key.ToLowerInvariant(), header => header.Value.ToString()),
            });
        });
        await backend.StartAsync(Token);
        _backends.Add(backend);
        return new Uri(backend.Urls.First() + "/");
    }

    private async Task InstallAsync(string id, string access = "everybody")
    {
        var package = TarBuilder.MinimalPackage(id, "1.0.0", TarBuilder.Manifest(id, "1.0.0", "", "")).End().Gzip();
        using var upload = await _admin.PutAsync("/api/apps/packages", new ByteArrayContent(package), Token);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var packageId = (await upload.JsonAsync()).GetProperty("packageId").GetString();
        using var install = await _admin.PostAsJsonAsync($"/api/apps/packages/{packageId}/install", new { pool = Pool, access }, Token);
        Assert.Equal(HttpStatusCode.Accepted, install.StatusCode);
        await FinishedAsync(await install.JsonAsync());
        // A10 installs frontends only; the backend comes into the manifest by hand until the Docker runtime.
        await _factory.ScalarAsync<long>("UPDATE apps SET manifest = json_set(manifest, '$.backend', json(@Backend)) WHERE id = @id RETURNING 1", new { Backend, id });
        await _factory.Services.GetRequiredService<AppRegistry>().ReloadAsync(Token);
    }

    private async Task FinishedAsync(JsonElement started)
    {
        var id = started.GetProperty("id").GetInt64();
        Job? job = null;
        var jobs = _factory.Services.GetRequiredService<JobService>();
        await JobTestSupport.WaitUntilAsync(async () => (job = await jobs.GetAsync(JobTestSupport.Principal(0, admin: true), id, Token)).State is not (JobState.Queued or JobState.Running), $"job {id}");
        Assert.Equal(JobState.Succeeded, job!.State);
    }

    private void Run(string appId) => Backends.SetRunning(appId, new Dictionary<string, Uri> { ["api"] = _api, ["metrics"] = _metrics });

    private async Task<(HttpClient Client, long Id)> UserAsync(string username)
    {
        long id;
        await using (var connection = await _factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token))
        {
            id = await TestUsers.InsertAsync(connection, username);
        }
        _factory.Services.GetRequiredService<Pithyx.Daemon.Permissions.PermissionService>().Invalidate();
        return (await SessionAsync(id), id);
    }

    private static async Task<string> TokenAsync(HttpClient session, string appId, string kind = "backend")
    {
        using var response = await session.PostAsJsonAsync($"/api/apps/{appId}/tokens", new { kind, instance = "w1" }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("token").GetString()!;
    }

    private async Task<string> TicketAsync(HttpClient session, string appId, string endpoint = "api")
    {
        var host = Bare();
        host.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(session, appId, "host"));
        host.DefaultRequestHeaders.Add("X-Pithyx-Request", "1");
        using var response = await host.PostAsJsonAsync("/api/app/tickets", new { endpoint }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("ticket").GetString()!;
    }

    /// <summary>A client without cookies, like a sandboxed window.</summary>
    private HttpClient Bare() => Client(cookies: false);

    /// <summary>A client of the daemon on loopback (the factory's own clients take the address it listens on, [::]).</summary>
    private HttpClient Client(bool cookies)
    {
        var client = new HttpClient(new SocketsHttpHandler { UseCookies = cookies }) { BaseAddress = _factory.Address };
        _clients.Add(client);
        return client;
    }

    /// <summary>A client with a new session of the user and the CSRF header, like the desktop.</summary>
    private async Task<HttpClient> SessionAsync(long userId)
    {
        var client = Client(cookies: false);
        client.DefaultRequestHeaders.Add("Cookie", await _factory.SessionCookieAsync(userId));
        client.DefaultRequestHeaders.Add("X-Pithyx-Request", "1");
        return client;
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response) =>
        response.IsSuccessStatusCode ? null : await response.ErrorCodeAsync();

    private async Task<HttpResponseMessage> GetAsync(string path, string? bearer = null, Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        configure?.Invoke(request);
        return await Bare().SendAsync(request, Token);
    }

    private async Task<ClientWebSocket> ConnectAsync(string path)
    {
        var socket = new ClientWebSocket();
        var address = new UriBuilder(_factory.Address) { Scheme = "ws" };
        await socket.ConnectAsync(new Uri(address.Uri, path), Token);
        return socket;
    }

    private static async Task<string?> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[1024];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var received = await socket.ReceiveAsync(buffer, timeout.Token);
            return received.MessageType == WebSocketMessageType.Close ? null : Encoding.UTF8.GetString(buffer, 0, received.Count);
        }
        catch (WebSocketException)
        {
            return null;
        }
    }

    private static async Task<int> RefusedUpgradeAsync(Uri baseAddress, string path)
    {
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        var address = new UriBuilder(baseAddress) { Scheme = "ws" };
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(new Uri(address.Uri, path), Token));
        return (int)socket.HttpStatusCode;
    }

    [Fact]
    public async Task PreflightsFromASandboxedWindowAreAnsweredHereAndOthersRefused()
    {
        await InstallAsync(AppA);
        using var preflight = new HttpRequestMessage(HttpMethod.Options, $"/apps/{AppA}/api/messages");
        preflight.Headers.Add("Origin", "null");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization, content-type");
        using var answer = await Bare().SendAsync(preflight, Token);
        Assert.Equal(HttpStatusCode.NoContent, answer.StatusCode);
        Assert.Equal("*", answer.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("authorization, content-type", answer.Headers.GetValues("Access-Control-Allow-Headers").Single());
        Assert.Equal("GET, HEAD, POST, PUT, PATCH, DELETE", answer.Headers.GetValues("Access-Control-Allow-Methods").Single());

        using var foreign = new HttpRequestMessage(HttpMethod.Options, $"/apps/{AppA}/api/messages");
        foreign.Headers.Add("Origin", "https://evil.example");
        foreign.Headers.Add("Access-Control-Request-Method", "POST");
        using var refused = await Bare().SendAsync(foreign, Token);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.False(refused.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task OnlyAValidBackendTokenOfTheAppPassesAndNeverACookie()
    {
        await InstallAsync(AppA);
        await InstallAsync(AppB);
        Run(AppA);
        Run(AppB);
        var path = $"/apps/{AppA}/api/messages";

        using (var refused = await GetAsync(path))
        {
            Assert.Equal("app.token.invalid", await refused.ErrorCodeAsync());
            Assert.Equal(AppFrontendHosting.ContentSecurityPolicy(internet: false), refused.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("*", refused.Headers.GetValues("Access-Control-Allow-Origin").Single());
        }
        Assert.Equal("app.token.invalid", await ErrorAsync(await GetAsync(path, "not-a-token")));
        Assert.Equal("app.token.invalid", await ErrorAsync(await GetAsync(path, await TokenAsync(_admin, AppA, "host"))));
        Assert.Equal("app.token.invalid", await ErrorAsync(await GetAsync(path, await TokenAsync(_admin, AppB))));
        Assert.Equal("app.token.invalid", await ErrorAsync(await GetAsync($"{path}?pithyx-ticket={await TicketAsync(_admin, AppA)}")));
        // The administrator's session cookie alone is not enough.
        using (var cookieOnly = await _admin.GetAsync(path, Token))
        {
            Assert.Equal("app.token.invalid", await ErrorAsync(cookieOnly));
        }

        var token = await TokenAsync(_admin, AppA);
        using (var byHeader = await GetAsync(path, token))
        {
            Assert.Equal(HttpStatusCode.OK, byHeader.StatusCode);
        }
        using (var byQuery = await GetAsync($"{path}?pithyx-token={token}"))
        {
            Assert.Equal(HttpStatusCode.OK, byQuery.StatusCode);
        }

        _factory.Time.Advance(AppTokens.TokenLifetime);
        Assert.Equal("app.token.invalid", await ErrorAsync(await GetAsync(path, token)));
    }

    [Fact]
    public async Task TheBackendGetsTheCheckedUserAndNothingTheBrowserClaims()
    {
        await InstallAsync(AppA);
        Run(AppA);
        var (rita, ritaId) = await UserAsync("rita");
        foreach (var group in new[] { "family", "editors" })
        {
            using var created = await _admin.PostAsJsonAsync("/api/groups", new { name = group, memberIds = new[] { ritaId } }, Token);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
        var token = await TokenAsync(rita, AppA);
        var trace = "4bf92f3577b34da6a3ce929d0e0e4736";

        using var response = await GetAsync($"/apps/{AppA}/api/messages/7?b=%20x&pithyx-token={token}&a=1&PITHYX-other=2", configure: request =>
        {
            request.Headers.Add("Cookie", "pithyx_session=stolen");
            request.Headers.Add("X-Pithyx-User", "admin");
            request.Headers.Add("X-Pithyx-Admin", "true");
            request.Headers.Add("X-Forwarded-For", "10.0.0.1");
            request.Headers.Add("Forwarded", "for=10.0.0.1");
            request.Headers.Add("traceparent", $"00-{trace}-00f067aa0ba902b7-01");
            request.Headers.Add("X-Custom", "kept");
            request.Headers.Referrer = new Uri($"http://box/apps/{AppA}/api/page?pithyx-token={token}");
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echo = await response.JsonAsync();
        var headers = echo.GetProperty("headers");
        Assert.Equal("api", echo.GetProperty("backend").GetString());
        Assert.Equal("/messages/7", echo.GetProperty("path").GetString());
        Assert.Equal("?b=%20x&a=1", echo.GetProperty("query").GetString());
        Assert.Equal("rita", headers.GetProperty("x-pithyx-user").GetString());
        Assert.Equal(ritaId.ToString(System.Globalization.CultureInfo.InvariantCulture), headers.GetProperty("x-pithyx-user-id").GetString());
        Assert.Equal("editors,family", headers.GetProperty("x-pithyx-groups").GetString());
        Assert.Equal("false", headers.GetProperty("x-pithyx-admin").GetString());
        Assert.Equal($"/apps/{AppA}/api", headers.GetProperty("x-forwarded-prefix").GetString());
        Assert.Equal("127.0.0.1", headers.GetProperty("x-forwarded-for").GetString());
        Assert.Equal("http", headers.GetProperty("x-forwarded-proto").GetString());
        Assert.Equal("kept", headers.GetProperty("x-custom").GetString());
        var traceparent = headers.GetProperty("traceparent").GetString()!;
        Assert.StartsWith($"00-{trace}-", traceparent, StringComparison.Ordinal);
        Assert.DoesNotContain("00f067aa0ba902b7", traceparent, StringComparison.Ordinal);
        foreach (var name in new[] { "cookie", "authorization", "forwarded", "referer" })
        {
            Assert.False(headers.TryGetProperty(name, out _), name);
        }
        Assert.Equal(_api.Authority, headers.GetProperty("host").GetString());

        // The answer acts on nothing beyond itself (no cookie, no wiping, no HSTS on the box's origin), is readable by the
        // sandboxed window and stays sandboxed in a tab.
        foreach (var name in new[] { "Set-Cookie", "Clear-Site-Data", "Strict-Transport-Security", "Access-Control-Allow-Credentials" })
        {
            Assert.False(response.Headers.Contains(name), name);
        }
        Assert.Equal("api", response.Headers.GetValues("X-Backend").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Expose-Headers").Single());
        Assert.Equal(AppFrontendHosting.ContentSecurityPolicy(internet: false), response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());

        // An administrator is named as one; a bearer header reaches the backend only as the user.
        using var asAdmin = await GetAsync($"/apps/{AppA}/api", await TokenAsync(_admin, AppA));
        var adminHeaders = (await asAdmin.JsonAsync()).GetProperty("headers");
        Assert.Equal("admin", adminHeaders.GetProperty("x-pithyx-user").GetString());
        Assert.Equal("true", adminHeaders.GetProperty("x-pithyx-admin").GetString());
        Assert.Equal("", adminHeaders.GetProperty("x-pithyx-groups").GetString());
        Assert.False(adminHeaders.TryGetProperty("authorization", out _));
    }

    [Fact]
    public async Task EndpointsHaveNamesAndTheErrorsSayWhatIsMissing()
    {
        await InstallAsync(AppA);
        var token = await TokenAsync(_admin, AppA);

        Assert.Equal("app.backend.unavailable", await ErrorAsync(await GetAsync($"/apps/{AppA}/api/x", token)));
        Run(AppA);
        using (var metrics = await GetAsync($"/apps/{AppA}/api-metrics/stats", token))
        {
            var echo = await metrics.JsonAsync();
            Assert.Equal("metrics", echo.GetProperty("backend").GetString());
            Assert.Equal("/stats", echo.GetProperty("path").GetString());
            Assert.Equal($"/apps/{AppA}/api-metrics", echo.GetProperty("headers").GetProperty("x-forwarded-prefix").GetString());
        }
        using (var root = await GetAsync($"/apps/{AppA}/api", token))
        {
            Assert.Equal("/", (await root.JsonAsync()).GetProperty("path").GetString());
        }
        using (var undeclared = await GetAsync($"/apps/{AppA}/api-nope/x", token))
        {
            Assert.Equal(HttpStatusCode.NotFound, undeclared.StatusCode);
            Assert.Equal("request.route.not_found", await undeclared.ErrorCodeAsync());
        }
        // api-api is no second name for api, and an upper-case name is no endpoint: the frontend's 404.
        using (var alias = await GetAsync($"/apps/{AppA}/api-api/x", token))
        {
            Assert.Equal(HttpStatusCode.NotFound, alias.StatusCode);
        }

        // A backend that does not answer is unavailable, not a gateway error.
        using (var closed = new TcpListener(IPAddress.Loopback, 0))
        {
            closed.Start();
            var port = ((IPEndPoint)closed.LocalEndpoint).Port;
            closed.Stop();
            Backends.SetRunning(AppA, new Dictionary<string, Uri> { ["api"] = new($"http://127.0.0.1:{port}/") });
        }
        using (var down = await GetAsync($"/apps/{AppA}/api/x", token))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
            Assert.Equal("app.backend.unavailable", await down.ErrorCodeAsync());
        }
        Backends.SetStopped(AppA);
        Assert.Equal("app.backend.unavailable", await ErrorAsync(await GetAsync($"/apps/{AppA}/api/x", token)));
    }

    [Fact]
    public async Task BodiesAndMethodsPassThrough()
    {
        await InstallAsync(AppA);
        Run(AppA);
        var (rita, _) = await UserAsync("rita");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/apps/{AppA}/api/items/1") { Content = new StringContent("{\"a\":1}", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(rita, AppA));
        request.Content.Headers.Add("X-Pithyx-Admin", "true");
        using var response = await Bare().SendAsync(request, Token);
        var echo = await response.JsonAsync();
        Assert.Equal("PUT", echo.GetProperty("method").GetString());
        Assert.StartsWith("application/json", echo.GetProperty("headers").GetProperty("content-type").GetString(), StringComparison.Ordinal);
        Assert.Equal("false", echo.GetProperty("headers").GetProperty("x-pithyx-admin").GetString());
    }

    [Fact]
    public async Task AWebSocketNeedsAOneTimeTicketForItsEndpoint()
    {
        await InstallAsync(AppA);
        Run(AppA);
        var baseAddress = _factory.Address;

        Assert.Equal(401, await RefusedUpgradeAsync(baseAddress, $"/apps/{AppA}/api/live"));
        Assert.Equal(401, await RefusedUpgradeAsync(baseAddress, $"/apps/{AppA}/api/live?pithyx-token={await TokenAsync(_admin, AppA)}"));
        Assert.Equal(401, await RefusedUpgradeAsync(baseAddress, $"/apps/{AppA}/api/live?pithyx-ticket={await TicketAsync(_admin, AppA, "metrics")}"));

        var ticket = await TicketAsync(_admin, AppA);
        using (var socket = await ConnectAsync($"/apps/{AppA}/api/live?x=1&pithyx-ticket={ticket}"))
        {
            Assert.Equal("api admin /live?x=1", await ReceiveAsync(socket));
            await socket.SendAsync(Encoding.UTF8.GetBytes("ping"), WebSocketMessageType.Text, true, Token);
            Assert.Equal("ping", await ReceiveAsync(socket));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, Token);
        }
        Assert.Equal(401, await RefusedUpgradeAsync(baseAddress, $"/apps/{AppA}/api/live?pithyx-ticket={ticket}"));

        using var metrics = await ConnectAsync($"/apps/{AppA}/api-metrics/live?pithyx-ticket={await TicketAsync(_admin, AppA, "metrics")}");
        Assert.Equal("metrics admin /live", await ReceiveAsync(metrics));
    }

    [Fact]
    public async Task SocketsCloseOnSignOutRevokeUninstallAndAStoppedBackend()
    {
        await InstallAsync(AppA);
        Run(AppA);
        var (rita, ritaId) = await UserAsync("rita");
        var sockets = _factory.Services.GetRequiredService<AppProxySockets>();

        async Task<ClientWebSocket> OpenAsync(HttpClient session)
        {
            var socket = await ConnectAsync($"/apps/{AppA}/api/live?pithyx-ticket={await TicketAsync(session, AppA)}");
            Assert.NotNull(await ReceiveAsync(socket));
            return socket;
        }

        // Sign-out.
        using (var socket = await OpenAsync(rita))
        {
            using var other = await OpenAsync(_admin);
            using (var logout = await rita.PostAsJsonAsync("/api/auth/logout", new { }, Token))
            {
                Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            }
            Assert.Null(await ReceiveAsync(socket));
            await other.SendAsync(Encoding.UTF8.GetBytes("still"), WebSocketMessageType.Text, true, Token);
            Assert.Equal("still", await ReceiveAsync(other));
        }

        // A revoked grant: the app becomes administrators only.
        var rita2 = await SessionAsync(ritaId);
        using (var socket = await OpenAsync(rita2))
        {
            using var other = await OpenAsync(_admin);
            using var revoke = await _admin.PutAsJsonAsync($"/api/apps/{AppA}/grants", new { grants = Array.Empty<object>() }, Token);
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
            Assert.Null(await ReceiveAsync(socket));
            await other.SendAsync(Encoding.UTF8.GetBytes("still"), WebSocketMessageType.Text, true, Token);
            Assert.Equal("still", await ReceiveAsync(other));
        }

        // A stopped backend.
        using (var socket = await OpenAsync(_admin))
        {
            Backends.SetStopped(AppA);
            Assert.Null(await ReceiveAsync(socket));
        }

        // The backend starts again at another address.
        Run(AppA);
        using (var socket = await OpenAsync(_admin))
        {
            Backends.SetRunning(AppA, new Dictionary<string, Uri> { ["api"] = _metrics, ["metrics"] = _metrics });
            Assert.Null(await ReceiveAsync(socket));
        }

        // An uninstall.
        Run(AppA);
        using (var socket = await OpenAsync(_admin))
        {
            using (var remove = await _admin.DeleteAsync($"/api/apps/{AppA}?data=delete", Token))
            {
                await FinishedAsync(await remove.JsonAsync());
            }
            Assert.Null(await ReceiveAsync(socket));
        }
        await JobTestSupport.WaitUntilAsync(() => Task.FromResult(sockets.Count == 0), "sockets forgotten");
    }

    [Fact]
    public async Task TokensAndTicketsNeverReachTheLogs()
    {
        await InstallAsync(AppA);
        Run(AppA);
        var token = await TokenAsync(_admin, AppA);
        var ticket = await TicketAsync(_admin, AppA);
        using (var response = await GetAsync($"/apps/{AppA}/api/x?pithyx-token={token}"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using (var socket = await ConnectAsync($"/apps/{AppA}/api/live?pithyx-ticket={ticket}"))
        {
            Assert.NotNull(await ReceiveAsync(socket));
        }
        // A backend that is down makes the forwarder log its error with the destination.
        Backends.SetRunning(AppA, new Dictionary<string, Uri> { ["api"] = new("http://127.0.0.1:1/") });
        using (await GetAsync($"/apps/{AppA}/api/x?pithyx-token={token}"))
        {
        }

        var logs = string.Join('\n', Directory.GetFiles(Path.Combine(_factory.DataDirectory, "logs"), "daemon*.json").Select(file =>
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }));
        Assert.Contains($"/apps/{AppA}/api/x", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(token, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(ticket, logs, StringComparison.Ordinal);
    }

    /// <summary>The daemon on a real Kestrel port, so WebSockets go through the proxy as in a browser.</summary>
    private sealed class KestrelDaemonFactory : DaemonFactory
    {
        private readonly int _port = FreePort();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("PITHYX_DEV_PORT", _port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        public Uri Address => new($"http://127.0.0.1:{_port}/");

        private static int FreePort()
        {
            using var free = new TcpListener(IPAddress.Loopback, 0);
            free.Start();
            var port = ((IPEndPoint)free.LocalEndpoint).Port;
            free.Stop();
            return port;
        }
    }
}
