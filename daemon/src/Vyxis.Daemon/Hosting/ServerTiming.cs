using System.Diagnostics;
using System.Globalization;

namespace Vyxis.Daemon.Hosting;

/// <summary>
/// Reports the time spent in Argon2 as <c>Server-Timing: argon2;dur=165.2</c>, so sign-in time can be split into the
/// password check and everything else (the performance budgets exclude the password check).
/// </summary>
public static class ServerTiming
{
    private const string Tag = "vyxis.argon2.ms";

    /// <summary>Adds a password hash or check to the current request.</summary>
    public static void AddArgon2(TimeSpan elapsed)
    {
        var activity = Activity.Current;
        if (activity is not null)
        {
            var total = activity.GetTagItem(Tag) is double milliseconds ? milliseconds : 0;
            activity.SetTag(Tag, total + elapsed.TotalMilliseconds);
        }
    }

    /// <summary>The header value for a request, or null when it did not hash a password.</summary>
    public static string? HeaderFor(Activity? activity) =>
        activity?.GetTagItem(Tag) is double milliseconds
            ? string.Create(CultureInfo.InvariantCulture, $"argon2;dur={milliseconds:0.#}")
            : null;
}
