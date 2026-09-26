using System.Collections.Concurrent;
using Vyxis.Daemon.Errors;

namespace Vyxis.Daemon.Auth;

/// <summary>
/// Limits failed password, TOTP and recovery attempts: five per user name and client address, and thirty per client
/// address, each in a five-minute window. Successful logins do not count, so several devices behind one address can
/// sign in freely.
/// </summary>
public sealed class LoginRateLimiter(TimeProvider time)
{
    public const int FailuresPerUser = 5;
    public const int FailuresPerAddress = 30;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.Ordinal);

    private sealed record Counter(DateTimeOffset WindowStart, int Failures);

    /// <summary>Throws <c>auth.rate.limited</c> with the seconds until the next attempt is allowed.</summary>
    public void EnsureAllowed(HttpContext context, string username)
    {
        var now = time.GetUtcNow();
        RemoveExpired(now);
        foreach (var (key, limit) in Keys(context, username))
        {
            if (_counters.TryGetValue(key, out var counter) && counter.Failures >= limit)
            {
                var retryAfter = counter.WindowStart + Window - now;
                throw new VyxisException(
                    ErrorCodes.AuthRateLimited,
                    new Dictionary<string, object?> { ["retryAfterSeconds"] = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)) });
            }
        }
    }

    public void RecordFailure(HttpContext context, string username)
    {
        var now = time.GetUtcNow();
        foreach (var (key, _) in Keys(context, username))
        {
            _counters.AddOrUpdate(
                key,
                _ => new Counter(now, 1),
                (_, counter) => counter.WindowStart + Window <= now ? new Counter(now, 1) : counter with { Failures = counter.Failures + 1 });
        }
    }

    private static (string Key, int Limit)[] Keys(HttpContext context, string username)
    {
        ArgumentNullException.ThrowIfNull(context);
        var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return [($"user|{address}|{username.Trim().ToLowerInvariant()}", FailuresPerUser), ($"address|{address}", FailuresPerAddress)];
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var (key, counter) in _counters)
        {
            if (counter.WindowStart + Window <= now)
            {
                _counters.TryRemove(key, out _);
            }
        }
    }
}
