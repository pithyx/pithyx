using System.Text.RegularExpressions;
using Pithyx.Daemon.Logs;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.FileServices;

/// <summary>
/// What SMB and SFTP did, read from the journal the Log Center follows anyway, so writes over them show in the used
/// space like writes through Files (D209) without polling. SMB's audit log (<c>full_audit</c>, one line per write
/// request, delete, rename or new folder) asks for a pool read a few seconds later and hands deletes to the recycle
/// bin. Its lines are not kept in the Log Center. <c>internal-sftp</c> cannot log from its chroot, so SFTP is seen by
/// its sessions: while a Pithyx user has one open, the pools are read once a minute, and once more when it ends.
/// Reading the used space never wakes a disk: ZFS keeps it in memory.
/// </summary>
public sealed partial class FileServiceActivity(StorageUsage usage, TimeProvider time) : IDisposable
{
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan SessionInterval = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _sessions = new(StringComparer.Ordinal);
    private ITimer? _settle;
    private ITimer? _sessionTimer;

    /// <summary>The users with an SFTP view; only their sessions count.</summary>
    public IReadOnlySet<string> SftpUsers { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Raised with the share's name in <c>shares</c> when a file was deleted over SMB.</summary>
    public event Action<string>? SmbDeleted;

    /// <summary>Looks at a journal entry; returns false when it should not be kept in the Log Center.</summary>
    public bool Observe(PendingLogEntry entry, string? identifier)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (identifier == SmbConfig.AuditIdentifier && Audit(entry.Message) is { } audit)
        {
            Written();
            if (audit.Operation == "unlinkat")
            {
                // Samba names every user's home "home"; its row is named after the user (D348).
                SmbDeleted?.Invoke(string.Equals(audit.Share, Users.Homes.OwnName, StringComparison.OrdinalIgnoreCase) ? Users.Homes.RowName(audit.User) : audit.Share);
            }
            return false;
        }
        if (entry.Source == "system/ssh" && Session().Match(entry.Message) is { Success: true } match && SftpUsers.Contains(match.Groups["user"].Value))
        {
            SessionChanged(match.Groups["user"].Value, match.Groups["state"].Value == "opened");
        }
        return true;
    }

    /// <summary>An audit line <c>user|share|operation|ok|path</c>, or null.</summary>
    public static (string User, string Share, string Operation)? Audit(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var fields = message.Split('|');
        return fields.Length >= 5 && fields[3] == "ok" && fields[2] is "pwrite_send" or "unlinkat" or "renameat" or "mkdirat"
            ? (fields[0], fields[1], fields[2])
            : null;
    }

    /// <summary>Asks for one pool read a few seconds after the last of a burst of writes.</summary>
    public void Written()
    {
        lock (_lock)
        {
            _settle ??= time.CreateTimer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _settle.Change(Settle, Timeout.InfiniteTimeSpan);
        }
    }

    private void SessionChanged(string user, bool opened)
    {
        lock (_lock)
        {
            var count = _sessions.GetValueOrDefault(user) + (opened ? 1 : -1);
            if (count > 0)
            {
                _sessions[user] = count;
            }
            else
            {
                _sessions.Remove(user);
            }
            if (_sessions.Count > 0)
            {
                _sessionTimer ??= time.CreateTimer(_ => usage.Written(), null, SessionInterval, SessionInterval);
            }
            else
            {
                _sessionTimer?.Dispose();
                _sessionTimer = null;
            }
        }
        if (!opened)
        {
            Written();
        }
    }

    private void Fire() => usage.Written();

    public void Dispose()
    {
        lock (_lock)
        {
            _settle?.Dispose();
            _sessionTimer?.Dispose();
        }
    }

    // pam_unix(sshd:session): session opened for user ada(uid=20000) by ada(uid=0)
    [GeneratedRegex(@"^pam_unix\(sshd:session\): session (?<state>opened|closed) for user (?<user>[^\s(]+)")]
    private static partial Regex Session();
}
