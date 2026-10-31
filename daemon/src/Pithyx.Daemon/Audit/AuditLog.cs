using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Logs;

namespace Pithyx.Daemon.Audit;

/// <summary>
/// What administrators did with elevated rights of apps, and every step-up (D311): a row of <c>audit_log</c> in
/// <c>pithyx.db</c>, which neither the Log Center's retention nor a restore removes, and the same entry in the Log Center
/// under the source <c>audit</c>.
/// </summary>
public sealed class AuditLog(SqliteConnectionFactory connections, LogStore logs, TimeProvider time)
{
    public const string Source = "audit";

    public const string StepUpConfirmed = "auth.step_up.confirmed";
    public const string StepUpFailed = "auth.step_up.failed";
    public const string ElevatedGranted = "app.elevated.granted";
    public const string ElevatedRevoked = "app.elevated.revoked";

    /// <summary>Records what <paramref name="actor"/> did; null for the box itself.</summary>
    public async Task RecordAsync(ClaimsPrincipal? actor, string action, string? target, IReadOnlyDictionary<string, string>? parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        var now = time.GetUtcNow();
        var userId = actor?.FindFirstValue(ClaimTypes.NameIdentifier) is { } id ? long.Parse(id, CultureInfo.InvariantCulture) : (long?)null;
        var username = actor?.Identity?.Name;
        var requestId = Activity.Current?.TraceId.ToString();
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO audit_log (at, user_id, username, action, target, params, request_id) VALUES (@at, @userId, @username, @action, @target, @params, @requestId)",
                new
                {
                    at = SessionService.Format(now),
                    userId,
                    username,
                    action,
                    target,
                    @params = parameters is null ? null : JsonSerializer.Serialize(parameters),
                    requestId,
                },
                cancellationToken: cancellationToken));
        }
        var data = new Dictionary<string, string>(parameters ?? new Dictionary<string, string>(), StringComparer.Ordinal) { ["action"] = action };
        if (target is not null)
        {
            data["target"] = target;
        }
        logs.Enqueue(new PendingLogEntry(
            now,
            action == StepUpFailed ? LogLevelName.Warn : LogLevelName.Info,
            Source,
            Message(action, username ?? "the box", target, parameters),
            requestId,
            username,
            data));
    }

    private static string Message(string action, string who, string? target, IReadOnlyDictionary<string, string>? parameters)
    {
        var right = parameters?.GetValueOrDefault("right");
        return action switch
        {
            StepUpConfirmed => $"{who} confirmed a step-up with the password",
            StepUpFailed => $"{who} failed a step-up",
            ElevatedGranted => $"{who} granted {right} to the app {target}",
            ElevatedRevoked => $"{who} revoked {right} from the app {target}",
            _ => $"{who}: {action} {target}",
        };
    }
}
