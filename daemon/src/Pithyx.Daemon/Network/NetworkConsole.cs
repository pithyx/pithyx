using System.Globalization;
using System.Text;
using System.Text.Json;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Network;

/// <summary>A request of the console menu (<c>/usr/lib/pithyx/console.sh</c> on tty1).</summary>
/// <param name="Action"><c>static</c>, <c>reset</c>, <c>keep</c> or <c>undo</c>.</param>
/// <param name="Interface">For static: the card.</param>
/// <param name="Address">For static: IPv4 address with prefix length.</param>
/// <param name="Gateway">For static: the gateway, optional.</param>
/// <param name="Dns">For static: name servers, optional.</param>
public sealed record ConsoleNetworkRequest(string Action, string? Interface = null, string? Address = null, string? Gateway = null, IReadOnlyList<string>? Dns = null);

/// <summary>
/// The daemon's side of the console menu on tty1 (D337): whoever sits at the box may set a static address (with the
/// confirmation timer, kept or undone on the console or in the browser) and reset the network to DHCP, without a
/// password. The menu runs as root and talks through files in <c>/run/pithyx/console</c>, which only root may write:
/// it writes <c>network-request</c> (JSON), the daemon answers in <c>network-result</c> (<c>ok</c> or the error code)
/// and keeps <c>network-pending</c> (the waiting change) and <c>network-cards</c> (the cards it may change) current.
/// </summary>
public sealed partial class NetworkConsole(NetworkService network, NetworkHost host, ILogger<NetworkConsole> logger) : BackgroundService
{
    public const string Directory = "/run/pithyx/console";
    public const string RequestFile = "network-request";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Only root may create it: a daemon without root (a test, a checkout) has no console menu.
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            LogUnavailable(exception);
            return;
        }
        var requests = System.Threading.Channels.Channel.CreateBounded<bool>(1);
        using var watcher = new FileSystemWatcher(Directory, RequestFile) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
        watcher.Created += (_, _) => requests.Writer.TryWrite(true);
        watcher.Changed += (_, _) => requests.Writer.TryWrite(true);
        watcher.Renamed += (_, _) => requests.Writer.TryWrite(true);
        watcher.EnableRaisingEvents = true;
        network.Changed += Write;
        try
        {
            Write(await network.StateAsync(null, stoppingToken));
            requests.Writer.TryWrite(true);
            await foreach (var _ in requests.Reader.ReadAllAsync(stoppingToken))
            {
                await HandleAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            network.Changed -= Write;
        }
    }

    private async Task HandleAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(Directory, RequestFile);
        if (!File.Exists(path))
        {
            return;
        }
        string answer;
        try
        {
            var request = JsonSerializer.Deserialize<ConsoleNetworkRequest>(await File.ReadAllTextAsync(path, cancellationToken), _json);
            File.Delete(path);
            await RunAsync(request ?? throw new PithyxException(ErrorCodes.RequestBodyInvalid), cancellationToken);
            answer = "ok";
        }
        catch (PithyxException exception)
        {
            answer = exception.Code;
        }
        catch (JsonException)
        {
            File.Delete(path);
            answer = ErrorCodes.RequestBodyInvalid;
        }
        LogRequest(answer);
        await WriteAtomicAsync("network-result", answer + "\n", cancellationToken);
    }

    private async Task RunAsync(ConsoleNetworkRequest request, CancellationToken cancellationToken)
    {
        switch (request.Action)
        {
            case "reset":
                await network.ResetAsync(cancellationToken);
                break;
            case "keep" or "undo":
                var pending = (await network.StateAsync(null, cancellationToken)).Pending ?? throw new PithyxException(ErrorCodes.NetworkChangeNotFound);
                await (request.Action == "keep" ? network.ConfirmAsync(pending.Id, cancellationToken) : network.RevertAsync(pending.Id, cancellationToken));
                break;
            case "static":
                var current = await network.ConfigAsync(cancellationToken);
                var name = request.Interface ?? "";
                var previous = current.Interfaces.FirstOrDefault(item => item.Name == name);
                var item = (previous ?? new InterfaceConfig(name, InterfaceKind.Ethernet)) with
                {
                    Enabled = true,
                    Ipv4 = Ipv4Mode.Static,
                    Ipv4Address = request.Address,
                    Ipv4Gateway = request.Gateway,
                };
                var global = request.Dns is { Count: > 0 } dns && host.Policy.GlobalChangeable ? current.Global with { DnsServers = dns } : current.Global;
                await network.ChangeAsync(new NetworkConfig([.. current.Interfaces.Where(other => other.Name != name), item], global), NetworkChangeOrigin.Console, cancellationToken);
                break;
            default:
                throw new PithyxException(ErrorCodes.RequestBodyInvalid);
        }
    }

    /// <summary>What the menu shows: the waiting change and the cards, as lines of key=value and name per line.</summary>
    private void Write(NetworkState state)
    {
        var pending = new StringBuilder();
        if (state.Pending is { } change)
        {
            pending.Append(CultureInfo.InvariantCulture, $"deadline={change.Deadline.ToUnixTimeSeconds()}\n");
            pending.Append(CultureInfo.InvariantCulture, $"addresses={string.Join(' ', change.Addresses)}\n");
            pending.Append(CultureInfo.InvariantCulture, $"origin={(change.Origin == NetworkChangeOrigin.Console ? "console" : "web")}\n");
        }
        var cards = string.Concat(state.Links
            .Where(link => link.Changeable && link.Master is null)
            .Select(link => $"{link.Name} {(link.Carrier ? "connected" : "disconnected")} {string.Join(',', link.Addresses)}\n"));
        _ = Task.Run(async () =>
        {
            try
            {
                await WriteAtomicAsync("network-pending", pending.ToString(), CancellationToken.None);
                await WriteAtomicAsync("network-cards", cards, CancellationToken.None);
            }
            catch (IOException exception)
            {
                LogWriteFailed(exception);
            }
        });
    }

    private static async Task WriteAtomicAsync(string name, string content, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Directory, name);
        var candidate = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(candidate, content, cancellationToken);
        File.Move(candidate, path, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Network request from the console: {Answer}")]
    private partial void LogRequest(string answer);

    [LoggerMessage(Level = LogLevel.Information, Message = "No console menu: the console directory cannot be created")]
    private partial void LogUnavailable(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Writing the network state for the console failed")]
    private partial void LogWriteFailed(Exception exception);
}
