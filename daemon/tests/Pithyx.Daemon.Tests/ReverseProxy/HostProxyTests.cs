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
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Network;
using Pithyx.Daemon.ReverseProxy;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.ReverseProxy;

/// <summary>
/// The reverse proxy on a real Kestrel: the daemon in simulation with a proxy port (<c>PITHYX_DEV_PROXY_PORTS</c>, plain
/// HTTP as simulation has no TLS) in front of a small backend, with forwarding, headers, access rules, the Pithyx
/// sign-in, WebSockets, unbuffered streaming, unknown names and streams.
/// </summary>
public sealed class HostProxyTests : IAsyncLifetime
{
    private static readonly string[] _app = ["app.home.arpa"];
    private static readonly string[] _old = ["old.home.arpa"];
    private static readonly string[] _loopback = ["127.0.0.0/8"];
    private static readonly string[] _rita = ["rita"];
    private readonly int _web = FreePort();
    private readonly int _plain = FreePort();
    private readonly TaskCompletionSource _secondEvent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ProxyDaemon _daemon = null!;
    private WebApplication _backend = null!;
    private int _backendPort;
    private HttpClient _admin = null!;

    private string AppWithPort => $"app.home.arpa:{_plain}";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _backendPort = FreePort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(_backendPort));
        _backend = builder.Build();
        _backend.UseWebSockets();
        _backend.Map("/ws", async (HttpContext context) =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[64];
            var received = await socket.ReceiveAsync(buffer, context.RequestAborted);
            await socket.SendAsync(buffer.AsMemory(0, received.Count), WebSocketMessageType.Text, true, context.RequestAborted);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, context.RequestAborted);
        });
        _backend.MapGet("/sse", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: one\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await _secondEvent.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync("data: two\n\n", context.RequestAborted);
        });
        _backend.Map("/{**path}", (HttpContext context) =>
        {
            context.Response.Headers["X-Backend"] = "yes";
            return Results.Json(new
            {
                path = context.Request.Path.Value,
                query = context.Request.QueryString.Value,
                headers = context.Request.Headers.ToDictionary(header => header.Key.ToLowerInvariant(), header => header.Value.ToString()),
            });
        });
        await _backend.StartAsync(Token);

        _daemon = new ProxyDaemon(_web, _plain);
        _daemon.UseKestrel();
        _daemon.StartServer();
        _admin = WebClient();
        await _admin.SetupAdminAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        await _daemon.DisposeAsync();
        await _backend.DisposeAsync();
    }

    [Fact]
    public async Task ForwardsByNameWithTheForwardedHeadersAndWithoutTheBoxsOwn()
    {
        await CreateHostAsync(new
        {
            domains = _app,
            requestHeaders = new[] { new { name = "X-Api-Key", value = "secret" } },
            responseHeaders = new[] { new { name = "X-Frame-Options", value = "SAMEORIGIN" }, new { name = "X-Backend", value = "" } },
        });

        using var response = await ProxyAsync("app.home.arpa", "/files/a?b=1", request =>
        {
            request.Headers.TryAddWithoutValidation("X-Pithyx-User", "mallory");
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.9");
            request.Headers.TryAddWithoutValidation("Cookie", "pithyx_session=stolen; app=1");
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echo = await response.JsonAsync();
        var headers = echo.GetProperty("headers");
        Assert.Equal("/files/a", echo.GetProperty("path").GetString());
        Assert.Equal("?b=1", echo.GetProperty("query").GetString());
        Assert.Equal($"localhost:{_backendPort}", headers.GetProperty("host").GetString());
        Assert.Equal("127.0.0.1", headers.GetProperty("x-forwarded-for").GetString());
        Assert.Equal("app.home.arpa", headers.GetProperty("x-forwarded-host").GetString());
        Assert.Equal("http", headers.GetProperty("x-forwarded-proto").GetString());
        Assert.Equal("app=1", headers.GetProperty("cookie").GetString());
        Assert.Equal("secret", headers.GetProperty("x-api-key").GetString());
        Assert.False(headers.TryGetProperty("x-pithyx-user", out _));
        Assert.Equal("SAMEORIGIN", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.False(response.Headers.Contains("X-Backend"));

        // The host's access log lands in the Log Center under its own source.
        var logged = await EventuallyAsync(async () =>
        {
            var page = await _admin.GetFromJsonAsync<JsonElement>("/api/logs?source=proxy/app.home.arpa", Token);
            return page.GetProperty("entries").EnumerateArray().Any(entry => entry.GetProperty("message").GetString() == $"GET app.home.arpa/files/a 200 {entry.GetProperty("data").GetProperty("durationMs").GetString()} ms");
        });
        Assert.True(logged);
    }

    [Fact]
    public async Task AChangeAppliesToTheNextRequestWithoutARestart()
    {
        var settings = await CreateHostAsync(new { domains = _app, preserveHost = true });
        var id = settings.GetProperty("hosts")[0].GetProperty("id").GetInt64();
        using (var first = await ProxyAsync("app.home.arpa", "/"))
        {
            Assert.Equal("app.home.arpa", (await first.JsonAsync()).GetProperty("headers").GetProperty("host").GetString());
        }

        using (var changed = await _admin.PutAsJsonAsync($"/api/system/proxy/hosts/{id}", Host(new { domains = _app, enabled = false }), Token))
        {
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        }
        using var second = await ProxyAsync("app.home.arpa", "/x");

        Assert.Equal(HttpStatusCode.TemporaryRedirect, second.StatusCode);
        Assert.Equal($"http://app.home.arpa:{_web}/x", second.Headers.Location?.ToString());
    }

    [Fact]
    public async Task UnknownNamesGoToTheBoxOrGet404ButTheInstallIdStays()
    {
        await File.WriteAllTextAsync(Path.Combine(_daemon.DataDirectory, Pithyx.Daemon.Installer.SystemInstaller.InstallIdFile), "a1b2", Token);
        using (var redirected = await ProxyAsync("nas.home.arpa", "/settings?x=1"))
        {
            Assert.Equal(HttpStatusCode.TemporaryRedirect, redirected.StatusCode);
            Assert.Equal($"http://nas.home.arpa:{_web}/settings?x=1", redirected.Headers.Location?.ToString());
        }

        using (var options = await _admin.PutAsJsonAsync("/api/system/proxy/options", new { unknownHosts = "notFound" }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        }
        using var unknown = await ProxyAsync("nas.home.arpa", "/settings");
        using var installId = await ProxyAsync("192.168.1.10", "/api/install-id");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(ErrorCodes.ProxyHostUnknown, await unknown.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, installId.StatusCode);
    }

    [Fact]
    public async Task ARedirectHostKeepsThePathWhenAsked()
    {
        using (var created = await _admin.PostAsJsonAsync("/api/system/proxy/redirects", new { domains = _old, enabled = true, target = "https://new.example.org/base", status = 308, preservePath = true, certificate = "box" }, Token))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        }

        using var response = await ProxyAsync("old.home.arpa", "/a/b?c=1");

        Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
        Assert.Equal("https://new.example.org/base/a/b?c=1", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task BasicAuthAndTheAddressListsGuardTheHost()
    {
        var settings = await CreateHostAsync(new { domains = _app, basicAuth = new[] { new { username = "anna", password = "river-candle" } } });
        var id = settings.GetProperty("hosts")[0].GetProperty("id").GetInt64();
        Assert.Equal(["anna"], settings.GetProperty("hosts")[0].GetProperty("basicAuthUsers").EnumerateArray().Select(user => user.GetString()));

        using (var anonymous = await ProxyAsync("app.home.arpa", "/"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal("Basic", anonymous.Headers.WwwAuthenticate.Single().Scheme);
        }
        using (var wrong = await ProxyAsync("app.home.arpa", "/", request => request.Headers.Authorization = Basic("anna", "wrong")))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }
        using (var right = await ProxyAsync("app.home.arpa", "/", request => request.Headers.Authorization = Basic("anna", "river-candle")))
        {
            Assert.Equal(HttpStatusCode.OK, right.StatusCode);
            Assert.False((await right.JsonAsync()).GetProperty("headers").TryGetProperty("authorization", out _));
        }

        // Saving without a password keeps anna's; denying the address shuts everybody from it out.
        using (var denied = await _admin.PutAsJsonAsync($"/api/system/proxy/hosts/{id}", Host(new { domains = _app, basicAuth = new[] { new { username = "anna" } }, denyAddresses = _loopback }), Token))
        {
            Assert.Equal(HttpStatusCode.OK, denied.StatusCode);
        }
        using var blocked = await ProxyAsync("app.home.arpa", "/", request => request.Headers.Authorization = Basic("anna", "river-candle"));
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal(ErrorCodes.ProxyAddressDenied, await blocked.ErrorCodeAsync());
    }

    [Fact]
    public async Task ThePithyxSignInLetsInOnlyAllowedUsersAndEndsWithTheSession()
    {
        using (var user = await _admin.PostAsync("/api/users", new { username = "rita", password = "river-candle-orbit-7" }))
        {
            Assert.Equal(HttpStatusCode.Created, user.StatusCode);
        }
        using (var other = await _admin.PostAsync("/api/users", new { username = "otto", password = "river-candle-orbit-7" }))
        {
            Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        }
        await CreateHostAsync(new { domains = _app, signIn = new { required = true, users = _rita, groups = Array.Empty<string>() } });
        // A browser on another port than 80 names it in Host.
        var wanted = $"http://{AppWithPort}/x?y=1";

        using (var page = await ProxyAsync(AppWithPort, "/x?y=1", request => request.Headers.Accept.ParseAdd("text/html")))
        {
            Assert.Equal(HttpStatusCode.Found, page.StatusCode);
            Assert.Equal($"http://app.home.arpa:{_web}/?pithyx-continue={Uri.EscapeDataString(wanted)}", page.Headers.Location?.ToString());
        }
        using (var script = await ProxyAsync(AppWithPort, "/x"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, script.StatusCode);
        }

        using var otto = WebClient();
        Assert.Equal(HttpStatusCode.OK, (await otto.LoginAsync("otto", "river-candle-orbit-7")).StatusCode);
        using (var refused = await otto.PostAsync("/api/proxy/tickets", new { url = wanted }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(ErrorCodes.ProxyAccessDenied, await refused.ErrorCodeAsync());
        }

        using var rita = WebClient();
        Assert.Equal(HttpStatusCode.OK, (await rita.LoginAsync("rita", "river-candle-orbit-7")).StatusCode);
        using var ticket = await rita.PostAsync("/api/proxy/tickets", new { url = wanted });
        Assert.Equal(HttpStatusCode.OK, ticket.StatusCode);
        var url = new Uri((await ticket.JsonAsync()).GetProperty("url").GetString()!);
        Assert.Equal("/.pithyx/auth", url.AbsolutePath);

        using var redeemed = await ProxyAsync(AppWithPort, url.PathAndQuery);
        Assert.Equal(HttpStatusCode.Found, redeemed.StatusCode);
        Assert.Equal("/x?y=1", redeemed.Headers.Location?.ToString());
        var cookie = redeemed.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(ProxySignIn.CookieName + "=", StringComparison.Ordinal)).Split(';')[0];
        using (var again = await ProxyAsync(AppWithPort, url.PathAndQuery))
        {
            // A ticket works once.
            Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        }

        using (var inside = await ProxyAsync(AppWithPort, "/x?y=1", request => request.Headers.TryAddWithoutValidation("Cookie", cookie + "; app=1")))
        {
            Assert.Equal(HttpStatusCode.OK, inside.StatusCode);
            var headers = (await inside.JsonAsync()).GetProperty("headers");
            Assert.Equal("rita", headers.GetProperty("x-pithyx-user").GetString());
            Assert.Equal("false", headers.GetProperty("x-pithyx-admin").GetString());
            Assert.Equal("app=1", headers.GetProperty("cookie").GetString());
        }

        using (await rita.PostAsync("/api/auth/logout", new { }))
        {
        }
        using var signedOut = await ProxyAsync(AppWithPort, "/x", request => request.Headers.TryAddWithoutValidation("Cookie", cookie));
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
    }

    [Fact]
    public async Task WebSocketsPassOnlyWhenSwitchedOn()
    {
        var settings = await CreateHostAsync(new { domains = _app });
        var id = settings.GetProperty("hosts")[0].GetProperty("id").GetInt64();

        using (var socket = new ClientWebSocket())
        {
            socket.Options.SetRequestHeader("Host", "app.home.arpa");
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_plain}/ws"), Token);
            await socket.SendAsync(Encoding.UTF8.GetBytes("ping"), WebSocketMessageType.Text, true, Token);
            var buffer = new byte[16];
            var received = await socket.ReceiveAsync(buffer, Token);
            Assert.Equal("ping", Encoding.UTF8.GetString(buffer, 0, received.Count));
        }

        using (await _admin.PutAsJsonAsync($"/api/system/proxy/hosts/{id}", Host(new { domains = _app, webSockets = false }), Token))
        {
        }
        using var refused = new ClientWebSocket();
        refused.Options.SetRequestHeader("Host", "app.home.arpa");
        refused.Options.CollectHttpResponseDetails = true;
        await Assert.ThrowsAsync<WebSocketException>(() => refused.ConnectAsync(new Uri($"ws://127.0.0.1:{_plain}/ws"), Token));
        Assert.Equal(HttpStatusCode.Forbidden, refused.HttpStatusCode);
    }

    [Fact]
    public async Task AnswersStreamThroughUnbuffered()
    {
        await CreateHostAsync(new { domains = _app });
        using var client = ProxyClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_plain}/sse");
        request.Headers.Host = "app.home.arpa";

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Token);
        await using var body = await response.Content.ReadAsStreamAsync(Token);
        using var reader = new StreamReader(body);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        // The first event arrives while the backend still holds back the second.
        Assert.Equal("data: one", await reader.ReadLineAsync(timeout.Token));
        _secondEvent.SetResult();
        Assert.Equal("", await reader.ReadLineAsync(timeout.Token));
        Assert.Equal("data: two", await reader.ReadLineAsync(timeout.Token));
    }

    [Fact]
    public async Task ARequestThatCameFromTheProxyItselfIsALoop()
    {
        await CreateHostAsync(new { domains = _app });
        var token = _daemon.Services.GetRequiredService<ProxyService>().LoopToken;

        using var response = await ProxyAsync("app.home.arpa", "/", request => request.Headers.TryAddWithoutValidation(ProxyService.LoopHeader, token));

        Assert.Equal((HttpStatusCode)508, response.StatusCode);
        Assert.Equal(ErrorCodes.ProxyLoopDetected, await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task AStreamForwardsTcpAndUdpAndOpensItsPort()
    {
        using var tcpTarget = new TcpListener(IPAddress.Loopback, 0);
        tcpTarget.Start();
        var targetPort = ((IPEndPoint)tcpTarget.LocalEndpoint).Port;
        using var udpTarget = new UdpClient(new IPEndPoint(IPAddress.Loopback, targetPort));
        _ = Task.Run(async () =>
        {
            using var accepted = await tcpTarget.AcceptTcpClientAsync(Token);
            var stream = accepted.GetStream();
            var buffer = new byte[16];
            var read = await stream.ReadAsync(buffer, Token);
            await stream.WriteAsync(buffer.AsMemory(0, read), Token);
        }, Token);
        _ = Task.Run(async () =>
        {
            var datagram = await udpTarget.ReceiveAsync(Token);
            await udpTarget.SendAsync(datagram.Buffer, datagram.RemoteEndPoint, Token);
        }, Token);
        var port = FreePort();

        using var created = await _admin.PostAsJsonAsync("/api/system/proxy/streams", new { name = "Echo", enabled = true, port, tcp = true, udp = true, targetHost = "127.0.0.1", targetPort, privilegedPort = false }, Token);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var stream = (await created.JsonAsync()).GetProperty("streams")[0];
        Assert.True(stream.GetProperty("listening").GetBoolean());
        var rules = _daemon.Services.GetRequiredService<Firewall>().Rules;
        Assert.Contains($"tcp dport {{ {port} }} accept", rules, StringComparison.Ordinal);
        Assert.Contains($"udp dport {{ {port} }} accept", rules, StringComparison.Ordinal);

        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port, Token);
            var network = tcp.GetStream();
            await network.WriteAsync("ping"u8.ToArray(), Token);
            var buffer = new byte[16];
            var read = await network.ReadAsync(buffer, Token);
            Assert.Equal("ping", Encoding.ASCII.GetString(buffer, 0, read));
        }
        using (var udp = new UdpClient())
        {
            udp.Connect(IPAddress.Loopback, port);
            await udp.SendAsync("pong"u8.ToArray(), Token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var answer = await udp.ReceiveAsync(timeout.Token);
            Assert.Equal("pong", Encoding.ASCII.GetString(answer.Buffer));
        }

        // A port another program holds is refused before anything is stored.
        using var taken = await _admin.PostAsJsonAsync("/api/system/proxy/streams", new { name = "Clash", enabled = true, port = _web, tcp = true, udp = false, targetHost = "127.0.0.1", targetPort, privilegedPort = false }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, taken.StatusCode);
        Assert.Equal(ErrorCodes.ProxyStreamPortReserved, await taken.ErrorCodeAsync());
        using var holder = new TcpListener(IPAddress.IPv6Any, 0) { Server = { DualMode = true } };
        holder.Start();
        var held = ((IPEndPoint)holder.LocalEndpoint).Port;
        using var inUse = await _admin.PostAsJsonAsync("/api/system/proxy/streams", new { name = "Clash", enabled = true, port = held, tcp = true, udp = false, targetHost = "127.0.0.1", targetPort, privilegedPort = false }, Token);
        Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
        Assert.Equal(ErrorCodes.ProxyStreamPortInUse, await inUse.ErrorCodeAsync());
    }

    private async Task<JsonElement> CreateHostAsync(object overrides)
    {
        using var response = await _admin.PostAsJsonAsync("/api/system/proxy/hosts", Host(overrides), Token);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
        return await response.JsonAsync();
    }

    // A proxy host to the backend with every field, the ones in overrides replaced.
    private Dictionary<string, object?> Host(object overrides)
    {
        var body = new Dictionary<string, object?>
        {
            ["domains"] = _app,
            ["enabled"] = true,
            ["target"] = new { scheme = "http", host = "localhost", port = _backendPort, tls = "system", onBox = true },
            ["webSockets"] = true,
            ["http2"] = true,
            ["certificate"] = "box",
            ["forceHttps"] = true,
            ["hsts"] = false,
            ["forwardedHeaders"] = true,
            ["preserveHost"] = false,
            ["hostHeader"] = null,
            ["requestHeaders"] = Array.Empty<object>(),
            ["responseHeaders"] = Array.Empty<object>(),
            ["maxBodyMegabytes"] = null,
            ["connectTimeoutSeconds"] = 5,
            ["responseTimeoutSeconds"] = 30,
            ["allowAddresses"] = Array.Empty<string>(),
            ["denyAddresses"] = Array.Empty<string>(),
            ["basicAuth"] = Array.Empty<object>(),
            ["signIn"] = new { required = false, users = Array.Empty<string>(), groups = Array.Empty<string>() },
            ["accessLog"] = true,
        };
        foreach (var property in overrides.GetType().GetProperties())
        {
            body[property.Name] = property.GetValue(overrides);
        }
        return body;
    }

    private async Task<HttpResponseMessage> ProxyAsync(string host, string pathAndQuery, Action<HttpRequestMessage>? setup = null)
    {
        using var client = ProxyClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_plain}{pathAndQuery}");
        request.Headers.Host = host;
        setup?.Invoke(request);
        var response = await client.SendAsync(request, Token);
        await response.Content.LoadIntoBufferAsync(Token);
        return response;
    }

    private static HttpClient ProxyClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });

    // A client on the web port that keeps cookies and sends the CSRF header like the desktop.
    private HttpClient WebClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://127.0.0.1:{_web}") };
        client.DefaultRequestHeaders.Add("X-Pithyx-Request", "1");
        return client;
    }

    private static AuthenticationHeaderValue Basic(string username, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));

    private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (await condition())
            {
                return true;
            }
            await Task.Delay(100, Token);
        }
        return false;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>The daemon on Kestrel with its web port and a plain proxy port.</summary>
    private sealed class ProxyDaemon(int web, int plain) : DaemonFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("PITHYX_DEV_PORT", web.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("PITHYX_DEV_PROXY_PORTS", plain.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
