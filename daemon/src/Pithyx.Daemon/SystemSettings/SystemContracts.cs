using System.Reflection;

namespace Pithyx.Daemon.SystemSettings;

/// <summary>The Pithyx build: the version and the commit it was built from (from the informational version).</summary>
public sealed record BuildInfo(string Version, string? Commit)
{
    public static BuildInfo Current { get; } = FromInformationalVersion(
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");

    /// <summary><c>1.2.3+&lt;sha&gt;</c> as the .NET SDK writes it from the git checkout.</summary>
    public static BuildInfo FromInformationalVersion(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var plus = value.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? new BuildInfo(value, null) : new BuildInfo(value[..plus], value[(plus + 1)..]);
    }
}

/// <param name="Version">The Pithyx version.</param>
/// <param name="Commit">The git commit it was built from, if known.</param>
/// <param name="OperatingSystem">The distribution, for example "Debian GNU/Linux 13 (trixie)".</param>
/// <param name="Kernel">The running Linux kernel.</param>
/// <param name="Machine">Vendor and product of the board or virtual machine, if the firmware reports them.</param>
/// <param name="CpuModel">The processor, if known.</param>
/// <param name="CpuCores">Logical processors.</param>
/// <param name="MemoryTotalBytes">Physical memory.</param>
/// <param name="Hostname">The host name.</param>
/// <param name="UptimeSeconds">Seconds since boot.</param>
public sealed record SystemOverview(
    string Version,
    string? Commit,
    string OperatingSystem,
    string Kernel,
    string? Machine,
    string? CpuModel,
    int CpuCores,
    long MemoryTotalBytes,
    string Hostname,
    double UptimeSeconds);

/// <param name="Name">The interface, for example <c>ens18</c>.</param>
/// <param name="MacAddress">Its hardware address, if it has one.</param>
/// <param name="Up">Whether the link is up.</param>
/// <param name="Method"><c>dhcp</c>, <c>static</c> or <c>unknown</c> when the configuration is not one Pithyx reads.</param>
/// <param name="Addresses">IPv4 and IPv6 addresses with prefix length, for example <c>192.168.1.20/24</c>.</param>
/// <param name="Gateways">Default gateways of the interface.</param>
/// <param name="DnsServers">Name servers the interface uses.</param>
public sealed record NetworkInterfaceInfo(
    string Name,
    string? MacAddress,
    bool Up,
    string Method,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> Gateways,
    IReadOnlyList<string> DnsServers);

/// <param name="TimeZone">IANA name of the system time zone.</param>
/// <param name="Synchronized">Whether the clock is synchronized with a time server.</param>
/// <param name="NtpServers">Time servers Pithyx configured; empty means the distribution's defaults.</param>
/// <param name="TimeZones">Every time zone the system knows.</param>
public sealed record TimeSettings(string TimeZone, bool Synchronized, IReadOnlyList<string> NtpServers, IReadOnlyList<string> TimeZones);

/// <param name="TimeZone">IANA name of the new time zone.</param>
/// <param name="NtpServers">At most eight host names or addresses; empty for the distribution's defaults.</param>
public sealed record TimeSettingsRequest(string TimeZone, IReadOnlyList<string> NtpServers);

/// <summary>The regional settings of the box (D282).</summary>
/// <param name="Language">
/// The system language: the sign-in page and every user on "Automatic" whose browser asks for a language Pithyx does
/// not have get it, and so do messages the box sends on its own. One of <see cref="SystemRegion.Languages"/>.
/// </param>
/// <param name="Region">
/// ISO 3166 country code such as <c>DE</c> or <c>US</c> whose date, time and number formats every user sees, or null to
/// follow each user's language.
/// </param>
public sealed record SystemRegion(string Language, string? Region)
{
    /// <summary>The languages Pithyx ships, as in <c>i18n/</c>.</summary>
    public static readonly IReadOnlyList<string> Languages = ["en", "de"];

    public static readonly SystemRegion Default = new("en", null);

    public bool IsValid() => Languages.Contains(Language) && (Region is null || (Region.Length == 2 && Region.All(char.IsAsciiLetterUpper)));
}

/// <summary>What the power button does.</summary>
public enum PowerAction
{
    Restart,
    Shutdown,
}

/// <param name="Action"><c>restart</c> or <c>shutdown</c>.</param>
public sealed record PowerRequest(PowerAction Action);
