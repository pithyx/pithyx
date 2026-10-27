using System.Collections.Concurrent;

namespace Pithyx.Daemon.Apps.Proxy;

/// <summary>
/// Where the endpoints of running app backends answer, by app and endpoint name: the address of the container and the
/// port of the manifest's <c>backend.endpoints</c>, never a URL an app sees (D303). The Docker runtime sets an app
/// running once its containers are healthy and stopped when they go; tests point it at their own servers. The proxy
/// reads it per request, so a change applies to the next request.
/// </summary>
public sealed class AppBackends
{
    public const string Stopped = "stopped";
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Updating = "updating";
    public const string Unavailable = "unavailable";

    /// <summary>The reason of an <see cref="Unavailable"/> backend whose required storage request is not mapped.</summary>
    public const string ReasonStorage = "storage";

    /// <summary>The reason of an <see cref="Unavailable"/> backend whose app pool is locked or missing.</summary>
    public const string ReasonPool = "pool";

    /// <summary>A required device of the app is not plugged in (D387).</summary>
    public const string ReasonDevice = "device";

    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Uri>> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _status = new(StringComparer.Ordinal);

    /// <summary>Raised with the app's id after it started or stopped.</summary>
    public event Action<string>? Changed;

    /// <summary>The app's backend answers at these base addresses, one per endpoint name.</summary>
    public void SetRunning(string appId, IReadOnlyDictionary<string, Uri> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.Values.Any(address => !address.IsAbsoluteUri || address.Scheme is not ("http" or "https")))
        {
            throw new ArgumentException("Endpoints need absolute http or https addresses.", nameof(endpoints));
        }
        _running[appId] = new Dictionary<string, Uri>(endpoints, StringComparer.Ordinal);
        _status[appId] = Running;
        Changed?.Invoke(appId);
    }

    /// <summary>The app's backend no longer answers.</summary>
    public void SetStopped(string appId)
    {
        var removed = _running.TryRemove(appId, out _);
        if ((_status.TryRemove(appId, out var status) && status != Stopped) || removed)
        {
            Changed?.Invoke(appId);
        }
    }

    /// <summary>
    /// Marks a backend that is on its way or in trouble (<see cref="Starting"/>, <see cref="Updating"/>,
    /// <see cref="Unavailable"/>), or healthy again (<see cref="Running"/>, only while it runs); the proxy's addresses stay
    /// as they are.
    /// </summary>
    public void SetStatus(string appId, string status)
    {
        if (status == Running && !_running.ContainsKey(appId))
        {
            return;
        }
        if (_status.GetValueOrDefault(appId, Stopped) != status)
        {
            _status[appId] = status;
            Changed?.Invoke(appId);
        }
    }

    /// <summary>The status the SDK shows the app (<c>context.backend.status</c>, <c>backendStatus</c>).</summary>
    public string StatusOf(string appId) => _status.GetValueOrDefault(appId, Stopped);

    public bool IsRunning(string appId) => _running.ContainsKey(appId);

    /// <summary>The base address of a running endpoint, or null.</summary>
    public Uri? Find(string appId, string endpoint) =>
        _running.TryGetValue(appId, out var endpoints) ? endpoints.GetValueOrDefault(endpoint) : null;
}
