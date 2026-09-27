namespace Pithyx.Daemon.Layouts;

/// <summary>Browser and operating system of a device, as product names that need no translation.</summary>
public sealed record DeviceNames(string? Browser, string? Os);

public static class DeviceNamesParser
{
    // Order matters: Edge and Opera also send "Chrome", Chrome also sends "Safari", Android also sends "Linux".
    private static readonly (string Token, string Name)[] _browsers =
    [
        ("Edg/", "Edge"),
        ("OPR/", "Opera"),
        ("Firefox/", "Firefox"),
        ("FxiOS/", "Firefox"),
        ("CriOS/", "Chrome"),
        ("Chrome/", "Chrome"),
        ("Safari/", "Safari"),
    ];

    private static readonly (string Token, string Name)[] _systems =
    [
        ("Android", "Android"),
        ("iPhone", "iOS"),
        ("iPad", "iPadOS"),
        ("Windows", "Windows"),
        ("Mac OS X", "macOS"),
        ("CrOS", "ChromeOS"),
        ("Linux", "Linux"),
    ];

    /// <summary>Names from a User-Agent header; either is null when it cannot be told.</summary>
    public static DeviceNames Parse(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
        {
            return new DeviceNames(null, null);
        }
        return new DeviceNames(Find(_browsers, userAgent), Find(_systems, userAgent));
    }

    private static string? Find((string Token, string Name)[] candidates, string userAgent)
    {
        foreach (var (token, name) in candidates)
        {
            if (userAgent.Contains(token, StringComparison.Ordinal))
            {
                return name;
            }
        }
        return null;
    }
}
