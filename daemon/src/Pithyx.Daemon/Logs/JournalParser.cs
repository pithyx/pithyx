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
                return (new PendingLogEntry(
                    DateTimeOffset.UnixEpoch.AddTicks(micros * 10),
                    AppLevelOf(Text(root, "PRIORITY"), message),
                    LogSources.AppPrefix + app,
                    message.TrimEnd(),
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

    /// <summary>
    /// The level of an app container's line (D305, A14): stdout (priority 6) is info, stderr (priority 3) warn, and a
    /// JSON object line with a string <c>level</c> (<c>debug</c>, <c>info</c>, <c>warn</c> or <c>warning</c>,
    /// <c>error</c>) wins, so structured loggers keep their levels.
    /// </summary>
    public static LogLevelName AppLevelOf(string? priority, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.StartsWith('{') && message.Length <= 64 * 1024)
        {
            try
            {
                using var line = JsonDocument.Parse(message);
                if (line.RootElement.ValueKind == JsonValueKind.Object
                    && line.RootElement.TryGetProperty("level", out var level) && level.ValueKind == JsonValueKind.String)
                {
                    switch (level.GetString()?.ToLowerInvariant())
                    {
                        case "debug" or "trace":
                            return LogLevelName.Debug;
                        case "info" or "information":
                            return LogLevelName.Info;
                        case "warn" or "warning":
                            return LogLevelName.Warn;
                        case "error" or "fatal" or "critical":
                            return LogLevelName.Error;
                    }
                }
            }
            catch (JsonException)
            {
                // Not JSON after all: the stream decides.
            }
        }
        return priority is "0" or "1" or "2" or "3" ? LogLevelName.Warn : LogLevelName.Info;
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
