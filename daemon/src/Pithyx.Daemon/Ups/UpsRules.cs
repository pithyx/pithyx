using System.Text.RegularExpressions;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Ups;

/// <summary>When the box shuts down, which settings it takes and the NUT files of a UPS on USB (D377); pure functions.</summary>
public static partial class UpsRules
{
    /// <summary>The UPS's name in the box's own NUT server, for a UPS on USB.</summary>
    public const string UsbName = "pithyx";

    /// <summary>The flag the shutdown hook of NUT reads: with it, the UPS cuts its power at the end of the shutdown.</summary>
    public const string KillPowerFlag = "/etc/killpower";

    /// <summary>What the flag must hold: <c>upsmon -K</c> ignores a flag without NUT's magic line.</summary>
    public const string KillPowerMagic = "upsmon-shutdown-file\n";

    /// <summary>
    /// Why the box shuts down after this reading, or null: the NUT server's primary forces it, or the UPS is on battery
    /// and says its battery is low, or the charge or runtime reached the box's own limits.
    /// </summary>
    public static string? ShutdownReason(UpsReading reading, UpsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(settings);
        if (reading.ForcedShutdown)
        {
            return "forced";
        }
        if (!reading.OnBattery)
        {
            return null;
        }
        if (reading.LowBattery)
        {
            return "low_battery";
        }
        if (reading.Charge is { } charge && charge <= settings.BatteryLow)
        {
            return "charge";
        }
        return reading.Runtime is { } runtime && runtime <= settings.RuntimeLow * 60 ? "runtime" : null;
    }

    /// <summary>
    /// The saved settings with the request applied, or <c>system.ups.invalid</c> naming the first bad field. A network
    /// UPS needs its server; the user and password go over the NUT protocol, which allows no spaces in them.
    /// </summary>
    public static UpsSettings Apply(UpsSettings saved, UpsSettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Mode))
        {
            throw Invalid("mode");
        }
        var next = saved with
        {
            Mode = request.Mode,
            Host = request.Host?.Trim() ?? saved.Host,
            Port = request.Port ?? saved.Port,
            Name = request.Name?.Trim() ?? saved.Name,
            Username = request.Username?.Trim() ?? saved.Username,
            Password = request.Password ?? saved.Password,
            BatteryLow = request.BatteryLow ?? saved.BatteryLow,
            RuntimeLow = request.RuntimeLow ?? saved.RuntimeLow,
        };
        if (next.Mode == UpsMode.Network && !HostPattern().IsMatch(next.Host))
        {
            throw Invalid("host");
        }
        if (next.Port is < 1 or > 65535)
        {
            throw Invalid("port");
        }
        if (!NamePattern().IsMatch(next.Name))
        {
            throw Invalid("name");
        }
        if (next.Username.Length > 0 && !TokenPattern().IsMatch(next.Username))
        {
            throw Invalid("username");
        }
        if (next.Password.Length > 0 && !TokenPattern().IsMatch(next.Password))
        {
            throw Invalid("password");
        }
        if (next.BatteryLow is < 5 or > 90)
        {
            throw Invalid("batteryLow");
        }
        return next.RuntimeLow is < 1 or > 60 ? throw Invalid("runtimeLow") : next;
    }

    /// <summary>The files of <c>/etc/nut</c> for a UPS on USB: a driver that finds the UPS itself and a server only for this box.</summary>
    public static IReadOnlyDictionary<string, string> UsbFiles() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["nut.conf"] = Managed + "MODE=standalone\n# Reboot if the UPS kept the power on 10 minutes after the shutdown (the power came back meanwhile).\nPOWEROFF_WAIT=10m\n",
        ["ups.conf"] = Managed + $"[{UsbName}]\n\tdriver = usbhid-ups\n\tport = auto\n",
        ["upsd.conf"] = Managed + "LISTEN 127.0.0.1 3493\nLISTEN ::1 3493\n",
        ["upsd.users"] = Managed,
        ["upsmon.conf"] = Managed + $"# The daemon watches the UPS; upsmon only answers the shutdown hook's question (upsmon -K).\nPOWERDOWNFLAG {KillPowerFlag}\n",
    };

    /// <summary>The files of <c>/etc/nut</c> without a UPS on USB: nothing of NUT runs.</summary>
    public static IReadOnlyDictionary<string, string> OffFiles() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["nut.conf"] = Managed + "MODE=none\n",
        ["ups.conf"] = Managed,
        ["upsd.conf"] = Managed,
        ["upsd.users"] = Managed,
        ["upsmon.conf"] = Managed + $"POWERDOWNFLAG {KillPowerFlag}\n",
    };

    private const string Managed = "# Managed by pithyx (Settings > UPS); changes here are overwritten.\n";

    private static PithyxException Invalid(string field) =>
        new(ErrorCodes.SystemUpsInvalid, new Dictionary<string, object?> { ["field"] = field });

    [GeneratedRegex(@"^(?=.{1,253}$)[A-Za-z0-9:]([A-Za-z0-9.:-]*[A-Za-z0-9])?$")]
    private static partial Regex HostPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[\x21\x23-\x7e]{1,64}$")]
    private static partial Regex TokenPattern();
}
