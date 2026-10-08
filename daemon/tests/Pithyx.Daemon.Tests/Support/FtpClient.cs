using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace Pithyx.Daemon.Tests.Support;

/// <summary>
/// A small FTP client: explicit TLS, passive data connections over EPSV, trusting the certificate whose fingerprint
/// Settings shows. Data connections use TLS after <c>PROT P</c> and none before or after <c>PROT C</c>.
/// </summary>
internal sealed class FtpClient : IAsyncDisposable
{
    public static string Pinned { get; set; } = "";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static bool Validate(object sender, System.Security.Cryptography.X509Certificates.X509Certificate? certificate, System.Security.Cryptography.X509Certificates.X509Chain? chain, SslPolicyErrors errors) =>
        certificate is not null
        && Pithyx.Daemon.FileServices.TlsCertificate.Fingerprint(new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate)) == Pinned;

    private readonly TcpClient _client;
    private Stream _stream;
    private StreamReader _reader;
    private bool _protected;

    private FtpClient(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
        _reader = new StreamReader(_stream, Encoding.UTF8);
    }

    public static async Task<FtpClient> ConnectAsync(int port)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, Token);
        var client = new FtpClient(tcp);
        Assert.StartsWith("220", await client.ReadReplyAsync(), StringComparison.Ordinal);
        return client;
    }

    public async Task StartTlsAsync()
    {
        Assert.StartsWith("234", await SendAsync("AUTH TLS"), StringComparison.Ordinal);
        var tls = new SslStream(_stream, false, Validate);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, Token);
        _stream = tls;
        _reader = new StreamReader(tls, Encoding.UTF8);
    }

    public async Task<string> SignInAsync(string user, string password)
    {
        await SendAsync($"USER {user}");
        return await SendAsync($"PASS {password}");
    }

    public async Task<string> SendAsync(string command)
    {
        await _stream.WriteAsync(Encoding.UTF8.GetBytes(command + "\r\n"), Token);
        await _stream.FlushAsync(Token);
        var reply = await ReadReplyAsync();
        if (command.StartsWith("PROT ", StringComparison.Ordinal) && reply.StartsWith("200", StringComparison.Ordinal))
        {
            _protected = command == "PROT P";
        }
        return reply;
    }

    public async Task<string?> TrySendAsync(string command)
    {
        try
        {
            var reply = await SendAsync(command);
            return reply.Length == 0 ? null : reply;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task<string> ReceiveDataAsync(string command)
    {
        await using var data = await OpenDataAsync();
        var preliminary = await SendAsync(command);
        Assert.StartsWith("150", preliminary, StringComparison.Ordinal);
        var stream = await SecureAsync(data);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = await reader.ReadToEndAsync(Token);
        Assert.StartsWith("226", await ReadReplyAsync(), StringComparison.Ordinal);
        return content;
    }

    public async Task<string> SendDataAsync(string command, string content)
    {
        await using var data = await OpenDataAsync();
        var preliminary = await SendAsync(command);
        if (!preliminary.StartsWith("150", StringComparison.Ordinal))
        {
            return preliminary;
        }
        try
        {
            await using var stream = await SecureAsync(data);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(content), Token);
            if (stream is SslStream tls)
            {
                await tls.ShutdownAsync();
            }
        }
        catch (Exception exception) when (exception is IOException or AuthenticationException)
        {
            // The server answers 150 before it checks the right and closes the data connection when it refuses;
            // its reply on the control connection still says why.
        }
        return await ReadReplyAsync();
    }

    private async Task<NetworkStream> OpenDataAsync()
    {
        var reply = await SendAsync("EPSV");
        Assert.StartsWith("229", reply, StringComparison.Ordinal);
        var port = int.Parse(reply.Split("|||")[1].Split('|')[0], System.Globalization.CultureInfo.InvariantCulture);
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, Token);
        return tcp.GetStream();
    }

    private async Task<Stream> SecureAsync(NetworkStream data)
    {
        if (!_protected)
        {
            return data;
        }
        var tls = new SslStream(data, false, Validate);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, Token);
        return tls;
    }

    /// <summary>A reply, with the lines of a multi-line reply joined.</summary>
    private async Task<string> ReadReplyAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var first = await _reader.ReadLineAsync(timeout.Token) ?? "";
        if (first.Length > 3 && first[3] == '-')
        {
            var text = new StringBuilder(first);
            string? line;
            while ((line = await _reader.ReadLineAsync(timeout.Token)) is not null)
            {
                text.Append('\n').Append(line);
                if (line.StartsWith(first[..3] + " ", StringComparison.Ordinal))
                {
                    break;
                }
            }
            return text.ToString();
        }
        return first;
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _client.Dispose();
    }
}
