using System.Globalization;
using System.Text;
using Dapper;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.FileServices.Ftp;
using Pithyx.Daemon.Hosting;

namespace Pithyx.Daemon.Network;

/// <summary>Which file services the firewall lets in, from the file service settings.</summary>
/// <param name="Smb">SMB is switched on (TCP 445).</param>
/// <param name="Ssh">SFTP or SSH logins are switched on (TCP 22).</param>
/// <param name="Ftp">FTP is switched on (TCP 21 and the passive ports).</param>
/// <param name="PrivateNetworksOnly">The file services answer only sources in private networks (RFC 1918, ULA, link-local).</param>
/// <param name="TcpStreams">The TCP ports of the reverse proxy's enabled streams (D347), open to every source.</param>
/// <param name="UdpStreams">The UDP ports of the enabled streams.</param>
public sealed record FirewallPlan(bool Smb, bool Ssh, bool Ftp, bool PrivateNetworksOnly, IReadOnlyList<int>? TcpStreams = null, IReadOnlyList<int>? UdpStreams = null);

/// <summary>
/// The box's firewall (D342): the chain <c>input</c> of the table <c>inet pithyx</c> with policy drop, next to the
/// <c>apps-input</c> chain of <see cref="Apps.Docker.DockerHost"/>. It lets in loopback, answers, the ICMP and ICMPv6
/// messages IPv4 and IPv6 need, DHCP replies, the web ports, only the file services that are switched on (WebDAV
/// goes through the web port) and the ports of the reverse proxy's enabled streams. One <c>nft -f -</c> replaces the rules in a single transaction whenever a service is
/// switched. On a development machine and in simulation the rules are only rendered, so the machine a session runs on
/// never firewalls its own management interface.
/// </summary>
public sealed partial class Firewall(
    SqliteConnectionFactory connections,
    ICommandRunner commands,
    DaemonMode mode,
    DeploymentEnvironment environment,
    ILogger<Firewall> logger) : BackgroundService
{
    public const string Chain = "input";
    public const int SmbPort = 445;
    public const int SshPort = 22;
    public const int FtpPort = 21;

    /// <summary>The ports of the web interface and the reverse proxy: 80 and 443 (D347) and 7484 (D346).</summary>
    public static IReadOnlyList<int> WebPorts { get; } = [80, 443, 7484];

    public static IReadOnlyList<string> PrivateIpv4 { get; } = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16"];

    public static IReadOnlyList<string> PrivateIpv6 { get; } = ["fc00::/7", "fe80::/10"];

    private static readonly string[] _icmp = ["echo-request", "destination-unreachable", "time-exceeded", "parameter-problem"];

    // Neighbor discovery and MLD are what IPv6 needs to work at all; the rest is what path MTU and errors need.
    private static readonly string[] _icmpv6 =
    [
        "destination-unreachable", "packet-too-big", "time-exceeded", "parameter-problem", "echo-request",
        "nd-router-advert", "nd-neighbor-solicit", "nd-neighbor-advert", "nd-redirect", "mld-listener-query",
    ];

    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Whether the rules reach the host: a box in real mode, never a development machine.</summary>
    public bool Applies => mode == DaemonMode.Real && environment.Kind == DeploymentKind.Prod;

    /// <summary>The rules of the last run, applied or not.</summary>
    public string? Rules { get; private set; }

    /// <summary>The TCP ports of the switched-on file services, as nft writes them.</summary>
    public static IReadOnlyList<string> FileServicePorts(FirewallPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var ports = new List<string>();
        if (plan.Ftp)
        {
            ports.Add(FtpPort.ToString(CultureInfo.InvariantCulture));
        }
        if (plan.Ssh)
        {
            ports.Add(SshPort.ToString(CultureInfo.InvariantCulture));
        }
        if (plan.Smb)
        {
            ports.Add(SmbPort.ToString(CultureInfo.InvariantCulture));
        }
        if (plan.Ftp)
        {
            ports.Add(string.Create(CultureInfo.InvariantCulture, $"{FtpServer.DefaultPassivePorts.First}-{FtpServer.DefaultPassivePorts.Last}"));
        }
        return ports;
    }

    /// <summary>The nftables script for <paramref name="plan"/>.</summary>
    public static string Render(FirewallPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        const string Rule = $"add rule inet pithyx {Chain}";
        var text = new StringBuilder()
            .Append("add table inet pithyx\n")
            .Append(CultureInfo.InvariantCulture, $"add chain inet pithyx {Chain} {{ type filter hook input priority filter; policy drop; }}\n")
            .Append(CultureInfo.InvariantCulture, $"flush chain inet pithyx {Chain}\n")
            .Append(CultureInfo.InvariantCulture, $"{Rule} iif \"lo\" accept\n")
            .Append(CultureInfo.InvariantCulture, $"{Rule} ct state established,related accept\n")
            .Append(CultureInfo.InvariantCulture, $"{Rule} icmp type {{ {string.Join(", ", _icmp)} }} accept\n")
            .Append(CultureInfo.InvariantCulture, $"{Rule} icmpv6 type {{ {string.Join(", ", _icmpv6)} }} accept\n")
            .Append(CultureInfo.InvariantCulture, $"{Rule} udp sport 67 udp dport 68 accept\n")
            .Append(CultureInfo.InvariantCulture, $"{Rule} ip6 saddr fe80::/10 udp sport 547 udp dport 546 accept\n")
            .Append(CultureInfo.InvariantCulture, $"{Rule} tcp dport {{ {string.Join(", ", WebPorts)} }} accept\n");
        var ports = FileServicePorts(plan);
        if (ports.Count > 0)
        {
            var accept = $"tcp dport {{ {string.Join(", ", ports)} }} accept";
            if (plan.PrivateNetworksOnly)
            {
                text.Append(CultureInfo.InvariantCulture, $"{Rule} ip saddr {{ {string.Join(", ", PrivateIpv4)} }} {accept}\n")
                    .Append(CultureInfo.InvariantCulture, $"{Rule} ip6 saddr {{ {string.Join(", ", PrivateIpv6)} }} {accept}\n");
            }
            else
            {
                text.Append(CultureInfo.InvariantCulture, $"{Rule} {accept}\n");
            }
        }
        if (plan.TcpStreams is { Count: > 0 } tcp)
        {
            text.Append(CultureInfo.InvariantCulture, $"{Rule} tcp dport {{ {string.Join(", ", tcp.Order())} }} accept\n");
        }
        if (plan.UdpStreams is { Count: > 0 } udp)
        {
            text.Append(CultureInfo.InvariantCulture, $"{Rule} udp dport {{ {string.Join(", ", udp.Order())} }} accept\n");
        }
        return text.ToString();
    }

    /// <summary>The plan from the stored file service settings and the enabled streams.</summary>
    public async Task<FirewallPlan> WantedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<(long Smb, long Sftp, long Ssh, long Ftp, long PrivateOnly)>(
            "SELECT smb_enabled, sftp_enabled, ssh_enabled, ftp_enabled, private_networks_only FROM file_services WHERE id = 1");
        var streams = (await connection.QueryAsync<(long Port, long Tcp, long Udp)>("SELECT port, tcp, udp FROM proxy_streams WHERE enabled = 1")).ToList();
        return new FirewallPlan(
            row.Smb == 1,
            row.Sftp == 1 || row.Ssh == 1,
            row.Ftp == 1,
            row.PrivateOnly == 1,
            [.. streams.Where(stream => stream.Tcp == 1).Select(stream => (int)stream.Port).Distinct()],
            [.. streams.Where(stream => stream.Udp == 1).Select(stream => (int)stream.Port).Distinct()]);
    }

    /// <summary>Writes the rules for the stored settings; on a development machine and in simulation it only renders them.</summary>
    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var plan = await WantedAsync(cancellationToken);
            Rules = Render(plan);
            var ports = string.Join(", ", FileServicePorts(plan));
            if (!Applies)
            {
                if (mode == DaemonMode.Real)
                {
                    LogSkipped(ports);
                }
                return;
            }
            var result = await commands.RunAsync(new CommandRequest("nft", ["-f", "-"], StandardInput: Rules), cancellationToken);
            if (result.ExitCode != 0)
            {
                LogRefused(result.ExitCode);
                throw new PithyxException(ErrorCodes.NetworkFirewallFailed);
            }
            LogApplied(ports, plan.PrivateNetworksOnly);
        }
        finally
        {
            _lock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ApplyAsync(stoppingToken);
        }
        catch (PithyxException)
        {
            // Logged; the next switch of a service tries again.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A missing or crashing nft must not stop the daemon; the next switch of a service tries again.
            LogFailed(exception);
        }
    }

    public override void Dispose()
    {
        _lock.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Firewall applied; file service ports open: {Ports}; private networks only: {PrivateOnly}")]
    private partial void LogApplied(string ports, bool privateOnly);

    [LoggerMessage(Level = LogLevel.Information, Message = "Firewall not applied on a development machine; file service ports it would open: {Ports}")]
    private partial void LogSkipped(string ports);

    [LoggerMessage(Level = LogLevel.Error, Message = "The firewall could not be applied")]
    private partial void LogFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "nft refused the firewall rules (exit {ExitCode}); the command log has its output")]
    private partial void LogRefused(int exitCode);
}
