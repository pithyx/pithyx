using System.Net;
using System.Text.RegularExpressions;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Tls;

namespace Pithyx.Daemon.ReverseProxy;

/// <summary>What the validator needs to know about the box to keep proxy targets away from its own ports.</summary>
/// <param name="HostName">The box's host name.</param>
/// <param name="Addresses">The addresses of its network cards.</param>
/// <param name="WebPort">The port of the web interface (7484 on a box).</param>
/// <param name="ProxyPorts">The ports the proxy answers on (80 and 443 on a box).</param>
public sealed record BoxIdentity(string HostName, IReadOnlyList<IPAddress> Addresses, int WebPort, IReadOnlyList<int> ProxyPorts)
{
    /// <summary>Whether <paramref name="host"/> (a DNS name or address) names this box.</summary>
    public bool IsBox(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var name = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (IPAddress.TryParse(name.Trim('[', ']'), out var address))
        {
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }
            return IPAddress.IsLoopback(address)
                || address.Equals(IPAddress.Any)
                || address.Equals(IPAddress.IPv6Any)
                || Addresses.Any(own => (own.IsIPv4MappedToIPv6 ? own.MapToIPv4() : own).Equals(address));
        }
        var hostName = HostName.ToLowerInvariant();
        return name is "localhost" || name.EndsWith(".localhost", StringComparison.Ordinal)
            || name == hostName || name == $"{hostName}.local" || name.StartsWith(hostName + ".", StringComparison.Ordinal);
    }
}

/// <summary>Checks and normalizes what an administrator saves in Settings > Reverse proxy (D347). Throws <c>proxy.*</c>.</summary>
public static partial class ProxyValidator
{
    public const int MaxDomains = 50;
    public const int MaxHeaders = 20;
    public const int MaxHeaderValue = 4096;
    public const int MaxAddresses = 100;
    public const int MaxBasicAuthUsers = 20;
    public const int MaxBodyMegabytes = 1024 * 1024;
    public const int MaxConnectTimeoutSeconds = 300;
    public const int MaxResponseTimeoutSeconds = 86400;
    public const int MaxStreamName = 100;

    /// <summary>The box's own services a stream may never take: FTP, SSH, the web ports, SMB, 7484 and FTP's passive ports.</summary>
    public static IReadOnlyList<(int First, int Last)> ReservedPorts { get; } =
    [
        (21, 21), (22, 22), (80, 80), (443, 443), (445, 445), (7484, 7484),
        (FileServices.Ftp.FtpServer.DefaultPassivePorts.First, FileServices.Ftp.FtpServer.DefaultPassivePorts.Last),
    ];

    // Headers the proxy controls itself or that belong to one connection, never set by hand.
    private static readonly HashSet<string> _reservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Transfer-Encoding", "Content-Length", "TE", "Trailer", "Upgrade",
        "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization", "Forwarded", "HTTP2-Settings",
        "Strict-Transport-Security",
    };

    /// <summary>The domains in lower case without a final dot, each a DNS name with an optional leading <c>*.</c>.</summary>
    public static IReadOnlyList<string> Domains(IReadOnlyList<string>? domains)
    {
        var result = new List<string>();
        foreach (var raw in domains ?? [])
        {
            var name = (raw ?? "").Trim().TrimEnd('.').ToLowerInvariant();
            if (name.Length == 0)
            {
                continue;
            }
            string checkedName;
            try
            {
                checkedName = NameScope.Name(name);
            }
            catch (PithyxException)
            {
                throw new PithyxException(ErrorCodes.ProxyDomainInvalid, new Dictionary<string, object?> { ["name"] = raw ?? "" });
            }
            if (IPAddress.TryParse(checkedName, out _))
            {
                throw new PithyxException(ErrorCodes.ProxyDomainInvalid, new Dictionary<string, object?> { ["name"] = raw ?? "" });
            }
            if (!result.Contains(checkedName, StringComparer.Ordinal))
            {
                result.Add(checkedName);
            }
        }
        if (result.Count == 0)
        {
            throw new PithyxException(ErrorCodes.ProxyDomainsMissing);
        }
        if (result.Count > MaxDomains)
        {
            throw new PithyxException(ErrorCodes.ProxyDomainsTooMany, new Dictionary<string, object?> { ["max"] = MaxDomains });
        }
        return result;
    }

    /// <summary>A proxy host's target: a known scheme, a host without a wildcard, a port, and never the box's web ports.</summary>
    public static ProxyTarget Target(ProxyTarget? target, BoxIdentity box)
    {
        ArgumentNullException.ThrowIfNull(box);
        if (target is null || target.Scheme is not ("http" or "https"))
        {
            throw new PithyxException(ErrorCodes.ProxyTargetInvalid, new Dictionary<string, object?> { ["host"] = target?.Host ?? "" });
        }
        var host = TargetHost(target.Host);
        Port(target.Port);
        OnBox(host, target.Port, target.OnBox, box);
        return target with { Host = host, Tls = target.Scheme == "https" ? target.Tls : ProxyTargetTls.System };
    }

    /// <summary>A target host: a DNS name or an IP address (IPv6 without brackets), in lower case.</summary>
    public static string TargetHost(string? host)
    {
        var trimmed = (host ?? "").Trim().Trim('[', ']').TrimEnd('.');
        try
        {
            var name = NameScope.Name(trimmed);
            if (!name.StartsWith("*.", StringComparison.Ordinal))
            {
                return name;
            }
        }
        catch (PithyxException)
        {
            // Reported below with the host as typed.
        }
        throw new PithyxException(ErrorCodes.ProxyTargetInvalid, new Dictionary<string, object?> { ["host"] = host ?? "" });
    }

    /// <summary>
    /// Refuses a target on the proxy's own ports of the box, which would send the proxy into itself, and any other port
    /// of the box (such as the web interface on 7484) unless the administrator confirmed it.
    /// </summary>
    public static void OnBox(string host, int port, bool confirmed, BoxIdentity box)
    {
        ArgumentNullException.ThrowIfNull(box);
        if (!box.IsBox(host))
        {
            return;
        }
        if (box.ProxyPorts.Contains(port))
        {
            throw new PithyxException(ErrorCodes.ProxyTargetLoop, new Dictionary<string, object?> { ["port"] = port });
        }
        if (!confirmed)
        {
            throw new PithyxException(ErrorCodes.ProxyTargetOnBox, new Dictionary<string, object?> { ["host"] = host, ["port"] = port });
        }
    }

    public static void Port(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new PithyxException(ErrorCodes.ProxyLimitOutOfRange, new Dictionary<string, object?> { ["limit"] = "port", ["min"] = 1, ["max"] = 65535 });
        }
    }

    /// <summary>Headers with a valid, unique name the proxy does not control, and a value without line breaks.</summary>
    public static IReadOnlyList<ProxyHeader> Headers(IReadOnlyList<ProxyHeader>? headers)
    {
        var list = headers ?? [];
        if (list.Count > MaxHeaders)
        {
            throw new PithyxException(ErrorCodes.ProxyHeadersTooMany, new Dictionary<string, object?> { ["max"] = MaxHeaders });
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ProxyHeader>();
        foreach (var header in list)
        {
            var name = header?.Name?.Trim() ?? "";
            var value = header?.Value ?? "";
            if (!HeaderName().IsMatch(name)
                || _reservedHeaders.Contains(name)
                || name.StartsWith("X-Pithyx-", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
                || !seen.Add(name)
                || value.Length > MaxHeaderValue
                || value.Any(character => character is '\r' or '\n' or '\0'))
            {
                throw new PithyxException(ErrorCodes.ProxyHeaderInvalid, new Dictionary<string, object?> { ["name"] = name });
            }
            result.Add(new ProxyHeader(name, value.Trim()));
        }
        return result;
    }

    /// <summary>Addresses and CIDR ranges in their usual form.</summary>
    public static IReadOnlyList<string> Addresses(IReadOnlyList<string>? addresses)
    {
        var list = addresses ?? [];
        if (list.Count > MaxAddresses)
        {
            throw new PithyxException(ErrorCodes.ProxyLimitOutOfRange, new Dictionary<string, object?> { ["limit"] = "addresses", ["min"] = 0, ["max"] = MaxAddresses });
        }
        return [.. list.Select(Network).Select(network => network.ToString()).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>One address (a range of one) or a CIDR range; throws <c>proxy.address.invalid</c>.</summary>
    public static IPNetwork Network(string? text)
    {
        var value = (text ?? "").Trim();
        if (IPAddress.TryParse(value, out var address) && !value.Contains('/', StringComparison.Ordinal))
        {
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }
            return new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
        }
        if (IPNetwork.TryParse(value, out var network))
        {
            return network;
        }
        throw new PithyxException(ErrorCodes.ProxyAddressInvalid, new Dictionary<string, object?> { ["value"] = text ?? "" });
    }

    /// <summary>Checks a limit and returns it.</summary>
    public static int Limit(string limit, int value, int min, int max) =>
        value >= min && value <= max
            ? value
            : throw new PithyxException(ErrorCodes.ProxyLimitOutOfRange, new Dictionary<string, object?> { ["limit"] = limit, ["min"] = min, ["max"] = max });

    /// <summary>A <c>Host</c> header of the administrator's choice: a host name or address with an optional port.</summary>
    public static string? HostHeader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var trimmed = value.Trim();
        return HostHeaderPattern().IsMatch(trimmed)
            ? trimmed
            : throw new PithyxException(ErrorCodes.ProxyHeaderInvalid, new Dictionary<string, object?> { ["name"] = "Host" });
    }

    /// <summary>Basic Auth users with valid, unique names; a new user needs a password.</summary>
    public static IReadOnlyList<ProxyBasicAuthUser> BasicAuth(IReadOnlyList<ProxyBasicAuthUser>? users, IReadOnlySet<string> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var list = users ?? [];
        if (list.Count > MaxBasicAuthUsers)
        {
            throw new PithyxException(ErrorCodes.ProxyLimitOutOfRange, new Dictionary<string, object?> { ["limit"] = "basicAuth", ["min"] = 0, ["max"] = MaxBasicAuthUsers });
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var user in list)
        {
            var name = user?.Username ?? "";
            if (name.Length is 0 or > 64 || name.Contains(':', StringComparison.Ordinal) || name.Any(char.IsControl) || name.Trim() != name || !seen.Add(name))
            {
                throw new PithyxException(ErrorCodes.ProxyBasicAuthInvalid, new Dictionary<string, object?> { ["username"] = name });
            }
            if (user!.Password is { } password && (password.Length is 0 or > 1024))
            {
                throw new PithyxException(ErrorCodes.ProxyBasicAuthInvalid, new Dictionary<string, object?> { ["username"] = name });
            }
            if (user.Password is null && !existing.Contains(name))
            {
                throw new PithyxException(ErrorCodes.ProxyBasicAuthPasswordMissing, new Dictionary<string, object?> { ["username"] = name });
            }
        }
        return list;
    }

    /// <summary>A redirect host's target: an absolute <c>http</c> or <c>https</c> URL without credentials.</summary>
    public static string RedirectTarget(string? target)
    {
        var trimmed = (target ?? "").Trim();
        if (trimmed.Length is 0 or > 2048
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0
            || string.IsNullOrEmpty(uri.Host))
        {
            throw new PithyxException(ErrorCodes.ProxyRedirectTargetInvalid, new Dictionary<string, object?> { ["target"] = target ?? "" });
        }
        return trimmed;
    }

    public static int RedirectStatus(int status) =>
        status is 301 or 302 or 307 or 308
            ? status
            : throw new PithyxException(ErrorCodes.ProxyRedirectStatusInvalid, new Dictionary<string, object?> { ["status"] = status });

    /// <summary>The sign-in address: an <c>http</c> or <c>https</c> origin without path, or none.</summary>
    public static string? SignInAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }
        var trimmed = address.Trim().TrimEnd('/');
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && uri.UserInfo.Length == 0
            && uri.AbsolutePath == "/"
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0
            ? uri.GetLeftPart(UriPartial.Authority)
            : throw new PithyxException(ErrorCodes.ProxySignInAddressInvalid, new Dictionary<string, object?> { ["address"] = address });
    }

    /// <summary>A stream: a name, a protocol, a port that is no service of the box (below 1024 only when confirmed) and a target.</summary>
    public static ProxyStreamRequest Stream(ProxyStreamRequest request, BoxIdentity box)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(box);
        var name = (request.Name ?? "").Trim();
        if (name.Length > MaxStreamName || name.Any(char.IsControl))
        {
            throw new PithyxException(ErrorCodes.ProxyStreamNameInvalid, new Dictionary<string, object?> { ["max"] = MaxStreamName });
        }
        if (!request.Tcp && !request.Udp)
        {
            throw new PithyxException(ErrorCodes.ProxyStreamProtocolMissing);
        }
        Port(request.Port);
        if (ReservedPorts.Any(range => request.Port >= range.First && request.Port <= range.Last) || request.Port == box.WebPort || box.ProxyPorts.Contains(request.Port))
        {
            throw new PithyxException(ErrorCodes.ProxyStreamPortReserved, new Dictionary<string, object?> { ["port"] = request.Port });
        }
        if (request.Port < 1024 && !request.PrivilegedPort)
        {
            throw new PithyxException(ErrorCodes.ProxyStreamPortPrivileged, new Dictionary<string, object?> { ["port"] = request.Port });
        }
        var host = TargetHost(request.TargetHost);
        Port(request.TargetPort);
        if (box.IsBox(host) && request.TargetPort == request.Port)
        {
            throw new PithyxException(ErrorCodes.ProxyTargetLoop, new Dictionary<string, object?> { ["port"] = request.Port });
        }
        return request with { Name = name, TargetHost = host };
    }

    /// <summary>The Log Center source of a proxy host's requests.</summary>
    public static string LogSource(IReadOnlyList<string> domains)
    {
        ArgumentNullException.ThrowIfNull(domains);
        return "proxy/" + (domains.Count > 0 ? domains[0] : "unknown");
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderName();

    [GeneratedRegex(@"^(\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9.-]{1,253})(:[0-9]{1,5})?$", RegexOptions.CultureInvariant)]
    private static partial Regex HostHeaderPattern();
}
