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

/// <summary>
/// The web ports on a real Kestrel: TLS on the web port with plain HTTP sent to https://, both proxy ports redirecting
/// unknown names, and the certificate chosen by the server name of each handshake.
/// </summary>
public sealed class WebTransportTests : IAsyncLifetime
{
    private static readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly SigningCertificate _root = CertificateAuthority.CreateRoot("pithyx", NameScope.Local("pithyx", [], []), _now);
    private WebTransport _transport = null!;
    private WebApplication _app = null!;
    private X509Certificate2 _proxyCertificate = null!;
    private SigningCertificate _intermediate = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var intermediate = _intermediate = CertificateAuthority.CreateIntermediate(_root, "pithyx", _now);
        var server = CertificateAuthority.Issue(intermediate, ["localhost", "127.0.0.1"], CertificateAuthority.ServerDays, CertificateKeyType.Ecdsa, _now).Certificate;
        var options = new SslServerAuthenticationOptions
        {
            ServerCertificateContext = SslStreamCertificateContext.Create(server, new X509Certificate2Collection(new[] { intermediate.Certificate, _root.Certificate }), offline: true),
            ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        };
        _proxyCertificate = CertificateAuthority.Issue(intermediate, ["*.proxy.home.arpa"], CertificateAuthority.ServerDays, CertificateKeyType.Ecdsa, _now).Certificate;
        var proxyOptions = new SslServerAuthenticationOptions
        {
            ServerCertificateContext = SslStreamCertificateContext.Create(_proxyCertificate, new X509Certificate2Collection(new[] { intermediate.Certificate, _root.Certificate }), offline: true),
            ApplicationProtocols = [SslApplicationProtocol.Http11],
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        };
        _transport = new WebTransport(FreePort(), Https: true, FreePort(), FreePort());
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => _transport.Listen(kestrel, name => name?.EndsWith(".proxy.home.arpa", StringComparison.Ordinal) == true ? proxyOptions : options));
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

        using var tls = await client.GetAsync($"https://127.0.0.1:{_transport.ProxyTlsPort}/settings", Token);
        using var plain = await client.GetAsync($"http://[::1]:{_transport.ProxyPlainPort}/", Token);
        using var installId = await client.GetAsync($"http://127.0.0.1:{_transport.ProxyPlainPort}/api/install-id", Token);

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

    [Fact]
    public async Task EachServerNameGetsItsOwnCertificateOnBothTlsPorts()
    {
        foreach (var port in new[] { _transport.ProxyTlsPort!.Value, _transport.WebPort })
        {
            X509Certificate2? presented = null;
            using var client = Client(certificate => presented = certificate);

            // The client takes the server name of the handshake from Host.
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{port}/") { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
            request.Headers.Host = "media.proxy.home.arpa";
            using var response = await client.SendAsync(request, Token);

            Assert.Equal(_proxyCertificate.Thumbprint, presented?.Thumbprint);
            // The proxy host offers HTTP/1.1 only, as its options say.
            Assert.Equal(HttpVersion.Version11, response.Version);
        }
        X509Certificate2? other = null;
        using (var client = Client(certificate => other = certificate))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{_transport.ProxyTlsPort}/");
            request.Headers.Host = "nas.home.arpa";
            using var response = await client.SendAsync(request, Token);
        }
        Assert.NotEqual(_proxyCertificate.Thumbprint, other?.Thumbprint);
    }

    // Trusts only the test's root, as a device that installed the box's root does; redirects are not followed.
    private HttpClient Client(Action<X509Certificate2>? presented = null)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        if (presented is not null)
        {
            // The chain must reach the test's root; which certificate came matters here, not whether it fits the name.
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
            {
                presented(new X509Certificate2(certificate!));
                chain!.ChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
                chain.ChainPolicy.CustomTrustStore.Add(_root.Certificate);
                chain.ChainPolicy.ExtraStore.Add(_intermediate.Certificate);
                return (errors & ~(SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors)) == SslPolicyErrors.None
                    && chain.Build(new X509Certificate2(certificate!));
            };
            return new HttpClient(handler);
        }
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
