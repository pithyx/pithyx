using System.Security.Claims;
using System.Text.Json;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Realtime;

namespace Pithyx.Daemon.Storage;

/// <summary>The pools and datasets as last read by <see cref="SystemStateService"/>. Reads never run a command.</summary>
public sealed class PoolInventory
{
    public const string Topic = "storage/pools";

    private PoolList _current = new([], []);
    private string _fingerprint = "";

    public PoolList Current => Volatile.Read(ref _current);

    /// <summary>False until the first successful read, for example when ZFS is not installed.</summary>
    public bool Available { get; private set; }

    /// <summary>Replaces the state; true when anything changed.</summary>
    public bool Replace(PoolList pools)
    {
        ArgumentNullException.ThrowIfNull(pools);
        Available = true;
        // Records compare lists by reference, so content is compared through its JSON form.
        var fingerprint = JsonSerializer.Serialize(pools);
        if (fingerprint == _fingerprint)
        {
            return false;
        }
        _fingerprint = fingerprint;
        Volatile.Write(ref _current, pools);
        return true;
    }

    /// <summary>Administrators only: pool layouts name every disk and dataset on the box.</summary>
    public static bool IsAllowed(ClaimsPrincipal user, string topic) =>
        user?.Identity?.IsAuthenticated == true && user.IsInRole(SessionAuthenticationHandler.AdminRole);

    public TopicDefinition Definition() => new(
        Topic,
        typeof(PoolList),
        typeof(PoolList),
        (_, _) => ValueTask.FromResult<object>(Current),
        IsAllowed);
}
