namespace Pithyx.Daemon.Ups;

/// <summary>Where the box reads its UPS from (D377): nowhere, a UPS on USB through the box's own NUT server, or another NUT server.</summary>
public enum UpsMode
{
    Off,
    Usb,
    Network,
}

/// <summary>The saved UPS settings; the password never leaves the daemon.</summary>
/// <param name="Mode">Off, a UPS on USB, or a UPS of another NUT server.</param>
/// <param name="Host">The NUT server of a network UPS.</param>
/// <param name="Port">Its port.</param>
/// <param name="Name">The UPS's name on that server.</param>
/// <param name="Username">The NUT user the box signs in with, empty for none.</param>
/// <param name="Password">Its password, empty for none.</param>
/// <param name="BatteryLow">Shut down on battery at or below this charge in percent.</param>
/// <param name="RuntimeLow">Shut down on battery at or below this many minutes of runtime left.</param>
public sealed record UpsSettings(UpsMode Mode, string Host, int Port, string Name, string Username, string Password, int BatteryLow, int RuntimeLow)
{
    public const int DefaultPort = 3493;
    public static readonly UpsSettings Default = new(UpsMode.Off, "", DefaultPort, "ups", "", "", 20, 5);
}

/// <summary>The UPS settings as administrators see them.</summary>
/// <param name="Mode">Off, a UPS on USB, or a UPS of another NUT server.</param>
/// <param name="Host">The NUT server of a network UPS.</param>
/// <param name="Port">Its port, 3493 unless set.</param>
/// <param name="Name">The UPS's name on that server.</param>
/// <param name="Username">The NUT user the box signs in with, empty for none.</param>
/// <param name="HasPassword">Whether a password is saved; the password itself is never sent.</param>
/// <param name="BatteryLow">On battery, the box shuts down at or below this charge in percent (5 to 90).</param>
/// <param name="RuntimeLow">On battery, the box shuts down at or below this many minutes of runtime left (1 to 60).</param>
public sealed record UpsSettingsView(UpsMode Mode, string Host, int Port, string Name, string Username, bool HasPassword, int BatteryLow, int RuntimeLow);

/// <summary>New UPS settings; a missing field keeps its value.</summary>
/// <param name="Mode">Off, a UPS on USB, or a UPS of another NUT server.</param>
/// <param name="Host">The NUT server of a network UPS (a host name or an address); required for the mode <c>network</c>.</param>
/// <param name="Port">Its port, 1 to 65535.</param>
/// <param name="Name">The UPS's name on that server: letters, digits, dot, dash and underscore, up to 64.</param>
/// <param name="Username">The NUT user, empty for none; no spaces or quotes, up to 64.</param>
/// <param name="BatteryLow">The charge limit in percent, 5 to 90.</param>
/// <param name="RuntimeLow">The runtime limit in minutes, 1 to 60.</param>
/// <param name="Password">A new password; an empty one removes it, a missing one keeps it.</param>
public sealed record UpsSettingsRequest(
    UpsMode Mode,
    string? Host = null,
    int? Port = null,
    string? Name = null,
    string? Username = null,
    string? Password = null,
    int? BatteryLow = null,
    int? RuntimeLow = null);

/// <summary>Whether the box reaches its UPS.</summary>
public enum UpsConnection
{
    Off,
    Connecting,
    Connected,
    Unreachable,
}

/// <summary>Where the UPS takes its power from.</summary>
public enum UpsPower
{
    Unknown,
    Online,
    OnBattery,
}

/// <summary>What the box knows about its UPS right now.</summary>
/// <param name="Connection">Off, connecting, connected or unreachable; while unreachable the other values are the last ones read.</param>
/// <param name="Power">On line power or on battery, unknown before the first answer.</param>
/// <param name="LowBattery">The UPS says its battery is low.</param>
/// <param name="Problem">
/// Why the UPS is unreachable: <c>refused</c> (no NUT server answers), <c>timeout</c>, <c>unknown_ups</c> (the server
/// has no UPS of that name), <c>driver_not_connected</c> (no UPS on USB, or its driver failed), <c>data_stale</c>,
/// <c>access_denied</c> (user or password wrong) or <c>protocol</c>.
/// </param>
/// <param name="Charge">Battery charge in percent, if the UPS reports it.</param>
/// <param name="Runtime">Runtime left on battery in seconds, if the UPS reports it.</param>
/// <param name="Load">Load in percent of the UPS's capacity, if the UPS reports it.</param>
/// <param name="Model">Manufacturer and model, if the UPS reports them.</param>
/// <param name="OnBatterySince">When the UPS went on battery, while it is.</param>
/// <param name="ShutdownReason">Why the box shuts down, once it does: <c>forced</c> (the NUT server's primary shuts everything down), <c>low_battery</c>, <c>charge</c>, <c>runtime</c> or <c>lost</c> (the UPS was on battery and stopped answering).</param>
public sealed record UpsState(
    UpsConnection Connection,
    UpsPower Power,
    bool LowBattery,
    int? Charge,
    int? Runtime,
    int? Load,
    string? Model,
    string? Problem,
    DateTimeOffset? OnBatterySince,
    string? ShutdownReason)
{
    public static readonly UpsState Off = new(UpsConnection.Off, UpsPower.Unknown, false, null, null, null, null, null, null, null);
}

/// <summary>Settings &gt; UPS: the settings, the UPS's state and whether this box can drive a UPS on USB.</summary>
/// <param name="Settings">The saved settings, without the password.</param>
/// <param name="State">What the box knows about the UPS right now.</param>
/// <param name="UsbAvailable">The NUT server is installed, so the mode <c>usb</c> works (always on an installed box).</param>
public sealed record UpsStatus(UpsSettingsView Settings, UpsState State, bool UsbAvailable);
