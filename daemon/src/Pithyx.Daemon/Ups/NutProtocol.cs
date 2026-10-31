using System.Globalization;
using System.Text;

namespace Pithyx.Daemon.Ups;

/// <summary>What one answer of a NUT server says about a UPS.</summary>
/// <param name="OnBattery">The flag <c>OB</c> of <c>ups.status</c>.</param>
/// <param name="LowBattery">The flag <c>LB</c>: the UPS says its battery is low.</param>
/// <param name="Charge"><c>battery.charge</c> in percent.</param>
/// <param name="Runtime"><c>battery.runtime</c> in seconds.</param>
/// <param name="Load"><c>ups.load</c> in percent.</param>
/// <param name="Model">Manufacturer and model.</param>
/// <param name="ForcedShutdown">The server's primary shuts every system on this UPS down (<c>FSD</c>).</param>
public sealed record UpsReading(bool OnBattery, bool LowBattery, bool ForcedShutdown, int? Charge, int? Runtime, int? Load, string? Model);

/// <summary>The lines of the NUT network protocol (<c>upsd</c>, port 3493) the box reads; pure functions.</summary>
public static class NutProtocol
{
    /// <summary>Parses <c>VAR &lt;ups&gt; &lt;name&gt; "&lt;value&gt;"</c>; the value may escape <c>"</c> and <c>\</c>.</summary>
    public static bool TryParseVar(string line, out string name, out string value)
    {
        ArgumentNullException.ThrowIfNull(line);
        name = "";
        value = "";
        var parts = line.Split(' ', 4);
        if (parts.Length != 4 || parts[0] != "VAR" || parts[3].Length < 2 || parts[3][0] != '"' || parts[3][^1] != '"')
        {
            return false;
        }
        var quoted = parts[3].AsSpan(1, parts[3].Length - 2);
        var text = new StringBuilder(quoted.Length);
        for (var index = 0; index < quoted.Length; index++)
        {
            if (quoted[index] == '\\' && index + 1 < quoted.Length)
            {
                index++;
            }
            text.Append(quoted[index]);
        }
        name = parts[2];
        value = text.ToString();
        return true;
    }

    /// <summary>The variables of a <c>LIST VAR</c> answer, without its <c>BEGIN</c> and <c>END</c> lines.</summary>
    public static IReadOnlyDictionary<string, string> ParseVars(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (TryParseVar(line, out var name, out var value))
            {
                vars[name] = value;
            }
        }
        return vars;
    }

    /// <summary>The state of the UPS from its variables: the flags of <c>ups.status</c> and the battery values.</summary>
    public static UpsReading Read(IReadOnlyDictionary<string, string> vars)
    {
        ArgumentNullException.ThrowIfNull(vars);
        var flags = (vars.GetValueOrDefault("ups.status") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var maker = vars.GetValueOrDefault("device.mfr") ?? vars.GetValueOrDefault("ups.mfr");
        var model = vars.GetValueOrDefault("device.model") ?? vars.GetValueOrDefault("ups.model");
        var name = string.Join(' ', new[] { maker?.Trim(), model?.Trim() }.Where(part => !string.IsNullOrEmpty(part)));
        return new UpsReading(
            flags.Contains("OB"),
            flags.Contains("LB"),
            flags.Contains("FSD"),
            Number(vars, "battery.charge"),
            Number(vars, "battery.runtime"),
            Number(vars, "ups.load"),
            name.Length == 0 ? null : name);
    }

    /// <summary>
    /// The problem of an <c>ERR</c> answer in the words of <see cref="UpsState.Problem"/>, or null for any other line.
    /// </summary>
    public static string? ProblemOf(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!line.StartsWith("ERR ", StringComparison.Ordinal))
        {
            return null;
        }
        return line[4..].Split(' ')[0] switch
        {
            "UNKNOWN-UPS" => "unknown_ups",
            "DRIVER-NOT-CONNECTED" => "driver_not_connected",
            "DATA-STALE" => "data_stale",
            "ACCESS-DENIED" or "PASSWORD-REQUIRED" or "USERNAME-REQUIRED" or "INVALID-PASSWORD" or "INVALID-USERNAME" => "access_denied",
            _ => "protocol",
        };
    }

    private static int? Number(IReadOnlyDictionary<string, string> vars, string name) =>
        vars.TryGetValue(name, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? (int)Math.Round(value)
            : null;
}
