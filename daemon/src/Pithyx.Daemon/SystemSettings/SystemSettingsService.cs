using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Monitoring;

namespace Pithyx.Daemon.SystemSettings;

/// <summary>
/// System facts for the Info Center and the network page, and the regional settings of the box. Reads come from files
/// only (no process for a read request). A time change runs <c>timedatectl</c> and writes a timesyncd drop-in in real
/// mode; simulation keeps it in memory. The system language and region live in SQLite.
/// </summary>
public sealed class SystemSettingsService(
    ICommandRunner commands,
    DaemonMode mode,
    SqliteConnectionFactory connections,
    TimeProvider time) : IDisposable
{
    public const string TimesyncdDropIn = "/etc/systemd/timesyncd.conf.d/pithyx.conf";
    public const int MaxTimeServers = 8;

    private readonly SemaphoreSlim _timeChange = new(1, 1);
    private TimeSettingsRequest? _simulatedTime;

    public void Dispose() => _timeChange.Dispose();

    /// <summary>The last power action asked for in simulation, where nothing restarts.</summary>
    public PowerAction? SimulatedPower { get; private set; }

    /// <summary>
    /// Restarts or shuts down the box a few seconds after the answer went out, so the desktop learns that it worked.
    /// Simulation only remembers it.
    /// </summary>
    public void Power(PowerAction action)
    {
        if (mode != DaemonMode.Real)
        {
            SimulatedPower = action;
            return;
        }
        var command = new CommandRequest("systemctl", [action == PowerAction.Restart ? "reboot" : "poweroff"]);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), time);
            await commands.RunAsync(command, CancellationToken.None);
        });
    }

    public SystemOverview Overview()
    {
        var build = BuildInfo.Current;
        var vendor = Read("/sys/class/dmi/id/sys_vendor");
        var product = Read("/sys/class/dmi/id/product_name");
        var memory = Read("/proc/meminfo");
        var uptime = Read("/proc/uptime");
        return new SystemOverview(
            build.Version,
            build.Commit,
            HostParsers.ParseOsRelease(Read("/etc/os-release") ?? "") ?? "Linux",
            Read("/proc/sys/kernel/osrelease") ?? Environment.OSVersion.VersionString,
            vendor is null && product is null ? null : $"{vendor} {product}".Trim(),
            HostParsers.ParseCpuModel(Read("/proc/cpuinfo") ?? ""),
            Environment.ProcessorCount,
            memory is null ? 0 : ProcParsers.ParseMemInfo(memory).TotalBytes,
            Environment.MachineName,
            uptime is null ? 0 : ProcParsers.ParseUptime(uptime));
    }

    public IReadOnlyList<NetworkInterfaceInfo> Network()
    {
        var methods = HostParsers.ParseInterfacesFile(ReadInterfacesFiles());
        return [.. NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback && ResourceParsers.IsShownInterface(nic.Name))
            .OrderBy(nic => nic.Name, StringComparer.Ordinal)
            .Select(nic =>
            {
                var properties = nic.GetIPProperties();
                var mac = nic.GetPhysicalAddress().GetAddressBytes();
                return new NetworkInterfaceInfo(
                    nic.Name,
                    mac.Length == 0 ? null : string.Join(':', mac.Select(part => part.ToString("x2", CultureInfo.InvariantCulture))),
                    nic.OperationalStatus == OperationalStatus.Up,
                    methods.GetValueOrDefault(nic.Name) switch
                    {
                        "dhcp" => "dhcp",
                        "static" => "static",
                        _ => "unknown",
                    },
                    [.. properties.UnicastAddresses
                        .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork || !address.Address.IsIPv6LinkLocal)
                        .Select(address => $"{address.Address}/{address.PrefixLength}")],
                    [.. properties.GatewayAddresses.Select(gateway => gateway.Address.ToString())],
                    [.. properties.DnsAddresses.Select(dns => dns.ToString())]);
            })];
    }

    public TimeSettings Time()
    {
        var zones = TimeZoneInfo.GetSystemTimeZones()
            .Select(zone => zone.Id)
            .Where(IsZoneName)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (mode == DaemonMode.Simulated)
        {
            var simulated = _simulatedTime ?? new TimeSettingsRequest("UTC", []);
            return new TimeSettings(simulated.TimeZone, true, simulated.NtpServers, zones);
        }
        var link = new FileInfo("/etc/localtime").LinkTarget;
        var zone = (link is null ? null : HostParsers.TimeZoneOfLink(link)) ?? TimeZoneInfo.Local.Id;
        return new TimeSettings(
            zone,
            File.Exists("/run/systemd/timesync/synchronized"),
            HostParsers.ParseTimesyncdServers(Read(TimesyncdDropIn) ?? ""),
            zones);
    }

    /// <summary>Sets the time zone and the time servers; real mode runs timedatectl and restarts timesyncd.</summary>
    public async Task<TimeSettings> SetTimeAsync(TimeSettingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsTimeZone(request.TimeZone))
        {
            throw new PithyxException(ErrorCodes.SystemTimeZoneInvalid, new Dictionary<string, object?> { ["zone"] = request.TimeZone ?? "" });
        }
        var servers = (request.NtpServers ?? [])
            .Select(server => server.Trim())
            .Where(server => server.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (servers.FirstOrDefault(server => !HostParsers.IsTimeServer(server)) is { } invalid)
        {
            throw new PithyxException(ErrorCodes.SystemTimeServerInvalid, new Dictionary<string, object?> { ["server"] = invalid });
        }
        if (servers.Count > MaxTimeServers)
        {
            throw new PithyxException(ErrorCodes.SystemTimeServersTooMany, new Dictionary<string, object?> { ["max"] = MaxTimeServers });
        }
        await _timeChange.WaitAsync(cancellationToken);
        try
        {
            if (mode == DaemonMode.Simulated)
            {
                _simulatedTime = new TimeSettingsRequest(request.TimeZone, servers);
                return Time();
            }
            await RunAsync(new CommandRequest("timedatectl", ["set-timezone", request.TimeZone]), cancellationToken);
            // File.Delete throws when the folder is missing, which it is on a box that never had time servers set.
            if (servers.Count == 0 && File.Exists(TimesyncdDropIn))
            {
                File.Delete(TimesyncdDropIn);
            }
            else if (servers.Count > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TimesyncdDropIn)!);
                await File.WriteAllTextAsync(TimesyncdDropIn, HostParsers.RenderTimesyncdDropIn(servers), cancellationToken);
            }
            await RunAsync(new CommandRequest("systemctl", ["try-restart", "systemd-timesyncd.service"]), cancellationToken);
            return Time();
        }
        finally
        {
            _timeChange.Release();
        }
    }

    /// <summary>The system language and region; the defaults until an administrator saved them.</summary>
    public async Task<SystemRegion> RegionAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<(string Language, string? Region)?>(
            "SELECT language, region FROM system_region WHERE id = 1");
        return row is { } saved ? new SystemRegion(saved.Language, saved.Region) : SystemRegion.Default;
    }

    public async Task<SystemRegion> SetRegionAsync(SystemRegion region, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (!region.IsValid())
        {
            throw new PithyxException(ErrorCodes.RequestBodyInvalid);
        }
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO system_region (id, language, region, updated_at) VALUES (1, @Language, @Region, @now)
            ON CONFLICT (id) DO UPDATE SET language = excluded.language, region = excluded.region, updated_at = excluded.updated_at
            """,
            new { region.Language, region.Region, now = SessionService.Format(time.GetUtcNow()) });
        return region;
    }

    /// <summary>Whether the value is an IANA time zone this box knows (Area/City or UTC).</summary>
    public static bool IsTimeZone(string? zone) => zone is not null && IsZoneName(zone) && TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _);

    // Area/City names and UTC; the legacy aliases without a slash (EST, GMT0) stay out of the list.
    private static bool IsZoneName(string id) => id.Contains('/', StringComparison.Ordinal) || id == "UTC";

    private async Task RunAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var result = await commands.RunAsync(command, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new PithyxException(ErrorCodes.SystemTimeApplyFailed, new Dictionary<string, object?> { ["command"] = command.FileName, ["exitCode"] = result.ExitCode });
        }
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
}
