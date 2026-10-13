using System.IO.Pipelines;
using System.Net.Security;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Pithyx.Daemon.Hosting;

/// <summary>
/// The ports of the web interface (D346) and the reverse proxy (D347). 7484 is the one address of the box. With HTTPS
/// it speaks TLS and answers a plain HTTP request on the same port with a redirect to <c>https://</c>. 443 (TLS) and
/// 80 (plain) serve the reverse proxy's hosts by name and redirect every other name to 7484; port 80 also answers
/// <c>/api/install-id</c> for the installer. Without HTTPS (development, simulation, the installer of the live system)
/// 7484 is plain HTTP. Every handshake gets the certificate for its server name: a proxy host's own, or the box's.
/// </summary>
/// <param name="WebPort">7484, or <c>PITHYX_DEV_PORT</c> in a development build.</param>
/// <param name="Https">The web port speaks TLS.</param>
/// <param name="ProxyPlainPort">80 on a box, otherwise none (or the first port of <c>PITHYX_DEV_PROXY_PORTS</c>).</param>
/// <param name="ProxyTlsPort">443 on a box with HTTPS, otherwise none (or the second port of <c>PITHYX_DEV_PROXY_PORTS</c>).</param>
public sealed record WebTransport(int WebPort, bool Https, int? ProxyPlainPort, int? ProxyTlsPort)
{
    public const int PlainPort = 80;
    public const int TlsPort = 443;

    // The marker a connection on the web port gets when its first byte is no TLS handshake.
    private const string PlainItem = "pithyx.plain";

    // A client that opens a connection and sends nothing is dropped after this, like a stalled TLS handshake.
    private static readonly TimeSpan _firstByteTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The ports of a box (80 and, with HTTPS, 443), or in a development build the plain and TLS ports of
    /// <paramref name="developmentProxyPorts"/> (<c>PITHYX_DEV_PROXY_PORTS=8080,8443</c>) to try the proxy next to the dev unit.
    /// </summary>
    public static WebTransport For(int webPort, bool https, bool box, string? developmentProxyPorts = null)
    {
        if (box)
        {
            return new(webPort, https, PlainPort, https ? TlsPort : null);
        }
        var ports = (developmentProxyPorts ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(port => int.TryParse(port, System.Globalization.CultureInfo.InvariantCulture, out var value) && value is > 0 and <= 65535 ? value : (int?)null)
            .ToList();
        return new(webPort, https, ports.ElementAtOrDefault(0), https ? ports.ElementAtOrDefault(1) : null);
    }

    /// <summary>The listeners on every address, IPv4 and IPv6, with the reverse proxy's and the box's certificates.</summary>
    public void Listen(KestrelServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Listen(options, name => options.ApplicationServices.GetRequiredService<ReverseProxy.ProxyService>().ServerOptions(name));
    }

    /// <summary>The listeners with the same TLS settings for every handshake.</summary>
    public void Listen(KestrelServerOptions options, Func<SslServerAuthenticationOptions> serverOptions)
    {
        ArgumentNullException.ThrowIfNull(serverOptions);
        Listen(options, _ => serverOptions());
    }

    /// <summary>The listeners with the TLS settings <paramref name="serverOptions"/> gives for the server name of every handshake.</summary>
    public void Listen(KestrelServerOptions options, Func<string?, SslServerAuthenticationOptions> serverOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serverOptions);
        options.ListenAnyIP(WebPort, listen =>
        {
            if (Https)
            {
                UseTls(listen, serverOptions, sniffPlain: true);
            }
        });
        if (ProxyPlainPort is { } plain)
        {
            options.ListenAnyIP(plain);
        }
        if (ProxyTlsPort is { } tls)
        {
            options.ListenAnyIP(tls, listen => UseTls(listen, serverOptions, sniffPlain: false));
        }
    }

    /// <summary>
    /// TLS 1.2 and 1.3 with HTTP/2 and HTTP/1.1 and the current certificate on every handshake. With
    /// <paramref name="sniffPlain"/> a connection whose first byte is no TLS handshake record (0x16) skips TLS and is
    /// marked, so <see cref="TransportRedirect"/> sends it to <c>https://</c>.
    /// </summary>
    private static void UseTls(ListenOptions listen, Func<string?, SslServerAuthenticationOptions> serverOptions, bool sniffPlain)
    {
        listen.Protocols = HttpProtocols.Http1AndHttp2;
        ConnectionDelegate? plain = null;
        if (sniffPlain)
        {
            listen.Use(tls => connection => SniffAsync(connection, tls, plain!));
        }
        listen.UseHttps(new TlsHandshakeCallbackOptions
        {
            OnConnection = context => ValueTask.FromResult(serverOptions(context.ClientHelloInfo.ServerName)),
            HandshakeTimeout = _firstByteTimeout,
        });
        if (sniffPlain)
        {
            // The middleware after TLS is the HTTP handler itself; plain connections go there directly.
            listen.Use(next => plain = next);
        }
    }

    /// <summary>Whether the request came in as plain HTTP on a port that speaks TLS.</summary>
    public static bool IsPlainOnTlsPort(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<IConnectionItemsFeature>()?.Items.ContainsKey(PlainItem) == true;
    }

    private static async Task SniffAsync(ConnectionContext connection, ConnectionDelegate tls, ConnectionDelegate plain)
    {
        var input = connection.Transport.Input;
        ReadResult result;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed))
        {
            timeout.CancelAfter(_firstByteTimeout);
            try
            {
                result = await input.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        if (result.Buffer.IsEmpty)
        {
            input.AdvanceTo(result.Buffer.Start);
            return;
        }
        var handshake = result.Buffer.FirstSpan[0] == 0x16;
        // Nothing consumed and nothing examined: the next reader gets the same bytes at once.
        input.AdvanceTo(result.Buffer.Start);
        if (handshake)
        {
            await tls(connection);
            return;
        }
        connection.Items[PlainItem] = true;
        await plain(connection);
    }
}

/// <summary>
/// Sends requests on the proxy ports that no proxy host took (<see cref="ReverseProxy.HostProxy"/> runs first), and
/// plain HTTP on the web port, to the same path on <c>https://&lt;host&gt;:7484</c> (<c>http://</c> without HTTPS).
/// Only <c>/api/install-id</c> is answered on port 80 itself: the installer page finds the rebooted box with it, and a redirect would turn its cross-origin request into
/// a failure. No HSTS: a browser refuses to let anyone pass a certificate warning on an HSTS host, and the box's
/// certificate is untrusted until a device trusts the box's authority.
/// </summary>
public sealed class TransportRedirect(RequestDelegate next, WebTransport transport)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var port = context.Connection.LocalPort;
        int target;
        if (port == transport.ProxyPlainPort)
        {
            if (context.Request.Path.Equals("/api/install-id", StringComparison.OrdinalIgnoreCase))
            {
                return next(context);
            }
            target = transport.WebPort;
        }
        else if (port == transport.ProxyTlsPort)
        {
            target = transport.WebPort;
        }
        else if (WebTransport.IsPlainOnTlsPort(context))
        {
            // The port the client used: behind a forwarded port it is not 7484.
            target = context.Request.Host.Port ?? transport.WebPort;
        }
        else
        {
            return next(context);
        }
        context.Response.Redirect(Target(context.Request, transport.Https ? "https" : "http", target), permanent: false, preserveMethod: true);
        return Task.CompletedTask;
    }

    /// <summary>The same URL with <paramref name="scheme"/> on <paramref name="port"/>; an IPv6 address keeps its brackets.</summary>
    public static string Target(HttpRequest request, string scheme, int port)
    {
        ArgumentNullException.ThrowIfNull(request);
        return $"{Origin(request, scheme, port)}{request.PathBase}{request.Path}{request.QueryString}";
    }

    /// <summary>The request's host name with <paramref name="scheme"/> on <paramref name="port"/>.</summary>
    public static string Origin(HttpRequest request, string scheme, int port)
    {
        ArgumentNullException.ThrowIfNull(request);
        var host = request.Host.HasValue ? request.Host.Host : request.HttpContext.Connection.LocalIpAddress?.ToString() ?? "localhost";
        if (host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('['))
        {
            host = $"[{host}]";
        }
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{scheme}://{host}:{port}");
    }
}
