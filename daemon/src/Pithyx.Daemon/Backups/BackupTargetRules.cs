using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Backups;

/// <summary>A target's settings after <see cref="BackupTargetRules.Check"/>.</summary>
public sealed record CheckedTarget(string Name, string Host, int Port, string User, string Path, BackupHostKey? HostKey, BackupTargetPurpose Purpose);

/// <summary>What an administrator may enter for a backup target. Throws <see cref="ErrorCodes.BackupTargetInvalid"/> with the field.</summary>
public static partial class BackupTargetRules
{
    public const int DefaultPort = 22;

    public const int MaxNameLength = 64;

    public const int MaxPathLength = 500;

    public static CheckedTarget Check(BackupTargetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = (request.Name ?? "").Trim();
        if (name.Length is 0 or > MaxNameLength || name.Any(char.IsControl))
        {
            throw Invalid("name");
        }
        var host = CheckHost(request.Host);
        var port = CheckPort(request.Port);
        var user = (request.User ?? "").Trim();
        if (!UserName().IsMatch(user))
        {
            throw Invalid("user");
        }
        var path = request.Purpose == BackupTargetPurpose.Server ? CheckServerPath(request.Path) : CheckPath(request.Path);
        BackupHostKey? hostKey = null;
        if (!string.IsNullOrWhiteSpace(request.HostKey))
        {
            hostKey = SshHostKeys.Parse(request.HostKey) ?? throw Invalid("hostKey");
        }
        return new CheckedTarget(name, host, port, user, path, hostKey, request.Purpose);
    }

    /// <summary>A host name (lowercased), an IPv4 address or an IPv6 address without brackets.</summary>
    public static string CheckHost(string? input)
    {
        var host = (input ?? "").Trim();
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host[1..^1];
        }
        if (host.Contains(':', StringComparison.Ordinal))
        {
            return IPAddress.TryParse(host, out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6 && !host.Contains('%', StringComparison.Ordinal)
                ? v6.ToString()
                : throw Invalid("host");
        }
        if (Ipv4().IsMatch(host))
        {
            return IPAddress.TryParse(host, out var v4) ? v4.ToString() : throw Invalid("host");
        }
        return host.Length <= 253 && HostName().IsMatch(host) ? host.ToLowerInvariant() : throw Invalid("host");
    }

    public static int CheckPort(int? port) => port switch
    {
        null => DefaultPort,
        >= 1 and <= 65535 => port.Value,
        _ => throw Invalid("port"),
    };

    /// <summary>
    /// The folder on the server: relative to the login folder or absolute, with <c>/</c> between its parts and none at
    /// the end; empty for the login folder. No quotes, backslashes, control characters, empty parts, <c>.</c> or <c>..</c>.
    /// </summary>
    public static string CheckPath(string? input)
    {
        var path = (input ?? "").Trim();
        if (path.Length > MaxPathLength || path.Any(character => char.IsControl(character) || character is '"' or '\\'))
        {
            throw Invalid("path");
        }
        if (path is "" or "/")
        {
            return path;
        }
        var absolute = path.StartsWith('/');
        var parts = path.Trim('/').Split('/');
        if (parts.Any(part => part is "" or "." or ".."))
        {
            throw Invalid("path");
        }
        return (absolute ? "/" : "") + string.Join('/', parts);
    }

    /// <summary>
    /// The folder a server's key may read below: absolute, <c>/</c> when left out, and without spaces, because it stands
    /// unquoted in the <c>rrsync</c> command of the key's authorized_keys line.
    /// </summary>
    public static string CheckServerPath(string? input)
    {
        var path = CheckPath(string.IsNullOrWhiteSpace(input) ? "/" : input);
        return path.StartsWith('/') && !path.Contains(' ', StringComparison.Ordinal) ? path : throw Invalid("path");
    }

    /// <summary>The authorized_keys line of a server: the key may only run rsync's <c>rrsync</c>, read-only, below <paramref name="path"/>.</summary>
    public static string AuthorizedKeys(string path, string publicKey) => $"restrict,command=\"rrsync -ro {path}\" {publicKey}";

    /// <summary>The folders from the outermost to <paramref name="path"/> itself, for creating it step by step.</summary>
    public static IReadOnlyList<string> Ancestry(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path is "" or "/")
        {
            return [];
        }
        var absolute = path.StartsWith('/');
        var parts = path.Trim('/').Split('/');
        return [.. Enumerable.Range(1, parts.Length).Select(count => (absolute ? "/" : "") + string.Join('/', parts.Take(count)))];
    }

    public static PithyxException Invalid(string field) =>
        new(ErrorCodes.BackupTargetInvalid, new Dictionary<string, object?> { ["field"] = field });

    public static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex UserName();

    [GeneratedRegex(@"^[0-9]{1,3}(\.[0-9]{1,3}){3}$")]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9-]{0,62})(\.[A-Za-z0-9]([A-Za-z0-9-]{0,62}))*$")]
    private static partial Regex HostName();
}
