using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Files;
using Pithyx.Daemon.Permissions;

namespace Pithyx.Daemon.FileServices.Ftp;

/// <summary>
/// One FTP client: the control connection, upgraded to TLS before anything but <c>AUTH</c>, <c>FEAT</c> and
/// <c>QUIT</c>, and one passive data connection at a time, with TLS and only from the control connection's address.
/// While <paramref name="plainAllowed"/> says so, plain FTP works too: without <c>AUTH TLS</c> and with
/// <c>PROT C</c>. The rights and the plain FTP setting are checked again before every command.
/// </summary>
public sealed partial class FtpSession(
    TcpClient client,
    X509Certificate2 certificate,
    ProtocolSignIn signIn,
    ProtocolFiles files,
    (int First, int Last) passivePorts,
    Func<bool> plainAllowed,
    TimeProvider time,
    ILogger<FtpSession> logger) : IDisposable
{
    public const int MaxLine = 4096;
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DataTimeout = TimeSpan.FromSeconds(30);
    private const string Protocol = Resources.Ftp;

    private readonly CancellationTokenSource _closing = new();
    private Stream _control = client.GetStream();
    private readonly byte[] _buffer = new byte[MaxLine];
    private int _buffered;
    private bool _tls;
    private bool _protected;
    private string? _username;
    private ClaimsPrincipal? _user;
    private string _current = "/";
    private TcpListener? _passive;
    private long _restart;
    private string? _renameFrom;

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Whether the control connection is still without TLS.</summary>
    public bool Plain => !_tls;

    private IPAddress RemoteAddress => Normalize(((IPEndPoint)client.Client.RemoteEndPoint!).Address);

    private IPAddress LocalAddress => Normalize(((IPEndPoint)client.Client.LocalEndPoint!).Address);

    public void Close() => _closing.Cancel();

    public void Dispose()
    {
        _passive?.Stop();
        _control.Dispose();
        client.Dispose();
        _closing.Dispose();
    }

    public async Task RunAsync(CancellationToken stopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, _closing.Token);
        var cancellationToken = linked.Token;
        try
        {
            await ReplyAsync(plainAllowed() ? "220 Pithyx FTP." : "220 Pithyx FTP. Only encrypted connections: use AUTH TLS.", cancellationToken);
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(IdleTimeout);
                var line = await ReadLineAsync(idle.Token);
                if (line is null)
                {
                    return;
                }
                var (verb, argument) = FtpFormat.Parse(line);
                if (!await HandleAsync(verb, argument, cancellationToken))
                {
                    return;
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or AuthenticationException or ObjectDisposedException)
        {
            // The client went away, the session idled out or Pithyx closed it.
        }
    }

    /// <summary>Handles one command; false ends the session.</summary>
    private async Task<bool> HandleAsync(string verb, string argument, CancellationToken cancellationToken)
    {
        switch (verb)
        {
            case "AUTH":
                if (_tls)
                {
                    await ReplyAsync("503 TLS is already on.", cancellationToken);
                    return true;
                }
                if (!argument.Equals("TLS", StringComparison.OrdinalIgnoreCase) && !argument.Equals("SSL", StringComparison.OrdinalIgnoreCase))
                {
                    await ReplyAsync("504 Only AUTH TLS.", cancellationToken);
                    return true;
                }
                await ReplyAsync("234 Starting TLS.", cancellationToken);
                if (_buffered > 0)
                {
                    // Plain text sent behind AUTH could be injected; refuse it.
                    return false;
                }
                var tls = new SslStream(_control, leaveInnerStreamOpen: false);
                await tls.AuthenticateAsServerAsync(Options(), cancellationToken);
                _control = tls;
                _tls = true;
                return true;
            case "FEAT":
                await ReplyAsync("211-Features:\r\n AUTH TLS\r\n PBSZ\r\n PROT\r\n UTF8\r\n EPSV\r\n PASV\r\n SIZE\r\n MDTM\r\n REST STREAM\r\n MLST type*;size*;modify*;\r\n211 End", cancellationToken);
                return true;
            case "QUIT":
                await ReplyAsync("221 Goodbye.", cancellationToken);
                return false;
            case "NOOP":
                await ReplyAsync("200 OK.", cancellationToken);
                return true;
        }
        if (!_tls && !plainAllowed())
        {
            await ReplyAsync("530 Only encrypted connections: use AUTH TLS first.", cancellationToken);
            return _user is null;
        }
        switch (verb)
        {
            case "PBSZ":
                await ReplyAsync("200 PBSZ=0", cancellationToken);
                return true;
            case "PROT":
                if (argument.Equals("P", StringComparison.OrdinalIgnoreCase))
                {
                    _protected = _tls;
                    await ReplyAsync(_tls ? "200 Data connections are protected." : "503 AUTH TLS first.", cancellationToken);
                }
                else if (argument.Equals("C", StringComparison.OrdinalIgnoreCase) && plainAllowed())
                {
                    _protected = false;
                    await ReplyAsync("200 Data connections are not protected.", cancellationToken);
                }
                else
                {
                    await ReplyAsync("536 Only PROT P.", cancellationToken);
                }
                return true;
            case "USER":
                _username = argument;
                _user = null;
                await ReplyAsync("331 Password, please.", cancellationToken);
                return true;
            case "PASS":
                if (_username is null)
                {
                    await ReplyAsync("503 USER first.", cancellationToken);
                    return true;
                }
                try
                {
                    _user = await signIn.SignInAsync(_username, argument, Protocol, RemoteAddress.ToString(), cancellationToken);
                }
                catch (PithyxException exception) when (exception.Code == ErrorCodes.AuthRateLimited)
                {
                    _user = null;
                }
                if (_user is null)
                {
                    LogSignInFailed(_username, RemoteAddress);
                    await ReplyAsync("530 Sign-in refused.", cancellationToken);
                    return true;
                }
                _current = "/";
                LogSignedIn(_username, RemoteAddress);
                await ReplyAsync("230 Signed in.", cancellationToken);
                return true;
        }
        if (_user is null)
        {
            await ReplyAsync("530 Sign in first.", cancellationToken);
            return true;
        }
        _user = signIn.Refresh(_user, Protocol);
        if (_user is null)
        {
            await ReplyAsync("421 You may no longer use FTP.", cancellationToken);
            return false;
        }
        try
        {
            await CommandAsync(_user, verb, argument, cancellationToken);
        }
        catch (PithyxException exception)
        {
            await ReplyAsync($"550 {Text(exception.Code)}", cancellationToken);
        }
        return true;
    }

    private async Task CommandAsync(ClaimsPrincipal user, string verb, string argument, CancellationToken cancellationToken)
    {
        switch (verb)
        {
            case "SYST":
                await ReplyAsync("215 UNIX Type: L8", cancellationToken);
                return;
            case "OPTS":
                await ReplyAsync(argument.StartsWith("UTF8", StringComparison.OrdinalIgnoreCase) ? "200 UTF8 is always on." : "501 Unknown option.", cancellationToken);
                return;
            case "TYPE":
            case "MODE":
            case "STRU":
                await ReplyAsync("200 OK.", cancellationToken);
                return;
            case "PWD":
            case "XPWD":
                await ReplyAsync($"257 {FtpFormat.Quote(_current)} is the current folder.", cancellationToken);
                return;
            case "CWD":
            case "CDUP":
                {
                    var target = FtpFormat.Combine(_current, verb == "CDUP" ? ".." : argument);
                    var entry = await files.StatAsync(user, Protocol, await ResolveAsync(user, target, cancellationToken), cancellationToken);
                    if (entry.Kind != FileKind.Folder)
                    {
                        await ReplyAsync("550 Not a folder.", cancellationToken);
                        return;
                    }
                    _current = target;
                    await ReplyAsync("250 OK.", cancellationToken);
                    return;
                }
            case "PASV":
            case "EPSV":
                await OpenPassiveAsync(extended: verb == "EPSV", cancellationToken);
                return;
            case "PORT":
            case "EPRT":
                await ReplyAsync("502 Only passive mode (PASV, EPSV).", cancellationToken);
                return;
            case "LIST":
            case "NLST":
            case "MLSD":
                {
                    // Options such as -la are not paths.
                    var argumentPath = argument.StartsWith('-') ? "" : argument;
                    var path = await ResolveAsync(user, FtpFormat.Combine(_current, argumentPath), cancellationToken);
                    var entries = (await files.ListAsync(user, Protocol, path, cancellationToken)).Where(entry => entry.Kind is FileKind.File or FileKind.Folder).ToList();
                    var now = time.GetUtcNow();
                    var lines = verb switch
                    {
                        "NLST" => entries.Select(entry => entry.Name),
                        "MLSD" => entries.Select(entry => FtpFormat.Facts(entry, entry.Name)),
                        _ => entries.Select(entry => FtpFormat.ListLine(entry, now)),
                    };
                    await SendDataAsync(Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line + "\r\n"))), cancellationToken);
                    return;
                }
            case "MLST":
                {
                    var target = FtpFormat.Combine(_current, argument);
                    var entry = await files.StatAsync(user, Protocol, await ResolveAsync(user, target, cancellationToken), cancellationToken);
                    await ReplyAsync($"250-Listing {target}\r\n {FtpFormat.Facts(entry, target)}\r\n250 End", cancellationToken);
                    return;
                }
            case "SIZE":
            case "MDTM":
                {
                    var entry = await files.StatAsync(user, Protocol, await ResolveAsync(user, FtpFormat.Combine(_current, argument), cancellationToken), cancellationToken);
                    if (entry.Kind != FileKind.File)
                    {
                        await ReplyAsync("550 Not a file.", cancellationToken);
                        return;
                    }
                    await ReplyAsync(verb == "SIZE" ? $"213 {entry.Size.ToString(CultureInfo.InvariantCulture)}" : $"213 {FtpFormat.Time(entry.Modified)}", cancellationToken);
                    return;
                }
            case "REST":
                _restart = long.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ? offset : 0;
                await ReplyAsync($"350 Restarting at {_restart.ToString(CultureInfo.InvariantCulture)}.", cancellationToken);
                return;
            case "RETR":
                await RetrieveAsync(user, argument, cancellationToken);
                return;
            case "STOR":
                await StoreAsync(user, argument, cancellationToken);
                return;
            case "APPE":
                await ReplyAsync("502 Appending is not offered.", cancellationToken);
                return;
            case "DELE":
            case "RMD":
            case "XRMD":
                {
                    var path = await ResolveAsync(user, FtpFormat.Combine(_current, argument), cancellationToken);
                    var entry = await files.StatAsync(user, Protocol, path, cancellationToken);
                    if ((entry.Kind == FileKind.Folder) != (verb != "DELE"))
                    {
                        await ReplyAsync(verb == "DELE" ? "550 Not a file." : "550 Not a folder.", cancellationToken);
                        return;
                    }
                    await files.DeleteAsync(user, path, cancellationToken);
                    await ReplyAsync("250 Moved to the recycle bin.", cancellationToken);
                    return;
                }
            case "MKD":
            case "XMKD":
                {
                    var target = FtpFormat.Combine(_current, argument);
                    await files.MakeFolderAsync(user, await ResolveAsync(user, target, cancellationToken), cancellationToken);
                    await ReplyAsync($"257 {FtpFormat.Quote(target)} created.", cancellationToken);
                    return;
                }
            case "RNFR":
                {
                    var source = FtpFormat.Combine(_current, argument);
                    _ = await files.StatAsync(user, Protocol, await ResolveAsync(user, source, cancellationToken), cancellationToken);
                    _renameFrom = source;
                    await ReplyAsync("350 Ready for RNTO.", cancellationToken);
                    return;
                }
            case "RNTO":
                {
                    if (_renameFrom is not { } from)
                    {
                        await ReplyAsync("503 RNFR first.", cancellationToken);
                        return;
                    }
                    _renameFrom = null;
                    var source = await ResolveAsync(user, from, cancellationToken);
                    var target = await ResolveAsync(user, FtpFormat.Combine(_current, argument), cancellationToken);
                    await files.TransferAsync(user, source, target, move: true, overwrite: false, cancellationToken);
                    await ReplyAsync("250 Renamed.", cancellationToken);
                    return;
                }
            case "ABOR":
                await ReplyAsync("226 Nothing to abort.", cancellationToken);
                return;
            case "STAT":
                await ReplyAsync("211 Pithyx FTP, signed in.", cancellationToken);
                return;
            case "HELP":
                await ReplyAsync("214 See FEAT.", cancellationToken);
                return;
            default:
                await ReplyAsync("502 Not offered.", cancellationToken);
                return;
        }
    }

    private Task<ProtocolPath> ResolveAsync(ClaimsPrincipal user, string path, CancellationToken cancellationToken) =>
        files.ResolveAsync(user, Protocol, path, cancellationToken);

    private async Task RetrieveAsync(ClaimsPrincipal user, string argument, CancellationToken cancellationToken)
    {
        var path = await ResolveAsync(user, FtpFormat.Combine(_current, argument), cancellationToken);
        var (stream, _, _) = await files.OpenReadAsync(user, path, cancellationToken);
        await using (stream)
        {
            var offset = _restart;
            _restart = 0;
            if (offset > 0)
            {
                stream.Seek(offset, SeekOrigin.Begin);
            }
            await TransferAsync(async data => await stream.CopyToAsync(data, cancellationToken), cancellationToken);
        }
    }

    private async Task StoreAsync(ClaimsPrincipal user, string argument, CancellationToken cancellationToken)
    {
        var path = await ResolveAsync(user, FtpFormat.Combine(_current, argument), cancellationToken);
        if (_restart != 0)
        {
            _restart = 0;
            await ReplyAsync("502 Resuming an upload is not offered.", cancellationToken);
            return;
        }
        PithyxException? refused = null;
        await TransferAsync(async data =>
        {
            try
            {
                await files.WriteAsync(user, path, data, null, overwrite: true, cancellationToken);
            }
            catch (PithyxException exception)
            {
                refused = exception;
            }
        }, cancellationToken, () => refused);
    }

    private async Task SendDataAsync(byte[] content, CancellationToken cancellationToken) =>
        await TransferAsync(async data => await data.WriteAsync(content, cancellationToken), cancellationToken);

    /// <summary>Accepts the data connection of the last PASV or EPSV, protects it with TLS and runs one transfer over it.</summary>
    private async Task TransferAsync(Func<Stream, Task> transfer, CancellationToken cancellationToken, Func<PithyxException?>? failed = null)
    {
        if (!_protected && !plainAllowed())
        {
            await ReplyAsync("521 Use PROT P first.", cancellationToken);
            return;
        }
        if (_passive is not { } listener)
        {
            await ReplyAsync("425 Use PASV or EPSV first.", cancellationToken);
            return;
        }
        _passive = null;
        try
        {
            await ReplyAsync("150 Opening the data connection.", cancellationToken);
            using var accepting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            accepting.CancelAfter(DataTimeout);
            using var socket = await listener.AcceptTcpClientAsync(accepting.Token);
            // A data connection from anywhere else could steal or replace the file.
            if (!Normalize(((IPEndPoint)socket.Client.RemoteEndPoint!).Address).Equals(RemoteAddress))
            {
                await ReplyAsync("425 The data connection came from another address.", cancellationToken);
                return;
            }
            if (!_protected)
            {
                // Plain FTP, which an administrator switched on at the users' own risk.
                await using var plain = socket.GetStream();
                await transfer(plain);
            }
            else
            {
                await using var data = new SslStream(socket.GetStream(), leaveInnerStreamOpen: false);
                await data.AuthenticateAsServerAsync(Options(), accepting.Token);
                await transfer(data);
                try
                {
                    await data.ShutdownAsync();
                }
                catch (IOException)
                {
                    // The client closed first, as it does after an upload.
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await ReplyAsync("425 No data connection came.", cancellationToken);
            return;
        }
        catch (Exception exception) when (exception is IOException or AuthenticationException or SocketException)
        {
            LogTransferFailed(exception);
            await ReplyAsync("426 The data connection broke.", cancellationToken);
            return;
        }
        finally
        {
            listener.Stop();
        }
        if (failed?.Invoke() is { } refused)
        {
            await ReplyAsync($"550 {Text(refused.Code)}", cancellationToken);
            return;
        }
        await ReplyAsync("226 Done.", cancellationToken);
    }

    private async Task OpenPassiveAsync(bool extended, CancellationToken cancellationToken)
    {
        _passive?.Stop();
        _passive = null;
        var (first, last) = passivePorts;
        for (var port = first; port <= last; port++)
        {
            var listener = new TcpListener(LocalAddress, port);
            try
            {
                listener.Start(1);
            }
            catch (SocketException)
            {
                // Taken by another session; the next one.
                continue;
            }
            _passive = listener;
            var bound = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (extended)
            {
                await ReplyAsync(FtpFormat.ExtendedPassiveReply(bound), cancellationToken);
            }
            else if (LocalAddress.AddressFamily == AddressFamily.InterNetwork)
            {
                await ReplyAsync(FtpFormat.PassiveReply(LocalAddress, bound), cancellationToken);
            }
            else
            {
                await ReplyAsync("522 Use EPSV over IPv6.", cancellationToken);
            }
            return;
        }
        await ReplyAsync("425 No free data port.", cancellationToken);
    }

    private SslServerAuthenticationOptions Options() => new()
    {
        ServerCertificate = certificate,
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        ClientCertificateRequired = false,
    };

    private async Task ReplyAsync(string reply, CancellationToken cancellationToken)
    {
        await _control.WriteAsync(Encoding.UTF8.GetBytes(reply + "\r\n"), cancellationToken);
        await _control.FlushAsync(cancellationToken);
    }

    /// <summary>The next command line, or null when the client closed the connection. Lines longer than <see cref="MaxLine"/> end the session.</summary>
    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var end = Array.IndexOf(_buffer, (byte)'\n', 0, _buffered);
            if (end >= 0)
            {
                var line = Encoding.UTF8.GetString(_buffer, 0, end).TrimEnd('\r');
                Buffer.BlockCopy(_buffer, end + 1, _buffer, 0, _buffered - end - 1);
                _buffered -= end + 1;
                return line;
            }
            if (_buffered == _buffer.Length)
            {
                return null;
            }
            var read = await _control.ReadAsync(_buffer.AsMemory(_buffered), cancellationToken);
            if (read == 0)
            {
                return null;
            }
            _buffered += read;
        }
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>A short English reason for a refused command; clients show it as it is.</summary>
    public static string Text(string code) => code switch
    {
        ErrorCodes.FsShareNotFound or ErrorCodes.FsEntryNotFound => "Not found.",
        ErrorCodes.FsShareReadOnly or ErrorCodes.FsEntryAccessDenied => "Read only for you.",
        ErrorCodes.FsEntryExists => "The name is taken.",
        ErrorCodes.FsPathInvalid or ErrorCodes.FsNameInvalid => "Not possible here.",
        ErrorCodes.FsShareUnavailable => "The shared folder is not available now.",
        ErrorCodes.FsEntryIsFolder => "That is a folder.",
        _ => $"Refused ({code}).",
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "FTP user {User} signed in from {Address}")]
    private partial void LogSignedIn(string user, IPAddress address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "FTP sign-in of {User} from {Address} refused")]
    private partial void LogSignInFailed(string user, IPAddress address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An FTP data connection broke")]
    private partial void LogTransferFailed(Exception exception);
}
