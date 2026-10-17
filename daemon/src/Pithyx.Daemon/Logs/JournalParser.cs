using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Pithyx.Daemon.Logs;

/// <summary>Parses one line of <c>journalctl --output=json</c>.</summary>
public static class JournalParser
{
    /// <summary>The services whose logs the Log Center shows next to the kernel's; the daemon logs through its own sink.</summary>
    public static readonly IReadOnlyList<string> Units =
    [
        "smbd.service", "nmbd.service", "winbind.service", "ssh.service", "zfs-zed.service", "zfs-import-cache.service",
        "zfs-import-scan.service", "zfs-mount.service", "zfs-share.service", "systemd-networkd.service",
        "NetworkManager.service", "systemd-timesyncd.service", "chrony.service", "docker.service", "containerd.service",
        "smartmontools.service", "systemd-logind.service",
    ];

    /// <summary>The journalctl arguments that follow the kernel and <see cref="Units"/>, after a cursor or from this boot.</summary>
    public static IReadOnlyList<string> FollowArguments(string? afterCursor)
    {
        List<string> arguments = ["--follow", "--output=json", "--no-pager"];
        arguments.AddRange(afterCursor is null ? ["--boot"] : [$"--after-cursor={afterCursor}"]);
        arguments.Add("_TRANSPORT=kernel");
        arguments.Add("+");
        arguments.AddRange(Units.Select(unit => $"_SYSTEMD_UNIT={unit}"));
        return arguments;
    }

    public static PendingLogEntry? Parse(string line) => ParseWithIdentifier(line).Entry;

    /// <summary>The entry with its <c>SYSLOG_IDENTIFIER</c>, which tells SMB's audit lines apart (<see cref="FileServices.FileServiceActivity"/>).</summary>
    public static (PendingLogEntry? Entry, string? Identifier) ParseWithIdentifier(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return (null, null);
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return (null, null);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || Text(root, "__REALTIME_TIMESTAMP") is not { } realtime
                || !long.TryParse(realtime, NumberStyles.None, CultureInfo.InvariantCulture, out var micros)
                || Message(root) is not { } message)
            {
                return (null, null);
            }
            var data = new Dictionary<string, string>(StringComparer.Ordinal);
            if (AppOf(root) is { } app)
            {
                if (Text(root, "COM_PITHYX_SERVICE") is { } service)
                {
                    data["service"] = service;
                }
                if (Text(root, "CONTAINER_NAME") is { } container)
                {
                    data["container"] = container;
                }
                var appLine = AppLineOf(Text(root, "PRIORITY"), message);
                foreach (var (name, value) in appLine.Fields)
                {
                    data.TryAdd(name, value);
                }
                return (new PendingLogEntry(
                    DateTimeOffset.UnixEpoch.AddTicks(micros * 10),
                    appLine.Level,
                    LogSources.AppPrefix + app,
                    appLine.Message.TrimEnd(),
                    RequestId: appLine.RequestId,
                    Data: data.Count == 0 ? null : data,
                    JournalCursor: Text(root, "__CURSOR")), Text(root, "SYSLOG_IDENTIFIER"));
            }
            if (Text(root, "_PID") is { } pid)
            {
                data["pid"] = pid;
            }
            if (Text(root, "_SYSTEMD_UNIT") is { } unit)
            {
                data["unit"] = unit;
            }
            return (new PendingLogEntry(
                DateTimeOffset.UnixEpoch.AddTicks(micros * 10),
                LevelOf(Text(root, "PRIORITY")),
                SourceOf(root),
                message.TrimEnd(),
                Data: data.Count == 0 ? null : data,
                JournalCursor: Text(root, "__CURSOR")), Text(root, "SYSLOG_IDENTIFIER"));
        }
    }

    /// <summary>syslog priorities: 0 to 3 are errors, 4 warnings, 5 and 6 information, 7 debug.</summary>
    public static LogLevelName LevelOf(string? priority) => priority switch
    {
        "0" or "1" or "2" or "3" => LogLevelName.Error,
        "4" => LogLevelName.Warn,
        "7" => LogLevelName.Debug,
        _ => LogLevelName.Info,
    };

    /// <summary>An app container's line as the Log Center shows it.</summary>
    /// <param name="Level">The line's level.</param>
    /// <param name="Message">The <c>message</c> (or <c>msg</c>) of a JSON line, otherwise the whole line.</param>
    /// <param name="Fields">The other top-level fields of a JSON line, as text.</param>
    /// <param name="RequestId">The trace id of a W3C <c>traceparent</c> field, which ties the line to its request.</param>
    public sealed record AppLine(LogLevelName Level, string Message, IReadOnlyDictionary<string, string> Fields, string? RequestId);

    private static readonly string[] MessageKeys = ["message", "msg"];
    private const int MaxAppFields = 32;
    private const int MaxAppFieldLength = 1024;

    /// <summary>
    /// The level of an app container's line (D305, A14): stdout (priority 6) is info, stderr (priority 3) warn, and a
    /// JSON object line with a string <c>level</c> (<c>debug</c>, <c>info</c>, <c>warn</c> or <c>warning</c>,
    /// <c>error</c>) wins, so structured loggers keep their levels.
    /// </summary>
    public static LogLevelName AppLevelOf(string? priority, string message) => AppLineOf(priority, message).Level;

    /// <summary>
    /// An app container's line: its level as in <see cref="AppLevelOf"/>, and for a JSON object line with a string
    /// <c>message</c> or <c>msg</c> that text as the message and the other fields (at most 32, each cut to 1024
    /// characters) as data, so a structured logger's line reads like the daemon's own.
    /// </summary>
    public static AppLine AppLineOf(string? priority, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var stream = priority is "0" or "1" or "2" or "3" ? LogLevelName.Warn : LogLevelName.Info;
        if (!message.StartsWith('{') || message.Length > 64 * 1024)
        {
            return new AppLine(stream, message, new Dictionary<string, string>(), null);
        }
        try
        {
            using var line = JsonDocument.Parse(message);
            var root = line.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new AppLine(stream, message, new Dictionary<string, string>(), null);
            }
            var level = root.TryGetProperty("level", out var named) && named.ValueKind == JsonValueKind.String
                ? named.GetString()?.ToLowerInvariant() switch
                {
                    "debug" or "trace" => LogLevelName.Debug,
                    "info" or "information" => LogLevelName.Info,
                    "warn" or "warning" => LogLevelName.Warn,
                    "error" or "fatal" or "critical" => LogLevelName.Error,
                    _ => stream,
                }
                : stream;
            var messageKey = MessageKeys.FirstOrDefault(key => root.TryGetProperty(key, out var text) && text.ValueKind == JsonValueKind.String);
            if (messageKey is null)
            {
                return new AppLine(level, message, new Dictionary<string, string>(), null);
            }
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            string? requestId = null;
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name is "level" || property.Name == messageKey || fields.Count >= MaxAppFields || property.Name.Length is 0 or > 64)
                {
                    continue;
                }
                var text = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText();
                if (property.Name == "traceparent" && TraceIdOf(text) is { } traceId)
                {
                    requestId = traceId;
                }
                fields.TryAdd(property.Name, text.Length > MaxAppFieldLength ? text[..MaxAppFieldLength] : text);
            }
            return new AppLine(level, root.GetProperty(messageKey).GetString()!, fields, requestId);
        }
        catch (JsonException)
        {
            // Not JSON after all: the stream decides.
            return new AppLine(stream, message, new Dictionary<string, string>(), null);
        }
    }

    /// <summary>The trace id of a W3C traceparent (<c>00-&lt;32 hex&gt;-&lt;16 hex&gt;-&lt;2 hex&gt;</c>), or null.</summary>
    private static string? TraceIdOf(string traceparent)
    {
        var parts = traceparent.Split('-');
        return parts is [{ Length: 2 }, { Length: 32 } traceId, { Length: 16 }, { Length: 2 }] && traceId.All(char.IsAsciiHexDigitLower)
            ? traceId
            : null;
    }

    // The app of a container line: Docker's journald driver writes the label com.pithyx.app as COM_PITHYX_APP (D309).
    // Only the daemon sets that label (the Compose rewriter refuses it from developers), but the id is checked anyway.
    private static string? AppOf(JsonElement root) =>
        Text(root, "_SYSTEMD_UNIT") == "docker.service" && Text(root, "COM_PITHYX_APP") is { } app && Apps.Manifest.AppId.IsValid(app) ? app : null;

    private static string SourceOf(JsonElement root)
    {
        if (Text(root, "_TRANSPORT") == "kernel")
        {
            return "system/kernel";
        }
        var name = Text(root, "_SYSTEMD_UNIT") ?? Text(root, "SYSLOG_IDENTIFIER") ?? "unknown";
        return "system/" + (name.EndsWith(".service", StringComparison.Ordinal) ? name[..^".service".Length] : name);
    }

    /// <summary>Text, or the bytes journald stores for a message that is not valid UTF-8 or holds control characters.</summary>
    private static string? Message(JsonElement root)
    {
        if (!root.TryGetProperty("MESSAGE", out var message))
        {
            return null;
        }
        if (message.ValueKind == JsonValueKind.String)
        {
            return message.GetString();
        }
        if (message.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var bytes = new List<byte>();
        foreach (var item in message.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetByte(out var value))
            {
                return null;
            }
            bytes.Add(value);
        }
        return Encoding.UTF8.GetString([.. bytes]);
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
