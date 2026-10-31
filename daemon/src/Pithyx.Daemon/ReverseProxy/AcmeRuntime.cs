using System.Collections.Concurrent;

namespace Pithyx.Daemon.ReverseProxy;

/// <summary>
/// What the proxy and the ACME client share while the daemon runs (D378): the HTTP-01 answers port 80 gives the
/// authority, the routes whose certificate is being requested, and the signal that a route's certificate changed.
/// </summary>
public sealed class AcmeRuntime : IDisposable
{
    public const string ChallengePath = "/.well-known/acme-challenge/";

    private readonly ConcurrentDictionary<string, string> _challenges = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _running = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>The key authorization for an HTTP-01 token the box asked the authority to check, if any.</summary>
    public string? Answer(string token) => _challenges.GetValueOrDefault(token);

    public void Offer(string token, string keyAuthorization) => _challenges[token] = keyAuthorization;

    public void Withdraw(string token) => _challenges.TryRemove(token, out _);

    public bool IsRunning(string route) => _running.ContainsKey(route);

    /// <summary>Marks a route as being requested; false if it already is.</summary>
    public bool Start(string route) => _running.TryAdd(route, true);

    public void Finish(string route) => _running.TryRemove(route, out _);

    /// <summary>A route's certificate source or names changed: look at the routes now.</summary>
    public void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already woken.
        }
    }

    /// <summary>Waits for <see cref="Wake"/> or the delay, whichever comes first.</summary>
    public async Task WaitAsync(TimeSpan delay, TimeProvider time, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayed = Task.Delay(delay, time, timeout.Token);
        var woken = _wake.WaitAsync(timeout.Token);
        await Task.WhenAny(delayed, woken);
        await timeout.CancelAsync();
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void Dispose() => _wake.Dispose();
}
