using System.IO.Pipelines;
using System.Net.Security;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Pithyx.Daemon.Tls;

namespace Pithyx.Daemon.Hosting;

/// <summary>
/// The ports of the web interface (D346). 7484 is the one address of the box. With HTTPS it speaks TLS and answers
/// a plain HTTP request on the same port with a redirect to <c>https://</c>; 443 (TLS) and 80 (plain) only redirect
/// to 7484, and port 80 also answers <c>/api/install-id</c> for the installer. Without HTTPS (development,
/// simulation, the installer of the live system) 7484 is plain HTTP.
/// </summary>
/// <param name="WebPort">7484, or <c>PITHYX_DEV_PORT</c> in a development build.</param>
/// <param name="Https">The web port speaks TLS.</param>
/// <param name="PlainRedirectPort">80 on a box, otherwise none.</param>
/// <param name="TlsRedirectPort">443 on a box with HTTPS, otherwise none.</param>
public sealed record WebTransport(int WebPort, bool Https, int? PlainRedirectPort, int? TlsRedirectPort)
{
    public const int PlainPort = 80;
    public const int TlsPort = 443;

    // The marker a connection on the web port gets when its first byte is no TLS handshake.
    private const string PlainItem = "pithyx.plain";

    // A client that opens a connection and sends nothing is dropped after this, like a stalled TLS handshake.
    private static readonly TimeSpan _firstByteTimeout = TimeSpan.FromSeconds(10);

    public static WebTransport For(int webPort, bool https, bool box) =>
        new(webPort, https, box ? PlainPort : null, box && https ? TlsPort : null);

    /// <summary>The listeners on every address, IPv4 and IPv6, with the box's certificates.</summary>
    public void Listen(KestrelServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Listen(options, () => options.ApplicationServices.GetRequiredService<BoxCertificates>().ServerOptions);
    }

    /// <summary>The listeners with the TLS settings <paramref name="serverOptions"/> gives for every handshake.</summary>
    public void Listen(KestrelServerOptions options, Func<SslServerAuthenticationOptions> serverOptions)
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
        if (PlainRedirectPort is { } plain)
        {
            options.ListenAnyIP(plain);
        }
        if (TlsRedirectPort is { } tls)
        {
            options.ListenAnyIP(tls, listen => UseTls(listen, serverOptions, sniffPlain: false));
        }
    }

    /// <summary>
    /// TLS 1.2 and 1.3 with HTTP/2 and HTTP/1.1 and the current certificate on every handshake. With
    /// <paramref name="sniffPlain"/> a connection whose first byte is no TLS handshake record (0x16) skips TLS and is
    /// marked, so <see cref="TransportRedirect"/> sends it to <c>https://</c>.
    /// </summary>
    private static void UseTls(ListenOptions listen, Func<SslServerAuthenticationOptions> serverOptions, bool sniffPlain)
    {
        listen.Protocols = HttpProtocols.Http1AndHttp2;
        ConnectionDelegate? plain = null;
        if (sniffPlain)
        {
            listen.Use(tls => connection => SniffAsync(connection, tls, plain!));
        }
        listen.UseHttps(new TlsHandshakeCallbackOptions
        {
            OnConnection = _ => ValueTask.FromResult(serverOptions()),
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
/// Sends requests on the redirect ports, and plain HTTP on the web port, to the same path on
/// <c>https://&lt;host&gt;:7484</c> (<c>http://</c> without HTTPS). Only <c>/api/install-id</c> is answered on port 80
/// itself: the installer page finds the rebooted box with it, and a redirect would turn its cross-origin request into
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
        if (port == transport.PlainRedirectPort)
        {
            if (context.Request.Path.Equals("/api/install-id", StringComparison.OrdinalIgnoreCase))
            {
                return next(context);
            }
            target = transport.WebPort;
        }
        else if (port == transport.TlsRedirectPort)
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
        var host = request.Host.HasValue ? request.Host.Host : request.HttpContext.Connection.LocalIpAddress?.ToString() ?? "localhost";
        if (host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('['))
        {
            host = $"[{host}]";
        }
        return $"{scheme}://{host}:{port}{request.PathBase}{request.Path}{request.QueryString}";
    }
}
