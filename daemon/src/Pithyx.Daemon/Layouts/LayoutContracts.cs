using System.Text.Json;

namespace Pithyx.Daemon.Layouts;

/// <param name="Layout">The saved layout of this device (schema in the desktop, <c>version</c> 1), or null.</param>
/// <param name="LayoutVersion">
/// Increments with every save of this device's layout, 0 without one. Send it as <c>If-Match: "n"</c> with the next save.
/// </param>
/// <param name="Preferences">Window preferences of the user for every app, shared across devices.</param>
/// <param name="Appearance">Accent color and wallpaper of the user, the same on every device.</param>
public sealed record DesktopState(JsonElement? Layout, long LayoutVersion, IReadOnlyList<WindowPreference> Preferences, Appearance Appearance);

/// <summary>How the desktop looks for the user on every device. The theme (light, dark) is a choice of each device.</summary>
/// <param name="Accent">One of the accent presets: <c>petrol</c>, <c>ocean</c>, <c>indigo</c>, <c>plum</c>, <c>moss</c>, <c>graphite</c>.</param>
/// <param name="Wallpaper"><c>boxes</c> (the Pithyx boxes) or <c>plain</c>.</param>
public sealed record Appearance(string Accent, string Wallpaper)
{
    public static readonly Appearance Default = new("petrol", "boxes");

    public static readonly IReadOnlyList<string> Accents = ["petrol", "ocean", "indigo", "plum", "moss", "graphite"];

    public static readonly IReadOnlyList<string> Wallpapers = ["boxes", "plain"];

    public bool IsValid() => Accents.Contains(Accent) && Wallpapers.Contains(Wallpaper);
}

/// <param name="Layout">The layout now saved for this device.</param>
/// <param name="Version">Its version, for <c>If-Match</c> on the next save.</param>
public sealed record SavedLayout(JsonElement Layout, long Version);

/// <param name="AppId">The app the preference belongs to.</param>
/// <param name="Width">Last free width of the app's windows in CSS pixels.</param>
/// <param name="Height">Last free height in CSS pixels.</param>
public sealed record WindowPreference(string AppId, int Width, int Height);

/// <summary>Another device of the user with a saved layout.</summary>
/// <param name="DeviceId">Pass to <c>POST /api/desktop/layout/copy</c>.</param>
/// <param name="Browser">Browser product name from the User-Agent, if known.</param>
/// <param name="Os">Operating system name from the User-Agent, if known.</param>
/// <param name="WindowCount">Windows in the layout.</param>
/// <param name="UpdatedAt">When the layout was last saved (UTC).</param>
/// <param name="ScreenWidth">Width of the desktop the layout was saved on, in CSS pixels, if the layout says.</param>
/// <param name="ScreenHeight">Its height.</param>
public sealed record DeviceLayout(string DeviceId, string? Browser, string? Os, long WindowCount, DateTimeOffset UpdatedAt, long? ScreenWidth, long? ScreenHeight);

public sealed record CopyLayoutRequest(string DeviceId);

public sealed record WindowPreferenceRequest(int Width, int Height);
