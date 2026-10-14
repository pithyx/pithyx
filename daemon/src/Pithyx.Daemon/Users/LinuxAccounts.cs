using Dapper;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;

namespace Pithyx.Daemon.Users;

/// <param name="PasswdPath">Where the accounts are read: <c>/etc/passwd</c>, a fixture in simulation.</param>
/// <param name="Apply">Whether commands run; false in simulation, where only the plan is made.</param>
/// <param name="ShadowPath">Where the account expiry is read: <c>/etc/shadow</c>; a missing file means no account expires.</param>
/// <param name="GroupPath">Where the groups are read: <c>/etc/group</c>, a fixture in simulation.</param>
public sealed record AccountOptions(string PasswdPath, bool Apply, string ShadowPath = "/etc/shadow", string GroupPath = "/etc/group");

/// <summary>
/// Keeps the Linux accounts in line with the users in SQLite (step 10): at start and after every change, the
/// difference (<see cref="AccountReconciler"/>) runs as <c>useradd</c>, <c>usermod</c> and <c>userdel</c>, only for
/// accounts in the Pithyx range, and the groups the same way with <c>groupadd</c>, <c>groupmod</c>, <c>gpasswd</c> and
/// <c>groupdel</c> (<see cref="GroupReconciler"/>), only for groups in the Pithyx range. A user's home directory is its
/// home (<see cref="Homes"/>).
/// </summary>
public sealed partial class LinuxAccounts(
    SqliteConnectionFactory connections,
    ICommandRunner commands,
    AccountOptions options,
    ILogger<LinuxAccounts> logger,
    FileServices.SmbSessions smbSessions,
    TimeProvider? time = null) : IDisposable
{
    private readonly SemaphoreSlim _running = new(1, 1);

    public void Dispose() => _running.Dispose();

    public IReadOnlyList<PasswdEntry> Actual() =>
        File.Exists(options.PasswdPath) ? PasswdParser.Parse(File.ReadAllText(options.PasswdPath)) : [];

    public IReadOnlyList<FileServices.GroupEntry> ActualGroups() =>
        File.Exists(options.GroupPath) ? FileServices.GroupParser.Parse(File.ReadAllText(options.GroupPath)) : [];

    /// <summary>
    /// The accounts with an expiry date. A daemon that is not root cannot read <c>/etc/shadow</c> (and cannot change
    /// accounts either); it sees none.
    /// </summary>
    private IReadOnlySet<string> Expiring()
    {
        try
        {
            return File.Exists(options.ShadowPath) ? ShadowParser.Expiring(File.ReadAllText(options.ShadowPath)) : new HashSet<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return new HashSet<string>();
        }
    }

    /// <summary>
    /// Every user's account. A user with the SSH login right gets a shell while SSH logins are switched on; everybody
    /// else <c>nologin</c>.
    /// </summary>
    public async Task<IReadOnlyList<WantedAccount>> WantedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var users = await connection.QueryAsync<(long Id, string Username, string? DisplayName, long? Uid, string? DisabledAt, string? Home)>(
            """
            SELECT users.id, users.username, users.display_name, users.uid, users.disabled_at, shares.path FROM users
            LEFT JOIN shares ON shares.home = 1 AND shares.home_user_id = users.id
            WHERE users.uid IS NOT NULL ORDER BY users.uid
            """);
        var ssh = await connection.ExecuteScalarAsync<long>("SELECT ssh_enabled FROM file_services WHERE id = 1") == 1;
        var rights = (await Permissions.PermissionService.AllUsersAsync(connection, null)).ToDictionary(user => user.Id, user => user.Permissions);
        return [.. users.Select(user => new WantedAccount(
            user.Username,
            user.Uid!.Value,
            AccountReconciler.Comment(user.DisplayName),
            user.Home ?? AccountReconciler.NoHome,
            ssh && rights[user.Id].Has(Permissions.Resources.SshLogin) ? AccountReconciler.LoginShell : AccountReconciler.NoLogin,
            user.DisabledAt is not null))];
    }

    /// <summary>
    /// The groups in the database with their members, and the two groups of every shared folder
    /// (<see cref="Permissions.ShareGroups"/>); a member whose account is not in <paramref name="accounts"/> (refused or
    /// not created yet) is left out. The built-in group <c>users</c> is the system's and never wanted here.
    /// </summary>
    public async Task<IReadOnlyList<WantedGroup>> WantedGroupsAsync(IReadOnlySet<string> accounts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        await Permissions.ShareGroups.AllocateAsync(connections, time ?? TimeProvider.System, cancellationToken);
        await using var connection = await connections.OpenAsync(cancellationToken);
        var shares = (await connection.QueryAsync<(long Id, long ReadGid, long WriteGid)>(
            "SELECT id, read_gid, write_gid FROM shares WHERE dataset IS NOT NULL AND read_gid IS NOT NULL AND write_gid IS NOT NULL ORDER BY id")).ToList();
        var shareGroups = Permissions.ShareGroups.Wanted(shares, await Permissions.PermissionService.AllUsersAsync(connection, null), accounts);
        var groups = await connection.QueryAsync<(long Id, string Name)>("SELECT id, name FROM groups WHERE builtin = 0 ORDER BY id");
        var members = (await connection.QueryAsync<(long GroupId, string Username)>(
            "SELECT group_members.group_id, users.username FROM group_members JOIN users ON users.id = group_members.user_id"))
            .ToLookup(member => member.GroupId, member => member.Username);
        return [
            .. groups.Select(group => new WantedGroup(
                group.Name,
                group.Id,
                [.. members[group.Id].Where(accounts.Contains).Order(StringComparer.Ordinal)])),
            .. shareGroups,
        ];
    }

    /// <summary>Plans and, in real mode, runs the difference of the accounts, then of the groups; returns the refusals and failures by name.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ReconcileAsync(CancellationToken cancellationToken)
    {
        await _running.WaitAsync(cancellationToken);
        try
        {
            var problems = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var action in AccountReconciler.Plan(await WantedAsync(cancellationToken), Actual(), Expiring()))
            {
                if (action is RefuseAccount refusal)
                {
                    LogRefused(refusal.Name, refusal.Reason);
                    problems[refusal.Name] = refusal.Reason;
                    continue;
                }
                if (!options.Apply || AccountReconciler.Command(action) is not { } command)
                {
                    continue;
                }
                var result = await commands.RunAsync(command, cancellationToken);
                if (result.ExitCode != 0)
                {
                    problems[action.Name] = "command_failed";
                }
            }
            var accounts = Actual().Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
            var actualGroups = ActualGroups();
            var groupActions = GroupReconciler.Plan(await WantedGroupsAsync(accounts, cancellationToken), actualGroups);
            foreach (var action in groupActions)
            {
                if (action is RefuseGroup refusal)
                {
                    LogGroupRefused(refusal.Name, refusal.Reason);
                    problems[GroupProblemKey(refusal.Name)] = refusal.Reason;
                    continue;
                }
                if (!options.Apply)
                {
                    continue;
                }
                foreach (var command in GroupReconciler.Commands(action))
                {
                    if ((await commands.RunAsync(command, cancellationToken)).ExitCode != 0)
                    {
                        problems[GroupProblemKey(action.Name)] = "command_failed";
                        break;
                    }
                }
            }
            if (options.Apply)
            {
                await smbSessions.CloseAsync(GroupReconciler.ChangedMembers(groupActions, actualGroups), cancellationToken);
            }
            return problems;
        }
        finally
        {
            _running.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Linux account {Name} was not provisioned: {Reason}")]
    private partial void LogRefused(string name, string reason);

    /// <summary>The key of a group's problem in the answer of <see cref="ReconcileAsync"/>; user names cannot contain a colon.</summary>
    public static string GroupProblemKey(string name) => $"group:{name}";

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Linux group {Name} was not provisioned: {Reason}")]
    private partial void LogGroupRefused(string name, string reason);
}
