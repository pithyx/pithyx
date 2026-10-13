using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Tls;

namespace Pithyx.Daemon.Tests.Tls;

/// <summary>The web ports on a real Kestrel: TLS on the web port with plain HTTP sent to https://, and both redirect ports.</summary>
public sealed class WebTransportTests : IAsyncLifetime
{
    private static readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly SigningCertificate _root = CertificateAuthority.CreateRoot("pithyx", NameScope.Local("pithyx", [], []), _now);
    private WebTransport _transport = null!;
    private WebApplication _app = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var intermediate = CertificateAuthority.CreateIntermediate(_root, "pithyx", _now);
        var server = CertificateAuthority.Issue(intermediate, ["localhost", "127.0.0.1"], CertificateAuthority.ServerDays, CertificateKeyType.Ecdsa, _now).Certificate;
        var options = new SslServerAuthenticationOptions
        {
            ServerCertificateContext = SslStreamCertificateContext.Create(server, new X509Certificate2Collection(new[] { intermediate.Certificate, _root.Certificate }), offline: true),
            ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        };
        _transport = new WebTransport(FreePort(), Https: true, FreePort(), FreePort());
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => _transport.Listen(kestrel, () => options));
        _app = builder.Build();
        _app.UseMiddleware<TransportRedirect>(_transport);
        _app.MapGet("/{**path}", (HttpContext context) => $"{context.Request.Protocol} {context.Request.IsHttps}");
        await _app.StartAsync(Token);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task TheWebPortSpeaksHttp2OverTlsWithTheWholeChain()
    {
        using var client = Client();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://localhost:{_transport.WebPort}/hello") { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        using var response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("HTTP/2 True", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task PlainHttpOnTheWebPortGoesToHttps()
    {
        using var client = Client();

        using var response = await client.GetAsync($"http://localhost:{_transport.WebPort}/apps/x?y=1", Token);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal($"https://localhost:{_transport.WebPort}/apps/x?y=1", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task BothRedirectPortsGoToTheWebPort()
    {
        using var client = Client();

        using var tls = await client.GetAsync($"https://127.0.0.1:{_transport.TlsRedirectPort}/settings", Token);
        using var plain = await client.GetAsync($"http://[::1]:{_transport.PlainRedirectPort}/", Token);
        using var installId = await client.GetAsync($"http://127.0.0.1:{_transport.PlainRedirectPort}/api/install-id", Token);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, tls.StatusCode);
        Assert.Equal($"https://127.0.0.1:{_transport.WebPort}/settings", tls.Headers.Location?.ToString());
        Assert.Equal($"https://[::1]:{_transport.WebPort}/", plain.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.OK, installId.StatusCode);
        Assert.Equal("HTTP/1.1 False", await installId.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task AConnectionThatClosesAtOnceLeavesTheListenerWorking()
    {
        using (var socket = new TcpClient())
        {
            await socket.ConnectAsync(IPAddress.Loopback, _transport.WebPort, Token);
        }
        using var client = Client();

        using var response = await client.GetAsync($"https://localhost:{_transport.WebPort}/", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Trusts only the test's root, as a device that installed the box's root does; redirects are not followed.
    private HttpClient Client()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        handler.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(_root.Certificate);
        return new HttpClient(handler);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
