using System.Globalization;
using System.Net;
using Pithyx.Daemon.Files;

namespace Pithyx.Daemon.FileServices.Ftp;

/// <summary>The text of FTP: commands, paths and listings (RFC 959, 2428, 3659), pure so it can be tested.</summary>
public static class FtpFormat
{
    /// <summary>A command line split into its upper-case verb and its argument.</summary>
    public static (string Verb, string Argument) Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var trimmed = line.TrimEnd('\r', '\n');
        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        return space < 0
            ? (trimmed.ToUpperInvariant(), "")
            : (trimmed[..space].ToUpperInvariant(), trimmed[(space + 1)..]);
    }

    /// <summary>
    /// The absolute path of an argument seen from the current folder, with <c>.</c> and <c>..</c> resolved; never above
    /// the root.
    /// </summary>
    public static string Combine(string current, string argument)
    {
        ArgumentNullException.ThrowIfNull(current);
        var start = string.IsNullOrEmpty(argument) ? current : argument.StartsWith('/') ? argument : $"{current.TrimEnd('/')}/{argument}";
        var parts = new List<string>();
        foreach (var segment in start.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
                continue;
            }
            parts.Add(segment);
        }
        return "/" + string.Join('/', parts);
    }

    /// <summary>A path in a 257 reply: in double quotes, with every quote doubled.</summary>
    public static string Quote(string path) => $"\"{(path ?? "").Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    /// <summary>One line of LIST in the long form of <c>ls -l</c>, which every client parses.</summary>
    public static string ListLine(FileEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var folder = entry.Kind == FileKind.Folder;
        var modified = entry.Modified.UtcDateTime;
        // Older than half a year or in the future: the year instead of the time, like ls.
        var date = Math.Abs((now.UtcDateTime - modified).TotalDays) > 182
            ? modified.ToString("MMM dd  yyyy", CultureInfo.InvariantCulture)
            : modified.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);
        return $"{(folder ? "drwxr-xr-x" : "-rw-r--r--")} 1 pithyx pithyx {entry.Size.ToString(CultureInfo.InvariantCulture),12} {date} {entry.Name}";
    }

    /// <summary>The facts of MLSD and MLST for one entry.</summary>
    public static string Facts(FileEntry entry, string name)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var type = entry.Kind == FileKind.Folder ? "dir" : "file";
        return $"type={type};size={entry.Size.ToString(CultureInfo.InvariantCulture)};modify={Time(entry.Modified)}; {name}";
    }

    /// <summary>A time as MDTM and MLSD give it: UTC, <c>YYYYMMDDHHMMSS</c>.</summary>
    public static string Time(DateTimeOffset value) => value.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    /// <summary>The 227 reply of PASV for an IPv4 address and a port.</summary>
    public static string PassiveReply(IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);
        var bytes = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).GetAddressBytes();
        return $"227 Entering Passive Mode ({string.Join(',', bytes.Select(part => part.ToString(CultureInfo.InvariantCulture)))},{(port >> 8).ToString(CultureInfo.InvariantCulture)},{(port & 0xff).ToString(CultureInfo.InvariantCulture)}).";
    }

    public static string ExtendedPassiveReply(int port) => $"229 Entering Extended Passive Mode (|||{port.ToString(CultureInfo.InvariantCulture)}|).";
}
