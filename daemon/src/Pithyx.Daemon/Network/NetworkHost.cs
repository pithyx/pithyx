using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.SystemSettings;

namespace Pithyx.Daemon.Network;

/// <summary>
/// The host side of the network: reads the interfaces from sysfs, procfs and networkd's state files (no process for a
/// read), writes Pithyx's networkd and resolved files and has networkd apply them, and sets the host name. Every
/// command goes through <see cref="ICommandRunner"/>. In simulation everything happens in <see cref="SimulatedNetwork"/>.
/// </summary>
public sealed partial class NetworkHost(ICommandRunner commands, DaemonMode mode, DeploymentEnvironment environment, ILogger<NetworkHost> logger)
{
    private const string SysClassNet = "/sys/class/net";
    private static readonly TimeSpan _commandTimeout = TimeSpan.FromSeconds(30);

    private readonly SimulatedNetwork? _simulated = mode == DaemonMode.Simulated ? new SimulatedNetwork() : null;

    public bool Simulated => _simulated is not null;

    /// <summary>The cards a development machine lets Pithyx change.</summary>
    public IReadOnlySet<string> TestNics => environment.TestNics;

    /// <summary>Who may change what here: everything on a box and in simulation; on a development machine only its test cards and never the host name (D292).</summary>
    public NetworkPolicy Policy => _simulated is not null || environment.Kind == DeploymentKind.Prod
        ? NetworkPolicy.Everything
        : new NetworkPolicy(false, environment.TestNics, false);

    public string HostName => _simulated?.HostName ?? Environment.MachineName;

    /// <summary>Every card, bond and VLAN as the system has it now, without what Pithyx keeps (Configured and Changeable are false, Method as the system says).</summary>
    public IReadOnlyList<NetworkLink> Links() => _simulated?.Links() ?? ReadLinks();

    /// <summary>The files Pithyx owns as they are on the host, by path.</summary>
    public IReadOnlyDictionary<string, string> OwnedFiles()
    {
        if (_simulated is not null)
        {
            return _simulated.Files;
        }
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (Directory.Exists(NetworkdConfig.Directory))
        {
            foreach (var path in Directory.EnumerateFiles(NetworkdConfig.Directory, "10-pithyx-*"))
            {
                if (NetworkdConfig.NameOf(path) is not null)
                {
                    files[path] = File.ReadAllText(path);
                }
            }
        }
        foreach (var path in new[] { NetworkdConfig.DefaultDnsDropIn, NetworkdConfig.ResolvedDropIn })
        {
            if (File.Exists(path))
            {
                files[path] = File.ReadAllText(path);
            }
        }
        return files;
    }

    /// <summary>
    /// Writes and removes the files of <paramref name="plan"/> and has networkd and resolved apply them. Returns the
    /// command that failed, or null. Simulation takes <paramref name="config"/> as the new state.
    /// </summary>
    public async Task<string?> ApplyAsync(NetworkdPlan plan, NetworkConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(config);
        if (_simulated is not null)
        {
            _simulated.Apply(plan, config);
            return null;
        }
        foreach (var (path, content) in plan.Write)
        {
            if (!NetworkdConfig.IsOwned(path))
            {
                throw new InvalidOperationException($"Pithyx does not own {path}.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var candidate = path + ".pithyx-new";
            await File.WriteAllTextAsync(candidate, content, cancellationToken);
            File.Move(candidate, path, overwrite: true);
        }
        foreach (var path in plan.Delete.Where(NetworkdConfig.IsOwned))
        {
            File.Delete(path);
        }
        foreach (var name in plan.RemoveNetdevs)
        {
            // Deleting a bond takes its VLANs with it.
            if (ExistingLinks().Contains(name) && !await RunAsync("networkctl", ["delete", name], cancellationToken))
            {
                return "networkctl";
            }
        }
        var existing = ExistingLinks();
        foreach (var name in plan.Release.Where(existing.Contains))
        {
            if (!await RunAsync("ip", ["link", "set", "dev", name, "nomaster"], cancellationToken))
            {
                return "ip";
            }
        }
        if (!await RunAsync("networkctl", ["reload"], cancellationToken))
        {
            return "networkctl";
        }
        existing = ExistingLinks();
        var reconfigure = plan.Reconfigure.Where(existing.Contains).ToList();
        if (plan.DefaultFileChanged)
        {
            // Cards without a file of their own follow the image's DHCP file and its name server drop-in.
            var own = OwnedFiles().Keys.Select(NetworkdConfig.NameOf).OfType<string>().ToHashSet(StringComparer.Ordinal);
            reconfigure.AddRange(ReadLinks().Where(link => link.Kind == InterfaceKind.Ethernet && link.Master is null && !own.Contains(link.Name)).Select(link => link.Name));
        }
        reconfigure = [.. reconfigure.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        if (reconfigure.Count > 0 && !await RunAsync("networkctl", ["reconfigure", .. reconfigure], cancellationToken))
        {
            return "networkctl";
        }
        if (plan.ResolvedChanged && !await RunAsync("systemctl", ["try-reload-or-restart", "systemd-resolved.service"], cancellationToken))
        {
            return "systemctl";
        }
        LogApplied(plan.Write.Count, plan.Delete.Count, reconfigure.Count);
        return null;
    }

    /// <summary>Sets the host name (hostnamed and <c>/etc/hosts</c>). Returns the command that failed, or null.</summary>
    public async Task<string?> SetHostNameAsync(string hostName, CancellationToken cancellationToken)
    {
        if (_simulated is not null)
        {
            _simulated.HostName = hostName;
            return null;
        }
        if (!await RunAsync("hostnamectl", ["set-hostname", hostName], cancellationToken))
        {
            return "hostnamectl";
        }
        const string HostsFile = "/etc/hosts";
        var content = File.Exists(HostsFile) ? await File.ReadAllTextAsync(HostsFile, cancellationToken) : "127.0.0.1\tlocalhost\n";
        var updated = NetworkParsers.HostsWithName(content, hostName);
        if (updated != content)
        {
            await File.WriteAllTextAsync(HostsFile + ".pithyx-new", updated, cancellationToken);
            File.Move(HostsFile + ".pithyx-new", HostsFile, overwrite: true);
        }
        return null;
    }

    private async Task<bool> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await commands.RunAsync(new CommandRequest(program, arguments, _commandTimeout), cancellationToken);
        if (result.ExitCode != 0)
        {
            LogCommandFailed(program, string.Join(' ', arguments), result.ExitCode, result.StandardError.Trim());
        }
        return result.ExitCode == 0;
    }

    private static HashSet<string> ExistingLinks() =>
        Directory.Exists(SysClassNet)
            ? Directory.EnumerateFileSystemEntries(SysClassNet).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : [];

    private List<NetworkLink> ReadLinks()
    {
        if (!Directory.Exists(SysClassNet))
        {
            return [];
        }
        var routes = NetworkParsers.Ipv4DefaultRoutes(Read("/proc/net/route") ?? "")
            .Concat(NetworkParsers.Ipv6DefaultRoutes(Read("/proc/net/ipv6_route") ?? ""))
            .ToList();
        var best = routes.Count == 0 ? null : routes.MinBy(route => route.Metric)!.Interface;
        var vlans = NetworkParsers.Vlans(Read("/proc/net/vlan/config") ?? "");
        var ifupdown = HostParsers.ParseInterfacesFile(ReadInterfacesFiles());
        var addresses = NetworkInterface.GetAllNetworkInterfaces().ToDictionary(nic => nic.Name, StringComparer.Ordinal);
        var links = new List<NetworkLink>();
        foreach (var directory in Directory.EnumerateFileSystemEntries(SysClassNet).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(directory);
            var kind = KindOf(directory, name);
            if (kind is null)
            {
                continue;
            }
            var networkd = Read($"/run/systemd/netif/links/{Read(Path.Combine(directory, "ifindex"))}") is { } state ? NetworkParsers.NetworkdLink(state) : null;
            var properties = addresses.TryGetValue(name, out var nic) ? nic.GetIPProperties() : null;
            var duplex = Read(Path.Combine(directory, "duplex"));
            var master = new FileInfo(Path.Combine(directory, "master")).LinkTarget is { } target ? Path.GetFileName(target) : null;
            var method = networkd?.GetValueOrDefault("NETWORK_FILE") == NetworkdConfig.DefaultFile ? "dhcp" : ifupdown.GetValueOrDefault(name) switch
            {
                "dhcp" => "dhcp",
                "static" => "static",
                _ => "unknown",
            };
            links.Add(new NetworkLink(
                name,
                kind.Value,
                Read(Path.Combine(directory, "address")),
                Read(Path.Combine(directory, "operstate")) == "up",
                Read(Path.Combine(directory, "carrier")) == "1",
                NetworkParsers.Number(Read(Path.Combine(directory, "speed"))),
                duplex is "full" or "half" ? duplex : null,
                NetworkParsers.Number(Read(Path.Combine(directory, "mtu"))) ?? 0,
                properties is null ? [] : [.. properties.UnicastAddresses
                    .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork || !address.Address.IsIPv6LinkLocal)
                    .Select(address => $"{address.Address}/{address.PrefixLength.ToString(CultureInfo.InvariantCulture)}")],
                [.. routes.Where(route => route.Interface == name && route.Gateway is not null).Select(route => route.Gateway!.ToString()).Distinct(StringComparer.Ordinal)],
                networkd is not null ? NetworkParsers.NetworkdDns(networkd) : properties is null ? [] : [.. properties.DnsAddresses.Select(dns => dns.ToString())],
                best == name,
                master,
                vlans.TryGetValue(name, out var vlan) ? vlan.Parent : null,
                vlans.TryGetValue(name, out vlan) ? vlan.Id : null,
                method,
                Configured: false,
                Changeable: false));
        }
        return links;
    }

    /// <summary>Cards, bonds and VLANs; container bridges, virtual cables, tunnels and wireless cards are not shown. A development machine's test cards count as cards.</summary>
    private InterfaceKind? KindOf(string directory, string name)
    {
        if (environment.TestNics.Contains(name))
        {
            return InterfaceKind.Ethernet;
        }
        if (Read(Path.Combine(directory, "type")) != "1")
        {
            return null;
        }
        return NetworkParsers.DevType(Read(Path.Combine(directory, "uevent")) ?? "") switch
        {
            "bond" => InterfaceKind.Bond,
            "vlan" => InterfaceKind.Vlan,
            null when Directory.Exists(Path.Combine(directory, "device")) => InterfaceKind.Ethernet,
            _ => null,
        };
    }

    private static string ReadInterfacesFiles()
    {
        var text = Read("/etc/network/interfaces") ?? "";
        if (Directory.Exists("/etc/network/interfaces.d"))
        {
            text += string.Concat(Directory.EnumerateFiles("/etc/network/interfaces.d").Order(StringComparer.Ordinal).Select(file => "\n" + (Read(file) ?? "")));
        }
        return text;
    }

    private static string? Read(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Applying the network failed: {Program} {Arguments} exited with {ExitCode}: {Error}")]
    private partial void LogCommandFailed(string program, string arguments, int exitCode, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Network files applied: {Written} written, {Deleted} removed, {Reconfigured} interfaces reconfigured")]
    private partial void LogApplied(int written, int deleted, int reconfigured);
}
