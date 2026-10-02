using System.Text.Json;
using System.Threading.Channels;
using Dapper;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Users;

namespace Pithyx.Daemon.FileServices;

/// <summary>Raised when something SMB or SFTP depend on changed that the users and shares do not announce: the file service settings or a user's protocol rights.</summary>
public sealed class FileServiceChanges
{
    public event Action? Changed;

    public void Announce() => Changed?.Invoke();
}

/// <summary>What SMB and SFTP should offer, derived from SQLite and the pools.</summary>
/// <param name="SmbEnabled">SMB is switched on.</param>
/// <param name="Workgroup">The SMB workgroup.</param>
/// <param name="SftpEnabled">SFTP is switched on.</param>
/// <param name="Accounts">Every Pithyx account with whether it is deactivated.</param>
/// <param name="SmbShares">The shares SMB offers.</param>
/// <param name="SftpUsers">The users with an SFTP view.</param>
/// <param name="SftpEntries">The shares in the SFTP views.</param>
/// <param name="SharePaths">Every available dataset share by id, for the recycle bin.</param>
public sealed record FileServicePlan(
    bool SmbEnabled,
    string Workgroup,
    bool SftpEnabled,
    IReadOnlyList<(string Name, bool Disabled)> Accounts,
    IReadOnlyList<SmbShare> SmbShares,
    IReadOnlyList<string> SftpUsers,
    IReadOnlyList<SftpViewEntry> SftpEntries,
    IReadOnlyDictionary<long, string> SharePaths);

/// <summary>
/// Brings SMB and SFTP in line with SQLite (D287, D288): the groups sshd matches on, the Samba accounts, the bin
/// folders Samba needs, <c>smb.conf</c> (checked with <c>testparm</c> before it replaces the old one), the sshd drop-in
/// (checked with <c>sshd -t</c>), the services and the SFTP views. It runs after the Linux accounts and the share ACLs
/// (<see cref="LinuxAccountSync"/>) and does nothing when the wanted state did not change since the last run. A file is
/// written only when its content changes, and a service is reloaded only then. Deletes over SMB that the audit log
/// reports are turned into recycle bin items here as well.
/// </summary>
public sealed partial class FileServiceSync(
    SqliteConnectionFactory connections,
    ICommandRunner commands,
    PoolInventory pools,
    FileServicePasswords passwords,
    SftpViews views,
    SmbRecycle recycle,
    FileServiceActivity activity,
    FileServiceOptions options,
    ILogger<FileServiceSync> logger) : BackgroundService
{
    public const string SmbUnit = "smbd.service";
    public const string SshUnit = "ssh.service";

    // Tuples are fields; the fingerprint must see them.
    private static readonly JsonSerializerOptions _fingerprint = new() { IncludeFields = true };

    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly Channel<string?> _deletes = Channel.CreateBounded<string?>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropWrite });
    private string? _applied;
    private FileServicePlan? _plan;

    public FileServiceState SmbState { get; private set; } = FileServiceState.Off;

    public FileServiceState SftpState { get; private set; } = FileServiceState.Off;

    /// <summary>The wanted state from the database and the pools.</summary>
    public async Task<FileServicePlan> WantedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var settings = await connection.QuerySingleAsync<(long Smb, string Workgroup, long Sftp)>("SELECT smb_enabled, smb_workgroup, sftp_enabled FROM file_services WHERE id = 1");
        var users = (await connection.QueryAsync<(long Id, string Username, long Uid, long IsAdmin, string? DisabledAt, long Smb, long Sftp)>(
            "SELECT id, username, uid, is_admin, disabled_at, smb, sftp FROM users WHERE uid IS NOT NULL ORDER BY username")).ToList();
        var shares = (await connection.QueryAsync<(long Id, string Name, string Path, string Dataset, long Smb, long Sftp, long Encrypt)>(
            "SELECT id, name, path, dataset, smb, sftp, smb_encrypt FROM shares WHERE dataset IS NOT NULL ORDER BY id")).ToList();
        var access = (await connection.QueryAsync<(long ShareId, long UserId, string Level)>("SELECT share_id, user_id, level FROM share_access"))
            .ToDictionary(row => (row.ShareId, row.UserId), row => row.Level);
        return Plan(settings.Smb == 1, settings.Workgroup, settings.Sftp == 1, users, shares, access, pools.Current, pools.Available);
    }

    /// <summary>The pure part of <see cref="WantedAsync"/>: who gets which share over which protocol.</summary>
    public static FileServicePlan Plan(
        bool smbEnabled,
        string workgroup,
        bool sftpEnabled,
        IReadOnlyList<(long Id, string Username, long Uid, long IsAdmin, string? DisabledAt, long Smb, long Sftp)> users,
        IReadOnlyList<(long Id, string Name, string Path, string Dataset, long Smb, long Sftp, long Encrypt)> shares,
        IReadOnlyDictionary<(long ShareId, long UserId), string> access,
        PoolList current,
        bool poolsAvailable)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(shares);
        ArgumentNullException.ThrowIfNull(access);
        var active = users.Where(user => user.DisabledAt is null && AccountReconciler.IsManaged(user.Uid)).ToList();
        var smbUsers = active.Where(user => user.IsAdmin == 1 || user.Smb == 1).ToList();
        var sftpUsers = sftpEnabled ? active.Where(user => user.IsAdmin == 1 || user.Sftp == 1).ToList() : [];
        var available = shares.Where(share => ShareAvailability.Reason(share.Dataset, current, poolsAvailable) is null).ToList();
        string? Level(long shareId, (long Id, string Username, long Uid, long IsAdmin, string? DisabledAt, long Smb, long Sftp) user) =>
            user.IsAdmin == 1 ? "readWrite" : access.TryGetValue((shareId, user.Id), out var level) ? level : null;
        var smbShares = available.Where(share => share.Smb == 1 && !SmbConfig.ReservedNames.Contains(share.Name)).Select(share => new SmbShare(
            share.Name,
            share.Path,
            [.. smbUsers.Where(user => Level(share.Id, user) == "read").Select(user => user.Username)],
            [.. smbUsers.Where(user => Level(share.Id, user) == "readWrite").Select(user => user.Username)],
            share.Encrypt == 1)).ToList();
        var entries = available.Where(share => share.Sftp == 1)
            .SelectMany(share => sftpUsers.Where(user => Level(share.Id, user) is "read" or "readWrite").Select(user => new SftpViewEntry(user.Username, share.Name, share.Path)))
            .ToList();
        return new FileServicePlan(
            smbEnabled,
            workgroup,
            sftpEnabled,
            [.. users.Where(user => AccountReconciler.IsManaged(user.Uid)).Select(user => (user.Username, user.DisabledAt is not null))],
            smbShares,
            [.. sftpUsers.Select(user => user.Username)],
            entries,
            available.ToDictionary(share => share.Id, share => share.Path));
    }

    /// <summary>Applies the wanted state when it changed since the last run, or always with <paramref name="force"/>. Never throws but for cancellation.</summary>
    public async Task ReconcileAsync(bool force, CancellationToken cancellationToken)
    {
        await _running.WaitAsync(cancellationToken);
        try
        {
            var plan = await WantedAsync(cancellationToken);
            var fingerprint = JsonSerializer.Serialize(plan, _fingerprint);
            if (!force && fingerprint == _applied)
            {
                return;
            }
            _plan = plan;
            activity.SftpUsers = plan.SftpUsers.ToHashSet(StringComparer.Ordinal);
            if (!options.Apply)
            {
                SmbState = plan.SmbEnabled ? FileServiceState.Running : FileServiceState.Off;
                SftpState = plan.SftpEnabled ? FileServiceState.Running : FileServiceState.Off;
                await passwords.ReconcileAsync(plan.Accounts, cancellationToken);
                _applied = fingerprint;
                return;
            }
            await GroupsAsync(plan, cancellationToken);
            await passwords.ReconcileAsync(plan.Accounts, cancellationToken);
            foreach (var path in plan.SharePaths.Values)
            {
                PrepareBin(path);
            }
            SmbState = await SmbAsync(plan, cancellationToken);
            SftpState = await SftpAsync(plan, cancellationToken);
            await views.ReconcileAsync(plan.SftpUsers, plan.SftpEntries, cancellationToken);
            _applied = fingerprint;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The next change tries again with everything.
            _applied = null;
            LogFailed(exception);
        }
        finally
        {
            _running.Release();
        }
    }

    private void PrepareBin(string path)
    {
        try
        {
            recycle.Prepare(path);
        }
        catch (Exception exception) when (exception is Files.FsError or IOException or UnauthorizedAccessException)
        {
            LogBinFailed(exception, path);
        }
    }

    /// <summary>
    /// <see cref="SshdConfig.UsersGroup"/> has every Pithyx account and <see cref="SshdConfig.SftpGroup"/> the SFTP
    /// users; both are system groups outside the Pithyx ranges.
    /// </summary>
    private async Task GroupsAsync(FileServicePlan plan, CancellationToken cancellationToken)
    {
        var groups = File.Exists(options.GroupPath) ? GroupParser.Parse(await File.ReadAllTextAsync(options.GroupPath, cancellationToken)) : [];
        var accounts = new HashSet<string>(File.Exists("/etc/passwd") ? PasswdParser.Parse(await File.ReadAllTextAsync("/etc/passwd", cancellationToken)).Select(entry => entry.Name) : [], StringComparer.Ordinal);
        // A member must be an account already; one the account reconciler refused is left out.
        var wanted = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [SshdConfig.UsersGroup] = [.. plan.Accounts.Select(account => account.Name).Where(accounts.Contains).Order(StringComparer.Ordinal)],
            [SshdConfig.SftpGroup] = [.. plan.SftpUsers.Where(accounts.Contains).Order(StringComparer.Ordinal)],
        };
        foreach (var (group, members) in wanted)
        {
            var existing = groups.FirstOrDefault(entry => entry.Name == group);
            if (existing is null)
            {
                await commands.RunAsync(new CommandRequest("groupadd", ["--system", group]), cancellationToken);
            }
            if (existing is null || !existing.Members.Order(StringComparer.Ordinal).SequenceEqual(members))
            {
                await commands.RunAsync(new CommandRequest("gpasswd", ["-M", string.Join(',', members), group]), cancellationToken);
            }
        }
    }

    private async Task<FileServiceState> SmbAsync(FileServicePlan plan, CancellationToken cancellationToken)
    {
        if (!Installed("smbd"))
        {
            return FileServiceState.Unavailable;
        }
        if (!File.Exists(options.SmbLocalInclude))
        {
            await File.WriteAllTextAsync(options.SmbLocalInclude, "# Your own Samba settings. Pithyx includes this file and never writes it.\n", cancellationToken);
        }
        var content = SmbConfig.Render(plan.Workgroup, plan.SmbShares);
        var current = File.Exists(options.SmbConfigPath) ? await File.ReadAllTextAsync(options.SmbConfigPath, cancellationToken) : null;
        var changed = current != content;
        if (changed)
        {
            // The file of the distribution (or of the owner) is kept once, next to it.
            if (current is not null && !current.StartsWith(SmbConfig.Header, StringComparison.Ordinal) && !File.Exists(options.SmbConfigPath + ".pre-pithyx"))
            {
                File.Copy(options.SmbConfigPath, options.SmbConfigPath + ".pre-pithyx");
            }
            var candidate = options.SmbConfigPath + ".pithyx-new";
            await File.WriteAllTextAsync(candidate, content, cancellationToken);
            var check = await commands.RunAsync(new CommandRequest("testparm", ["--suppress-prompt", "-s", candidate]), cancellationToken);
            if (check.ExitCode != 0)
            {
                File.Delete(candidate);
                LogConfigRefused("smb.conf", check.StandardError);
                return plan.SmbEnabled ? FileServiceState.Failed : FileServiceState.Off;
            }
            File.Move(candidate, options.SmbConfigPath, overwrite: true);
        }
        return await UnitAsync(SmbUnit, plan.SmbEnabled, changed, cancellationToken);
    }

    private async Task<FileServiceState> SftpAsync(FileServicePlan plan, CancellationToken cancellationToken)
    {
        if (!Installed("sshd"))
        {
            return FileServiceState.Unavailable;
        }
        var content = SshdConfig.Render(options.SftpRoot);
        var current = File.Exists(options.SshdDropIn) ? await File.ReadAllTextAsync(options.SshdDropIn, cancellationToken) : null;
        var changed = current != content;
        if (changed)
        {
            await File.WriteAllTextAsync(options.SshdDropIn, content, cancellationToken);
            // sshd reads its files only at start and reload, so the check runs on the file in place.
            var check = await commands.RunAsync(new CommandRequest("sshd", ["-t"]), cancellationToken);
            if (check.ExitCode != 0)
            {
                if (current is null)
                {
                    File.Delete(options.SshdDropIn);
                }
                else
                {
                    await File.WriteAllTextAsync(options.SshdDropIn, current, cancellationToken);
                }
                LogConfigRefused("sshd", check.StandardError);
                return plan.SftpEnabled ? FileServiceState.Failed : FileServiceState.Off;
            }
        }
        var active = (await commands.RunAsync(new CommandRequest("systemctl", ["is-active", "--quiet", SshUnit]), cancellationToken)).ExitCode == 0;
        if (active && changed)
        {
            await commands.RunAsync(new CommandRequest("systemctl", ["reload", SshUnit]), cancellationToken);
        }
        if (!plan.SftpEnabled)
        {
            // Whether sshd runs at all belongs to the SSH setting; SFTP only stops letting Pithyx accounts in.
            return FileServiceState.Off;
        }
        if (!active && (await commands.RunAsync(new CommandRequest("systemctl", ["enable", "--now", SshUnit]), cancellationToken)).ExitCode != 0)
        {
            return FileServiceState.Failed;
        }
        return FileServiceState.Running;
    }

    /// <summary>Starts, reloads or stops a unit as wanted and tells how it is doing.</summary>
    private async Task<FileServiceState> UnitAsync(string unit, bool enabled, bool reload, CancellationToken cancellationToken)
    {
        var active = (await commands.RunAsync(new CommandRequest("systemctl", ["is-active", "--quiet", unit]), cancellationToken)).ExitCode == 0;
        if (!enabled)
        {
            var isEnabled = (await commands.RunAsync(new CommandRequest("systemctl", ["is-enabled", "--quiet", unit]), cancellationToken)).ExitCode == 0;
            if (active || isEnabled)
            {
                await commands.RunAsync(new CommandRequest("systemctl", ["disable", "--now", unit]), cancellationToken);
            }
            return FileServiceState.Off;
        }
        var result = active
            ? reload ? await commands.RunAsync(new CommandRequest("systemctl", ["reload", unit]), cancellationToken) : null
            : await commands.RunAsync(new CommandRequest("systemctl", ["enable", "--now", unit]), cancellationToken);
        return result is { ExitCode: not 0 } ? FileServiceState.Failed : FileServiceState.Running;
    }

    /// <summary>Whether a program of a file service is installed.</summary>
    public static bool Installed(string program) =>
        File.Exists($"/usr/sbin/{program}") || File.Exists($"/usr/bin/{program}") || File.Exists($"/sbin/{program}");

    public override void Dispose()
    {
        _running.Dispose();
        base.Dispose();
    }

    /// <summary>Asks for the deleted files of a share (by its SMB name) to be moved into its recycle bin; null for every share.</summary>
    public void Deleted(string? share) => _deletes.Writer.TryWrite(share);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Apply)
        {
            return;
        }
        activity.SmbDeleted += Deleted;
        try
        {
            // What Samba moved while the daemon was not running.
            Deleted(null);
            await foreach (var first in _deletes.Reader.ReadAllAsync(stoppingToken))
            {
                // Deletes arrive file by file; a short wait takes a whole folder at once.
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                var names = new HashSet<string?>([first]);
                while (_deletes.Reader.TryRead(out var next))
                {
                    names.Add(next);
                }
                await IngestAsync(names, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; the next start takes what is left.
        }
        finally
        {
            activity.SmbDeleted -= Deleted;
        }
    }

    private async Task IngestAsync(HashSet<string?> names, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connections.OpenAsync(cancellationToken);
            var shares = await connection.QueryAsync<(long Id, string Name, string Path)>("SELECT id, name, path FROM shares WHERE dataset IS NOT NULL");
            var available = (_plan ?? await WantedAsync(cancellationToken)).SharePaths;
            foreach (var share in shares.Where(share => available.ContainsKey(share.Id) && (names.Contains(null) || names.Any(name => string.Equals(name, share.Name, StringComparison.OrdinalIgnoreCase)))))
            {
                await recycle.IngestAsync(share.Id, share.Path, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogIngestFailed(exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Bringing SMB and SFTP in line failed")]
    private partial void LogFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The generated {Config} configuration was refused and not loaded: {Details}")]
    private partial void LogConfigRefused(string config, string details);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Preparing the recycle bin of {Path} for SMB failed")]
    private partial void LogBinFailed(Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Moving files deleted over SMB into the recycle bin failed")]
    private partial void LogIngestFailed(Exception exception);
}
