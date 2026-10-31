using System.Net.Sockets;
using System.Text;

namespace Pithyx.Daemon.Ups;

/// <summary>A NUT server refused or did not answer; <see cref="Problem"/> is the word of <see cref="UpsState.Problem"/>.</summary>
public sealed class NutException(string problem) : Exception(problem)
{
    public string Problem { get; } = problem;
}

/// <summary>
/// One connection to a NUT server (<c>upsd</c>). With a user it signs in and logs in to the UPS as a secondary, so the
/// server's primary waits for the box before it cuts the power; then it reads the UPS's variables on request.
/// </summary>
public sealed class NutClient : IAsyncDisposable
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    private readonly TcpClient _tcp;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    private NutClient(TcpClient tcp)
    {
        _tcp = tcp;
        var stream = tcp.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
    }

    public static async Task<NutClient> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient();
        try
        {
            using var timeout = Timeout(cancellationToken);
            await tcp.ConnectAsync(host, port, timeout.Token);
            return new NutClient(tcp);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new NutException("timeout");
        }
        catch (SocketException)
        {
            tcp.Dispose();
            throw new NutException("refused");
        }
    }

    /// <summary>Signs in and logs in to the UPS; a wrong user or password is <c>access_denied</c>.</summary>
    public async Task LoginAsync(string username, string password, string ups, CancellationToken cancellationToken)
    {
        await CommandAsync($"USERNAME {username}", cancellationToken);
        if (password.Length > 0)
        {
            await CommandAsync($"PASSWORD {password}", cancellationToken);
        }
        await CommandAsync($"LOGIN {ups}", cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, string>> ListVarsAsync(string ups, CancellationToken cancellationToken)
    {
        using var timeout = Timeout(cancellationToken);
        var lines = new List<string>();
        await SendAsync($"LIST VAR {ups}", timeout.Token);
        var first = await ReadLineAsync(timeout.Token);
        if (first != $"BEGIN LIST VAR {ups}")
        {
            throw new NutException(NutProtocol.ProblemOf(first) ?? "protocol");
        }
        while (await ReadLineAsync(timeout.Token) is var line && line != $"END LIST VAR {ups}")
        {
            lines.Add(line);
        }
        return NutProtocol.ParseVars(lines);
    }

    public async ValueTask DisposeAsync()
    {
        if (_tcp.Connected)
        {
            try
            {
                await _writer.WriteLineAsync("LOGOUT");
            }
            catch (IOException)
            {
                // The server is gone already.
            }
        }
        _tcp.Dispose();
    }

    private async Task CommandAsync(string command, CancellationToken cancellationToken)
    {
        using var timeout = Timeout(cancellationToken);
        await SendAsync(command, timeout.Token);
        var answer = await ReadLineAsync(timeout.Token);
        if (!answer.StartsWith("OK", StringComparison.Ordinal))
        {
            throw new NutException(NutProtocol.ProblemOf(answer) ?? "protocol");
        }
    }

    private async Task SendAsync(string line, CancellationToken cancellationToken)
    {
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken);
        }
        catch (IOException)
        {
            throw new NutException("refused");
        }
    }

    private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _reader.ReadLineAsync(cancellationToken) ?? throw new NutException("refused");
        }
        catch (IOException)
        {
            throw new NutException("refused");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new NutException("timeout");
        }
    }

    private static CancellationTokenSource Timeout(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(_timeout);
        return source;
    }
}
