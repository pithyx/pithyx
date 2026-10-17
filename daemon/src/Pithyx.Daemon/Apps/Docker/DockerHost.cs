using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Apps.Docker;

/// <summary>
/// Docker on the box as D309 chose it, owned by the daemon (A14): <c>/etc/docker/daemon.json</c> with the data root on
/// the app pool (dataset <c>&lt;pool&gt;/pithyx/docker</c>), userns-remap to the system user <c>pithyx-docker</c> with the
/// identity windows in <c>/etc/subuid</c> and <c>/etc/subgid</c>, a drop-in that ties <c>docker.service</c> to the
/// pool's mount, and the nftables rule that keeps app containers away from the host's own services. Docker stays
/// disabled in systemd: the daemon starts it while a backend app is installed and its pool is mounted, and stops it
/// when the last one goes. Every command goes through <see cref="ICommandRunner"/>; in simulation nothing is touched.
/// </summary>
public sealed partial class DockerHost(
    ICommandRunner commands,
    DockerEngine engine,
    DockerOptions options,
    PoolInventory pools,
    SystemStateService state,
    TimeProvider time,
    ILogger<DockerHost> logger) : IDisposable
{
    public const string RemapUser = "pithyx-docker";

    /// <summary>Container ids outside the identity windows map to this base plus the id (D309), above <c>SUB_UID_MAX</c>.</summary>
    public const long RemapBase = 1_000_000_000;

    /// <summary>The address pool of the app networks, one /24 each.</summary>
    public const string AddressPool = "172.29.0.0/16";

    private static readonly TimeSpan _startTimeout = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Raised after Docker answered following a start, so the events can be followed.</summary>
    public event Action? Started;

    /// <summary>Docker's dataset on an app pool, mounted at <c>/&lt;pool&gt;/.pithyx/docker</c>.</summary>
    public static string Dataset(string pool) => $"{pool}/pithyx/docker";

    /// <summary>The subordinate uid lines of the remap user: the identity window for app uids 40000 to 49999.</summary>
    public static IReadOnlyList<string> SubuidLines =>
        [$"{RemapUser}:{RemapBase}:40000", $"{RemapUser}:40000:10000", $"{RemapUser}:{RemapBase + 50000}:15536"];

    /// <summary>The subordinate gid lines: the identity window for share gids and app gids, 30000 to 49999.</summary>
    public static IReadOnlyList<string> SubgidLines =>
        [$"{RemapUser}:{RemapBase}:30000", $"{RemapUser}:30000:20000", $"{RemapUser}:{RemapBase + 50000}:15536"];

    /// <summary>Docker's <c>daemon.json</c> (D309) with its data root, the mount point of <see cref="Dataset"/>.</summary>
    public static string DaemonJson(string dataRoot)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("data-root", dataRoot);
            writer.WriteString("storage-driver", "overlay2");
            writer.WriteString("userns-remap", RemapUser);
            // No docker0: it would masquerade to the LAN, and every app has its own network.
            writer.WriteString("bridge", "none");
            writer.WritePropertyName("default-address-pools");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("base", AddressPool);
            writer.WriteNumber("size", 24);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteString("log-driver", "journald");
            writer.WritePropertyName("log-opts");
            writer.WriteStartObject();
            writer.WriteString("labels", "com.pithyx.app,com.pithyx.service");
            writer.WriteString("tag", "{{.Name}}");
            writer.WriteEndObject();
            writer.WriteBoolean("no-new-privileges", true);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>The drop-in of <c>docker.service</c>: Docker never starts without its data root mounted.</summary>
    public static string DropIn(string dataRoot) =>
        $"""
        # Managed by pithyx. Docker's data lives on the app pool; it never starts without it.
        [Unit]
        RequiresMountsFor={dataRoot}

        """;

    /// <summary>
    /// The file with every line of the remap user replaced by <paramref name="lines"/>, the other users' lines kept in
    /// their order; the same text when nothing changes.
    /// </summary>
    public static string SubIds(string existing, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(lines);
        var kept = existing.Split('\n').Where(line => line.Length > 0 && !line.StartsWith(RemapUser + ":", StringComparison.Ordinal));
        return string.Join('\n', kept.Concat(lines)) + "\n";
    }

    /// <summary>
    /// Destinations an app's backend reaches only with <c>network.lan</c> (D358): private, shared (CGNAT), loopback,
    /// link-local, multicast and reserved IPv4 ranges. Everything else counts as the internet.
    /// </summary>
    public static IReadOnlyList<string> LocalIpv4 { get; } =
        ["0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.168.0.0/16", "224.0.0.0/4", "240.0.0.0/4"];

    /// <summary>The IPv6 ranges of <see cref="LocalIpv4"/>: unspecified, loopback, unique local, link-local and multicast.</summary>
    public static IReadOnlyList<string> LocalIpv6 { get; } = ["::/128", "::1/128", "fc00::/7", "fe80::/10", "ff00::/8"];

    /// <summary>
    /// The nftables rules of D309 and D358. Chain <c>apps-input</c>: new connections from the app bridges (<c>px-*</c>) to
    /// the host are dropped, so a container reaches no host service (7484, SSH, SMB) through its gateway address; the
    /// proxy's connections to the containers and their answers keep working. Chain <c>apps-forward</c>, before Docker's
    /// own rules: what leaves an app's outside network (<c>px-i*</c>) for the local ranges passes only from a bridge in the
    /// set <c>apps-lan</c>, anything else only from one in <c>apps-internet</c>; DNS to the resolvers Docker forwards to
    /// always passes, so names resolve without <c>network.lan</c>. A bridge in neither set gets nowhere. Only the two
    /// chains are rewritten; the sets keep their elements, which <see cref="NetworkRules"/> sets per app.
    /// </summary>
    public static string NftRules =>
        $$"""
        add table inet pithyx
        add chain inet pithyx apps-input { type filter hook input priority filter; policy accept; }
        flush chain inet pithyx apps-input
        add rule inet pithyx apps-input iifname "px-*" ct state new drop
        add set inet pithyx {{LanSet}} { type ifname; }
        add set inet pithyx {{InternetSet}} { type ifname; }
        add set inet pithyx {{Resolvers4Set}} { type ipv4_addr; }
        add set inet pithyx {{Resolvers6Set}} { type ipv6_addr; }
        add chain inet pithyx apps-forward { type filter hook forward priority -1; policy accept; }
        flush chain inet pithyx apps-forward
        add rule inet pithyx apps-forward iifname "px-i*" oifname "px-*" accept
        add rule inet pithyx apps-forward iifname "px-i*" ip daddr @{{Resolvers4Set}} meta l4proto { tcp, udp } th dport 53 accept
        add rule inet pithyx apps-forward iifname "px-i*" ip6 daddr @{{Resolvers6Set}} meta l4proto { tcp, udp } th dport 53 accept
        add rule inet pithyx apps-forward iifname "px-i*" iifname != @{{LanSet}} ip daddr { {{string.Join(", ", LocalIpv4)}} } drop
        add rule inet pithyx apps-forward iifname "px-i*" iifname != @{{LanSet}} ip6 daddr { {{string.Join(", ", LocalIpv6)}} } drop
        add rule inet pithyx apps-forward iifname "px-i*" iifname != @{{InternetSet}} ip daddr != { {{string.Join(", ", LocalIpv4)}} } drop
        add rule inet pithyx apps-forward iifname "px-i*" iifname != @{{InternetSet}} ip6 daddr != { {{string.Join(", ", LocalIpv6)}} } drop

        """;

    /// <summary>The bridges whose app may reach the local network.</summary>
    public const string LanSet = "apps-lan";

    /// <summary>The bridges whose app may reach the internet.</summary>
    public const string InternetSet = "apps-internet";

    /// <summary>The resolvers Docker's embedded DNS forwards to, IPv4.</summary>
    public const string Resolvers4Set = "apps-dns4";

    /// <summary>The resolvers, IPv6.</summary>
    public const string Resolvers6Set = "apps-dns6";

    /// <summary>
    /// One transaction for an app's outside network before its backend starts: its bridge in or out of the two sets as
    /// its permissions say (an element is added before it is deleted, so a missing one is no error), and the resolvers
    /// as Docker reads them now.
    /// </summary>
    public static string NetworkRules(string bridge, bool internet, bool lan, IReadOnlyList<IPAddress> resolvers)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(resolvers);
        var script = new StringBuilder();
        foreach (var (set, allowed) in new[] { (LanSet, lan), (InternetSet, internet) })
        {
            script.Append(CultureInfo.InvariantCulture, $"add element inet pithyx {set} {{ \"{bridge}\" }}\n");
            script.Append(CultureInfo.InvariantCulture, $"delete element inet pithyx {set} {{ \"{bridge}\" }}\n");
            if (allowed)
            {
                script.Append(CultureInfo.InvariantCulture, $"add element inet pithyx {set} {{ \"{bridge}\" }}\n");
            }
        }
        foreach (var (set, family) in new[] { (Resolvers4Set, AddressFamily.InterNetwork), (Resolvers6Set, AddressFamily.InterNetworkV6) })
        {
            script.Append(CultureInfo.InvariantCulture, $"flush set inet pithyx {set}\n");
            var addresses = resolvers.Where(address => address.AddressFamily == family).Select(address => address.ToString()).ToList();
            if (addresses.Count > 0)
            {
                script.Append(CultureInfo.InvariantCulture, $"add element inet pithyx {set} {{ {string.Join(", ", addresses)} }}\n");
            }
        }
        return script.ToString();
    }

    /// <summary>
    /// The name servers of a <c>resolv.conf</c> that Docker's embedded DNS forwards to: every <c>nameserver</c> address
    /// except loopback ones (a zone index of a link-local address is dropped).
    /// </summary>
    public static IReadOnlyList<IPAddress> Resolvers(string resolvConf)
    {
        ArgumentNullException.ThrowIfNull(resolvConf);
        var addresses = new List<IPAddress>();
        foreach (var line in resolvConf.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0] == "nameserver"
                && IPAddress.TryParse(parts[1].Split('%')[0], out var address) && !IPAddress.IsLoopback(address) && !addresses.Contains(address))
            {
                addresses.Add(address);
            }
        }
        return addresses;
    }

    /// <summary>
    /// Makes Docker run with the box's configuration on the app pool: the dataset, the remap user and its ids, the files,
    /// the firewall rule, then a start (or a restart after a change) and a wait until the Engine answers.
    /// </summary>
    public async Task EnsureRunningAsync(string pool, CancellationToken cancellationToken)
    {
        if (!options.Apply)
        {
            return;
        }
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var dataRoot = await EnsureDatasetAsync(pool, cancellationToken);
            if ((await commands.RunAsync(new CommandRequest("getent", ["passwd", RemapUser]), cancellationToken)).ExitCode != 0)
            {
                await RunAsync(new CommandRequest("useradd", ["--system", "--user-group", "--no-create-home", "--home-dir", "/nonexistent", "--shell", "/usr/sbin/nologin", RemapUser]), cancellationToken);
            }
            var changed = false;
            changed |= await WriteAsync(options.SubuidPath, SubIds(Read(options.SubuidPath), SubuidLines), cancellationToken);
            changed |= await WriteAsync(options.SubgidPath, SubIds(Read(options.SubgidPath), SubgidLines), cancellationToken);
            changed |= await WriteAsync(options.ConfigPath, DaemonJson(dataRoot), cancellationToken);
            if (await WriteAsync(options.DropInPath, DropIn(dataRoot), cancellationToken))
            {
                changed = true;
                await RunAsync(new CommandRequest("systemctl", ["daemon-reload"]), cancellationToken);
            }
            await RunAsync(new CommandRequest("nft", ["-f", "-"], StandardInput: NftRules), cancellationToken);
            var active = (await commands.RunAsync(new CommandRequest("systemctl", ["is-active", "--quiet", "docker.service"]), cancellationToken)).ExitCode == 0;
            if (active && changed)
            {
                LogRestarting(pool);
                await RunAsync(new CommandRequest("systemctl", ["restart", "docker.service"], TimeSpan.FromMinutes(2)), cancellationToken);
            }
            else if (!active)
            {
                LogStarting(pool);
                // A unit that failed before (a pool that was missing) would refuse to start again for a while.
                await commands.RunAsync(new CommandRequest("systemctl", ["reset-failed", "docker.service", "containerd.service"]), cancellationToken);
                await RunAsync(new CommandRequest("systemctl", ["start", "docker.service"], TimeSpan.FromMinutes(2)), cancellationToken);
            }
            else if (await engine.PingAsync(cancellationToken))
            {
                return;
            }
            var deadline = time.GetUtcNow() + _startTimeout;
            while (!await engine.PingAsync(cancellationToken))
            {
                if (time.GetUtcNow() > deadline)
                {
                    throw new PithyxException(ErrorCodes.AppDockerUnavailable);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500), time, cancellationToken);
            }
        }
        finally
        {
            _lock.Release();
        }
        Started?.Invoke();
    }

    /// <summary>
    /// Sets where an app's outside network leads (D358) before its backend starts. Docker takes the name servers of
    /// <c>/etc/resolv.conf</c>, or those systemd-resolved uses when that file names only its local stub, and the
    /// containers' DNS queries go to them through the bridge, so the same addresses are let through.
    /// </summary>
    public async Task AllowNetworkAsync(string bridge, bool internet, bool lan, CancellationToken cancellationToken)
    {
        if (!options.Apply)
        {
            return;
        }
        var resolvers = Resolvers(Read(options.ResolvConfPath));
        if (resolvers.Count == 0)
        {
            resolvers = Resolvers(Read(options.ResolvedConfPath));
        }
        await RunAsync(new CommandRequest("nft", ["-f", "-"], StandardInput: NetworkRules(bridge, internet, lan, resolvers)), cancellationToken);
    }

    /// <summary>Stops Docker and containerd when no backend app is left; their configuration stays for the next app.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!options.Apply)
        {
            return;
        }
        await _lock.WaitAsync(cancellationToken);
        try
        {
            LogStopping();
            await RunAsync(new CommandRequest("systemctl", ["stop", "docker.socket", "docker.service", "containerd.service"], TimeSpan.FromMinutes(2)), cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();

    /// <summary>Creates Docker's dataset unless it exists and returns where it is mounted.</summary>
    private async Task<string> EnsureDatasetAsync(string pool, CancellationToken cancellationToken)
    {
        var dataset = Dataset(pool);
        if (!pools.Current.Datasets.Any(candidate => candidate.Name == dataset))
        {
            await RunAsync(Storage.Zfs.ZfsPlans.CreateDockerDataset(pool), cancellationToken);
            await state.RefreshPoolsAsync(cancellationToken);
        }
        return pools.Current.Datasets.FirstOrDefault(candidate => candidate.Name == dataset) is { Mounted: true, MountPoint: { } mountPoint }
            ? mountPoint
            : throw new PithyxException(ErrorCodes.AppPoolUnavailable, new Dictionary<string, object?> { ["pool"] = pool, ["reason"] = "locked" });
    }

    private static string Read(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

    /// <summary>Writes a file when its content differs (through a temporary file and a rename); true when it changed.</summary>
    private static async Task<bool> WriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && await File.ReadAllTextAsync(path, cancellationToken) == content)
        {
            return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".pithyx-new";
        await File.WriteAllTextAsync(temporary, content, cancellationToken);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        File.Move(temporary, path, overwrite: true);
        return true;
    }

    private async Task RunAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var result = await commands.RunAsync(request, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new PithyxException(ErrorCodes.AppDockerUnavailable);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting Docker for the app pool {Pool}")]
    private partial void LogStarting(string pool);

    [LoggerMessage(Level = LogLevel.Information, Message = "Restarting Docker with the changed configuration for the app pool {Pool}")]
    private partial void LogRestarting(string pool);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping Docker: no backend app is left")]
    private partial void LogStopping();
}
