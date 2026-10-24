using System.Text.Json;

namespace Pithyx.Daemon.Layouts;

/// <param name="Layout">The saved layout of this device (schema in the desktop, <c>version</c> 1), or null.</param>
/// <param name="LayoutVersion">
/// Increments with every save of this device's layout, 0 without one. Send it as <c>If-Match: "n"</c> with the next save.
/// </param>
/// <param name="Preferences">Window preferences of the user for every app, shared across devices.</param>
/// <param name="Appearance">Accent color and wallpaper of the user, the same on every device.</param>
/// <param name="FileDefaults">How the user opens files of an extension, by extension; the same on every device.</param>
/// <param name="StartupApps">The apps the user opens after signing in, in order (D373); the same on every device.</param>
public sealed record DesktopState(
    JsonElement? Layout,
    long LayoutVersion,
    IReadOnlyList<WindowPreference> Preferences,
    Appearance Appearance,
    IReadOnlyList<FileDefault> FileDefaults,
    IReadOnlyList<string> StartupApps);

/// <summary>How the user opens files of one extension, chosen with "Always use this app" (D371); the same on every device.</summary>
/// <param name="Extension">The extension in lowercase without the dot, as in the manifest's <c>fileHandlers</c>, for example <c>pdf</c> or <c>tar.gz</c>.</param>
/// <param name="Handler"><c>viewer</c>, <c>download</c> or <c>app:</c> and an app id. The desktop ignores an app that is gone or no longer handles the extension.</param>
public sealed partial record FileDefault(string Extension, string Handler)
{
    public const int MaxPerUser = 200;

    public bool IsValid() =>
        Extension is { Length: > 0 and <= 32 } && ExtensionPattern().IsMatch(Extension)
        && (Handler is "viewer" or "download"
            || (Handler.StartsWith("app:", StringComparison.Ordinal) && Apps.Manifest.AppId.IsValid(Handler["app:".Length..])));

    // The pattern of a file handler's extensions in the manifest schema.
    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9]+([.+_-][a-z0-9]+)*$")]
    private static partial System.Text.RegularExpressions.Regex ExtensionPattern();
}

/// <param name="Handler"><c>viewer</c>, <c>download</c> or <c>app:</c> and an app id.</param>
public sealed record FileDefaultRequest(string Handler);

/// <summary>The apps the user opens after signing in (D373); the same on every device.</summary>
/// <param name="Apps">App ids in the order they open, each once, at most <see cref="Max"/>. Empty opens nothing.</param>
public sealed record StartupApps(IReadOnlyList<string> Apps)
{
    public const int Max = 10;

    public bool IsValid() =>
        Apps is not null && Apps.Count <= Max && Apps.All(Pithyx.Daemon.Apps.Manifest.AppId.IsValid) && Apps.Distinct(StringComparer.Ordinal).Count() == Apps.Count;
}

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
