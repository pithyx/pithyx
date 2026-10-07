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
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Uri>> _running = new(StringComparer.Ordinal);

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
        Changed?.Invoke(appId);
    }

    /// <summary>The app's backend no longer answers.</summary>
    public void SetStopped(string appId)
    {
        if (_running.TryRemove(appId, out _))
        {
            Changed?.Invoke(appId);
        }
    }

    public bool IsRunning(string appId) => _running.ContainsKey(appId);

    /// <summary>The base address of a running endpoint, or null.</summary>
    public Uri? Find(string appId, string endpoint) =>
        _running.TryGetValue(appId, out var endpoints) ? endpoints.GetValueOrDefault(endpoint) : null;
}
