using System.Globalization;
using System.Text.Json;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Users;

namespace Pithyx.Daemon.FileServices;

/// <summary>
/// Ends the SMB sessions of users whose groups changed. smbd reads a user's groups once, when the session starts, and
/// a client such as Explorer keeps its session for hours, so a new grant would not work and a revoked one would keep
/// working until the user signs out of the computer. Ending the session's smbd process makes the client reconnect at
/// once with the current groups; open files are closed like after a short network outage.
/// </summary>
public sealed partial class SmbSessions(ICommandRunner commands, AccountOptions options, ILogger<SmbSessions> logger)
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    /// <summary>The smbd processes that serve a session of one of <paramref name="users"/>, from <c>smbstatus --json</c>.</summary>
    public static IReadOnlyList<int> Processes(string json, IReadOnlySet<string> users)
    {
        ArgumentNullException.ThrowIfNull(users);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("sessions", out var sessions) || sessions.ValueKind != JsonValueKind.Object)
        {
            return [];
        }
        var processes = new SortedSet<int>();
        foreach (var session in sessions.EnumerateObject())
        {
            if (session.Value.TryGetProperty("username", out var username) && users.Contains(username.GetString() ?? "")
                && session.Value.TryGetProperty("server_id", out var server) && server.TryGetProperty("pid", out var pid)
                && int.TryParse(pid.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                processes.Add(number);
            }
        }
        return [.. processes];
    }

    /// <summary>Ends every SMB session of <paramref name="users"/>; nothing happens in simulation or without Samba.</summary>
    public async Task CloseAsync(IReadOnlySet<string> users, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(users);
        if (users.Count == 0 || !options.Apply || !FileServiceSync.Installed("smbd"))
        {
            return;
        }
        var status = await commands.RunAsync(new CommandRequest("smbstatus", ["--json"], _timeout), cancellationToken);
        if (status.ExitCode != 0)
        {
            return;
        }
        foreach (var process in Processes(status.StandardOutput, users))
        {
            var pid = process.ToString(CultureInfo.InvariantCulture);
            LogClosing(pid);
            await commands.RunAsync(new CommandRequest("smbcontrol", [pid, "shutdown"], _timeout), cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ending the SMB session in smbd {Pid}: its user's groups changed")]
    private partial void LogClosing(string pid);
}
