using System.Collections.Concurrent;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Auth;

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

    /// <summary>A user name at an address (or an address for every name, with a null name) just reached its limit.</summary>
    public event Action<SignInBlock>? Blocked;

    /// <summary>Throws <c>auth.rate.limited</c> with the seconds until the next attempt is allowed.</summary>
    public void EnsureAllowed(HttpContext context, string username)
    {
        ArgumentNullException.ThrowIfNull(context);
        EnsureAllowed(Address(context), username);
    }

    /// <summary>The same for a client of a file protocol, known by its address only.</summary>
    public void EnsureAllowed(string address, string username)
    {
        var now = time.GetUtcNow();
        RemoveExpired(now);
        foreach (var (key, limit) in Keys(address, username))
        {
            if (_counters.TryGetValue(key, out var counter) && counter.Failures >= limit)
            {
                var retryAfter = counter.WindowStart + Window - now;
                throw new PithyxException(
                    ErrorCodes.AuthRateLimited,
                    new Dictionary<string, object?> { ["retryAfterSeconds"] = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)) });
            }
        }
    }

    public void RecordFailure(HttpContext context, string username)
    {
        ArgumentNullException.ThrowIfNull(context);
        RecordFailure(Address(context), username);
    }

    public void RecordFailure(string address, string username)
    {
        var now = time.GetUtcNow();
        var keys = Keys(address, username);
        for (var i = 0; i < keys.Length; i++)
        {
            var (key, limit) = keys[i];
            var counter = _counters.AddOrUpdate(
                key,
                _ => new Counter(now, 1),
                (_, counter) => counter.WindowStart + Window <= now ? new Counter(now, 1) : counter with { Failures = counter.Failures + 1 });
            if (counter.Failures == limit)
            {
                Blocked?.Invoke(new SignInBlock(address, i == 0 ? username.Trim() : null, limit));
            }
        }
    }

    private static string Address(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static (string Key, int Limit)[] Keys(string address, string username)
    {
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

/// <summary>Failed sign-ins that reached a limit: from an address, for one user name or for any.</summary>
public sealed record SignInBlock(string Address, string? Username, int Failures);
