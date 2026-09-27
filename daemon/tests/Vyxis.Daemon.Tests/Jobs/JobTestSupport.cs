using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Vyxis.Daemon.Auth;
using Vyxis.Daemon.Database;
using Vyxis.Daemon.Jobs;
using Vyxis.Daemon.Tests.Support;
using static Vyxis.Daemon.Tests.Auth.AuthApi;

namespace Vyxis.Daemon.Tests.Jobs;

internal static class JobTestSupport
{
    /// <summary>A user row; user management comes with M3, so it is inserted directly.</summary>
    public static async Task<long> InsertUserAsync(this DaemonFactory factory, string username, bool admin = false)
    {
        await using var connection = await factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token);
        return await connection.ExecuteScalarAsync<long>(
            "INSERT INTO users (username, password_hash, is_admin, created_at, updated_at) VALUES (@username, 'x', @admin, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z') RETURNING id",
            new { username, admin = admin ? 1 : 0 });
    }

    /// <summary>A Cookie header value with a new session of the user.</summary>
    public static async Task<string> SessionCookieAsync(this DaemonFactory factory, long userId)
    {
        var session = await factory.Services.GetRequiredService<SessionService>().CreateAsync(userId, null, Token);
        return $"{SessionService.CookieName}={session.Token}";
    }

    /// <summary>A client that sends the given session cookie and the CSRF header.</summary>
    public static HttpClient ClientWithCookie(this DaemonFactory factory, string cookie)
    {
        var client = factory.CreateClient(new() { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        client.DefaultRequestHeaders.Add("X-Vyxis-Request", "1");
        return client;
    }

    /// <summary>The signed-in user as the session handler builds it.</summary>
    public static ClaimsPrincipal Principal(long userId, bool admin = false) => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture)),
            .. admin ? [new Claim(ClaimTypes.Role, SessionAuthenticationHandler.AdminRole)] : Array.Empty<Claim>(),
        ],
        SessionAuthenticationHandler.SchemeName));

    /// <summary>Work that runs until <paramref name="gate"/> completes, or gives up when the job is cancelled.</summary>
    public static Func<JobContext, Task> Gated(TaskCompletionSource gate) => context => gate.Task.WaitAsync(context.Cancellation);

    public static async Task<Job> WaitForStateAsync(this JobService jobs, long jobId, JobState state)
    {
        var owner = Principal(0, admin: true);
        Job? job = null;
        await WaitUntilAsync(async () => (job = await jobs.GetAsync(owner, jobId, Token)).State == state, $"job {jobId} to become {state}");
        return job!;
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Timed out waiting for {what}.");
            await Task.Delay(10, Token);
        }
    }

    public static async Task<T> ScalarAsync<T>(this DaemonFactory factory, string sql, object? parameters = null)
    {
        await using var connection = await factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync(Token);
        return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
    }
}
