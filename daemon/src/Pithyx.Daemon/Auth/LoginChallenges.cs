using System.Collections.Concurrent;

namespace Pithyx.Daemon.Auth;

/// <summary>
/// The second login step for users with TOTP: after the password check the client gets a short-lived challenge that
/// must be completed with a code from the same client address within five minutes.
/// </summary>
public sealed class LoginChallenges(TimeProvider time)
{
    private static readonly TimeSpan _lifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed record Entry(long UserId, string Username, string Address, DateTimeOffset ExpiresAt);

    public string Create(long userId, string username, string address)
    {
        RemoveExpired();
        var token = Secrets.NewToken();
        _entries[Convert.ToHexString(Secrets.Sha256(token))] = new Entry(userId, username, address, time.GetUtcNow() + _lifetime);
        return token;
    }

    /// <summary>Returns the pending login without consuming it, so a mistyped code can be retried.</summary>
    public (long UserId, string Username)? Find(string token, string address)
    {
        RemoveExpired();
        return _entries.TryGetValue(Convert.ToHexString(Secrets.Sha256(token)), out var entry) && entry.Address == address
            ? (entry.UserId, entry.Username)
            : null;
    }

    public void Complete(string token) => _entries.TryRemove(Convert.ToHexString(Secrets.Sha256(token)), out _);

    private void RemoveExpired()
    {
        var now = time.GetUtcNow();
        foreach (var (key, entry) in _entries)
        {
            if (entry.ExpiresAt <= now)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }
}
