using Pithyx.Daemon.Notifications;

namespace Pithyx.Daemon.Auth;

/// <summary>
/// Tells the administrators when failed sign-ins reach the limit of <see cref="LoginRateLimiter"/> (D379), at most once an
/// hour for the same address and name, so a password guesser shows up in the desktop and on the box's channels.
/// </summary>
public sealed partial class SignInAlerts(LoginRateLimiter limiter, NotificationService notifications, TimeProvider time, ILogger<SignInAlerts> logger) : IHostedService
{
    public const string BlockedCode = "auth.signin.blocked";
    public static readonly TimeSpan Quiet = TimeSpan.FromHours(1);

    private readonly Dictionary<(string Address, string? Username), DateTimeOffset> _told = [];
    private readonly Lock _lock = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        limiter.Blocked += OnBlocked;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        limiter.Blocked -= OnBlocked;
        return Task.CompletedTask;
    }

    private void OnBlocked(SignInBlock block)
    {
        var now = time.GetUtcNow();
        var key = (block.Address, block.Username?.ToLowerInvariant());
        lock (_lock)
        {
            if (_told.TryGetValue(key, out var last) && last + Quiet > now)
            {
                return;
            }
            _told[key] = now;
            foreach (var old in _told.Where(entry => entry.Value + Quiet <= now).Select(entry => entry.Key).ToList())
            {
                _told.Remove(old);
            }
        }
        LogBlocked(block.Address, block.Username, block.Failures);
        var draft = new NotificationDraft(BlockedCode, new Dictionary<string, object?>
        {
            ["address"] = block.Address,
            ["user"] = block.Username,
            ["failures"] = block.Failures,
            ["minutes"] = (int)LoginRateLimiter.Window.TotalMinutes,
        }, NotificationTone.Warning);
        _ = Task.Run(async () =>
        {
            try
            {
                await notifications.CreateForAdministratorsAsync(draft, CancellationToken.None);
            }
            catch (Exception exception)
            {
                LogNotifyFailed(exception);
            }
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sign-ins from {Address} blocked after {Failures} failures (user {Username})")]
    private partial void LogBlocked(string address, string? username, int failures);

    [LoggerMessage(Level = LogLevel.Error, Message = "Telling the administrators about blocked sign-ins failed")]
    private partial void LogNotifyFailed(Exception exception);
}
