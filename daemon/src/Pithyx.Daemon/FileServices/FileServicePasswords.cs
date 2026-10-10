using System.Collections.Concurrent;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Users;

namespace Pithyx.Daemon.FileServices;

/// <summary>
/// The passwords SMB and SFTP check (D287). Pithyx keeps only a hash of its own, so the Linux account (for SFTP,
/// through PAM) and the Samba account get the password whenever the daemon has it in plain text: setup, sign-in, a new
/// user, an administrator's reset and recovery. A user who never signed in after the update has none yet; the Users
/// page says so. The password goes to <c>chpasswd</c> and <c>smbpasswd</c> on stdin, never as an argument and never into
/// a log. The Samba accounts are listed with <c>pdbedit</c> at start and after every change, never per request.
/// </summary>
public sealed partial class FileServicePasswords(ICommandRunner commands, AccountOptions options, ILogger<FileServicePasswords> logger)
{
    private IReadOnlyDictionary<string, PassdbEntry> _passdb = new Dictionary<string, PassdbEntry>(StringComparer.Ordinal);

    // Simulation runs no command; it remembers whose password it would have set.
    private readonly ConcurrentDictionary<string, bool> _simulated = new(StringComparer.Ordinal);

    /// <summary>The Samba accounts as last listed.</summary>
    public IReadOnlyDictionary<string, PassdbEntry> Passdb => Volatile.Read(ref _passdb);

    /// <summary>Whether SMB and SFTP know the user's password: the Samba account has one and the Linux account is not locked.</summary>
    public bool HasPassword(string username, IReadOnlySet<string> linuxWithPassword)
    {
        ArgumentNullException.ThrowIfNull(linuxWithPassword);
        if (!options.Apply)
        {
            return _simulated.ContainsKey(username);
        }
        return Passdb.TryGetValue(username, out var entry) && entry.HasPassword && linuxWithPassword.Contains(username);
    }

    /// <summary>The Linux accounts with a usable password; a daemon that is not root sees none.</summary>
    public IReadOnlySet<string> LinuxWithPassword()
    {
        try
        {
            return options.Apply && File.Exists(options.ShadowPath) ? ShadowParser.WithPassword(File.ReadAllText(options.ShadowPath)) : new HashSet<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return new HashSet<string>();
        }
    }

    /// <summary>Gives the Linux and the Samba account this password. Never throws but for cancellation; a failure is logged.</summary>
    public async Task SetAsync(string username, string password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentNullException.ThrowIfNull(password);
        // Both tools read a line per password; a line break or NUL would cut it.
        if (password.Any(c => c is '\n' or '\r' or '\0'))
        {
            LogUnsupported(username);
            return;
        }
        if (!options.Apply)
        {
            _simulated[username] = true;
            return;
        }
        try
        {
            var linux = await commands.RunAsync(new CommandRequest("chpasswd", [], StandardInput: $"{username}:{password}\n"), cancellationToken);
            // -a adds the Samba account or changes its password; -s reads it twice from stdin.
            var samba = await commands.RunAsync(new CommandRequest("smbpasswd", ["-s", "-a", username], StandardInput: $"{password}\n{password}\n"), cancellationToken);
            if (linux.ExitCode != 0 || samba.ExitCode != 0)
            {
                LogFailed(username, linux.ExitCode, samba.ExitCode);
            }
            await RefreshAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogError(exception, username);
        }
    }

    /// <summary>Sets the password in the background when SMB or SFTP do not know it yet, for a sign-in that should not wait.</summary>
    public void SetIfMissing(string username, string password)
    {
        if (HasPassword(username, LinuxWithPassword()))
        {
            return;
        }
        _ = Task.Run(() => SetAsync(username, password, CancellationToken.None));
    }

    /// <summary>Lists the Samba accounts again.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!options.Apply)
        {
            return;
        }
        var result = await commands.RunAsync(new CommandRequest("pdbedit", ["-L", "-w"], SecretOutput: true), cancellationToken);
        if (result.ExitCode == 0)
        {
            Volatile.Write(ref _passdb, PassdbParser.Parse(result.StandardOutput).GroupBy(entry => entry.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// Removes the Samba accounts of the Pithyx range that no user has any more and disables or enables the others like
    /// their user. Accounts outside the range are never touched.
    /// </summary>
    public async Task ReconcileAsync(IReadOnlyList<(string Name, bool Disabled)> users, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(users);
        if (!options.Apply)
        {
            foreach (var name in _simulated.Keys.Where(name => users.All(user => user.Name != name)))
            {
                _simulated.TryRemove(name, out _);
            }
            return;
        }
        await RefreshAsync(cancellationToken);
        var wanted = users.ToDictionary(user => user.Name, user => user.Disabled, StringComparer.Ordinal);
        var changed = false;
        foreach (var entry in Passdb.Values.Where(entry => AccountReconciler.IsManaged(entry.Uid)))
        {
            var command = !wanted.TryGetValue(entry.Name, out var disabled)
                ? new CommandRequest("pdbedit", ["-x", "-u", entry.Name])
                : disabled != entry.Disabled ? new CommandRequest("smbpasswd", [disabled ? "-d" : "-e", entry.Name]) : null;
            if (command is not null)
            {
                await commands.RunAsync(command, cancellationToken);
                changed = true;
            }
        }
        if (changed)
        {
            await RefreshAsync(cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The password of {Username} has a line break; SMB and SFTP do not get it")]
    private partial void LogUnsupported(string username);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setting the SMB and SFTP password of {Username} failed (chpasswd {LinuxExit}, smbpasswd {SambaExit})")]
    private partial void LogFailed(string username, int linuxExit, int sambaExit);

    [LoggerMessage(Level = LogLevel.Error, Message = "Setting the SMB and SFTP password of {Username} failed")]
    private partial void LogError(Exception exception, string username);
}
