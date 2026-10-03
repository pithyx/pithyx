using Dapper;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Zfs;

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
/// <c>groupdel</c> (<see cref="GroupReconciler"/>), only for groups in the Pithyx range. A user's home is a dataset below the first pool's <c>homes</c> (D105), created with the
/// user and never deleted with it.
/// </summary>
public sealed partial class LinuxAccounts(
    SqliteConnectionFactory connections,
    ICommandRunner commands,
    PoolInventory pools,
    AccountOptions options,
    ILogger<LinuxAccounts> logger) : IDisposable
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

    /// <summary>The first writable pool whose <c>homes</c> dataset is mounted, with its mount point.</summary>
    public static (string Pool, string Mount)? HomesOf(PoolList current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var writable = current.Pools.Where(pool => !pool.ReadOnly && pool.State is "ONLINE" or "DEGRADED").Select(pool => pool.Name).ToHashSet(StringComparer.Ordinal);
        return current.Datasets
            .Where(dataset => writable.Contains(dataset.Pool) && dataset.Name == $"{dataset.Pool}/pithyx/homes" && dataset is { Mounted: true, MountPoint: not null })
            .OrderBy(dataset => dataset.Pool, StringComparer.Ordinal)
            .Select(dataset => ((string Pool, string Mount)?)(dataset.Pool, dataset.MountPoint!))
            .FirstOrDefault();
    }

    /// <summary>The home folder of a user: below the first pool's homes when it is there, otherwise none.</summary>
    public string HomeOf(string username) => HomesOf(pools.Current) is { } homes ? Path.Combine(homes.Mount, username) : AccountReconciler.NoHome;

    public async Task<IReadOnlyList<WantedAccount>> WantedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var users = await connection.QueryAsync<(string Username, string? DisplayName, long? Uid, string? DisabledAt)>(
            "SELECT username, display_name, uid, disabled_at FROM users WHERE uid IS NOT NULL ORDER BY uid");
        return [.. users.Select(user => new WantedAccount(
            user.Username,
            user.Uid!.Value,
            AccountReconciler.Comment(user.DisplayName),
            HomeOf(user.Username),
            AccountReconciler.NoLogin,
            user.DisabledAt is not null))];
    }

    /// <summary>
    /// The groups in the database with their members; a member whose account is not in <paramref name="accounts"/>
    /// (refused or not created yet) is left out. The built-in group <c>users</c> is the system's and never wanted here.
    /// </summary>
    public async Task<IReadOnlyList<WantedGroup>> WantedGroupsAsync(IReadOnlySet<string> accounts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        await using var connection = await connections.OpenAsync(cancellationToken);
        var groups = await connection.QueryAsync<(long Id, string Name)>("SELECT id, name FROM groups WHERE builtin = 0 ORDER BY id");
        var members = (await connection.QueryAsync<(long GroupId, string Username)>(
            "SELECT group_members.group_id, users.username FROM group_members JOIN users ON users.id = group_members.user_id"))
            .ToLookup(member => member.GroupId, member => member.Username);
        return [.. groups.Select(group => new WantedGroup(
            group.Name,
            group.Id,
            [.. members[group.Id].Where(accounts.Contains).Order(StringComparer.Ordinal)]))];
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
            foreach (var action in GroupReconciler.Plan(await WantedGroupsAsync(accounts, cancellationToken), ActualGroups()))
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
            return problems;
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>Creates the user's home dataset on the first pool and gives it to the user, when there is such a pool.</summary>
    public async Task<string?> CreateHomeAsync(string username, long uid, CancellationToken cancellationToken)
    {
        if (!options.Apply || HomesOf(pools.Current) is not { } homes)
        {
            return null;
        }
        var path = Path.Combine(homes.Mount, username);
        if (!Directory.Exists(path))
        {
            var result = await commands.RunAsync(ZfsPlans.CreateHomeDataset(homes.Pool, username), cancellationToken);
            if (result.ExitCode != 0 || !Directory.Exists(path))
            {
                LogHomeFailed(username, homes.Pool);
                return null;
            }
        }
        using (var parent = Files.LinuxFs.OpenRoot(homes.Mount))
        {
            Files.LinuxFs.ChangeOwnerAt(parent, username, (uint)uid, Files.FileOwner.UsersGroup);
        }
        // The daemon runs on Linux only; the analyzer cannot know that.
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Linux account {Name} was not provisioned: {Reason}")]
    private partial void LogRefused(string name, string reason);

    /// <summary>The key of a group's problem in the answer of <see cref="ReconcileAsync"/>; user names cannot contain a colon.</summary>
    public static string GroupProblemKey(string name) => $"group:{name}";

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Linux group {Name} was not provisioned: {Reason}")]
    private partial void LogGroupRefused(string name, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not create the home dataset of {Name} on pool {Pool}")]
    private partial void LogHomeFailed(string name, string pool);
}
