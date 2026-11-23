using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Backups;

/// <summary>Where a job's repository is and how it is opened.</summary>
/// <param name="Endpoint">The target's server and keys.</param>
/// <param name="RemotePath">The repository's folder on the server.</param>
/// <param name="Key">The repository's key.</param>
/// <param name="UploadLimitKib">0 for none.</param>
/// <param name="DownloadLimitKib">0 for none.</param>
/// <param name="CacheDir">restic's cache on a pool; null for none.</param>
public sealed record ResticRepository(SshEndpoint Endpoint, string RemotePath, string Key, int UploadLimitKib = 0, int DownloadLimitKib = 0, string? CacheDir = null);

/// <summary>
/// Runs restic through <see cref="ICommandRunner"/> (D415). The target's key, its known_hosts line and the repository's
/// key go to restic in a <see cref="KeyFolder"/>, removed after each call; nothing secret is on a command line.
/// </summary>
public sealed class BackupRestic(ICommandRunner commands)
{
    public static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan BackupTimeout = TimeSpan.FromDays(7);
    public static readonly TimeSpan PruneTimeout = TimeSpan.FromHours(24);
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromDays(2);
    public static readonly TimeSpan StatsTimeout = TimeSpan.FromHours(1);

    /// <summary>Makes a new repository. Throws <see cref="ErrorCodes.BackupRepositoryExists"/> or a connection error.</summary>
    public async Task InitAsync(ResticRepository repository, CancellationToken cancellationToken)
    {
        var result = await RunAsync(repository, ResticCommands.Init, null, SetupTimeout, null, cancellationToken);
        if (ResticOutput.Failure(result.ExitCode, result.StandardError, repository.RemotePath) is { } error)
        {
            throw error;
        }
    }

    /// <summary>Opens an existing repository. Throws <see cref="ErrorCodes.BackupRepositoryNotFound"/>, <see cref="ErrorCodes.BackupRepositoryWrongKey"/> or a connection error.</summary>
    public async Task VerifyAsync(ResticRepository repository, CancellationToken cancellationToken)
    {
        var result = await RunAsync(repository, ResticCommands.Verify, null, SetupTimeout, null, cancellationToken);
        if (ResticOutput.Failure(result.ExitCode, result.StandardError, repository.RemotePath) is { } error)
        {
            throw error;
        }
    }

    /// <summary>Backs up <paramref name="paths"/> with the snapshots mounted over them; restic's lines go to <paramref name="report"/>.</summary>
    public async Task<CommandResult> BackupAsync(
        ResticRepository repository,
        IReadOnlyList<SnapshotMount> mounts,
        IReadOnlyList<string> paths,
        IReadOnlyList<string> excludes,
        ResticReport report,
        Action onProgress,
        CancellationToken cancellationToken)
    {
        var host = Environment.MachineName;
        return await RunAsync(
            repository,
            global => ResticCommands.Backup(global, paths, excludes, host),
            mounts,
            BackupTimeout,
            line =>
            {
                if (ResticOutput.Read(line, report))
                {
                    onProgress();
                }
            },
            cancellationToken,
            report);
    }

    /// <summary>Applies the retention; null when it keeps everything.</summary>
    public async Task<CommandResult?> ForgetAsync(ResticRepository repository, BackupRetention retention, CancellationToken cancellationToken)
    {
        if (ResticCommands.Forget([], retention) is null)
        {
            return null;
        }
        return await RunAsync(repository, global => ResticCommands.Forget(global, retention)!, null, PruneTimeout, null, cancellationToken);
    }

    /// <summary>What the repository takes on the target; null when restic could not tell.</summary>
    public async Task<long?> StatsAsync(ResticRepository repository, CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunAsync(repository, ResticCommands.Stats, null, StatsTimeout, null, cancellationToken);
            return result.ExitCode == 0 ? ResticOutput.RepositoryBytes(result.StandardOutput) : null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    public async Task<CommandResult> CheckAsync(ResticRepository repository, int readPercent, ResticReport report, CancellationToken cancellationToken) =>
        await RunAsync(repository, global => ResticCommands.Check(global, readPercent), null, CheckTimeout, line => ResticOutput.Read(line, report), cancellationToken, report);

    private async Task<CommandResult> RunAsync(
        ResticRepository repository,
        Func<IReadOnlyList<string>, IReadOnlyList<string>> command,
        IReadOnlyList<SnapshotMount>? mounts,
        TimeSpan timeout,
        Action<string>? outputLine,
        CancellationToken cancellationToken,
        ResticReport? report = null)
    {
        using var folder = new KeyFolder();
        var password = folder.Write("repository", repository.Key);
        var global = ResticCommands.Global(
            repository.RemotePath,
            password,
            BackupSsh.Options(folder, repository.Endpoint),
            repository.UploadLimitKib,
            repository.DownloadLimitKib,
            repository.CacheDir);
        var env = ResticCommands.Env(command(global));
        var request = mounts is null
            ? new CommandRequest("env", env, timeout, OutputLine: outputLine, Interruptible: true)
            : new CommandRequest("unshare", ResticCommands.InNamespace(mounts, ["env", .. env]), timeout, OutputLine: outputLine, Interruptible: true);
        var result = await commands.RunAsync(request, cancellationToken);
        if (report is not null)
        {
            // restic prints its errors as JSON on stderr.
            foreach (var line in result.StandardError.Split('\n'))
            {
                ResticOutput.Read(line, report);
            }
        }
        return result;
    }
}
