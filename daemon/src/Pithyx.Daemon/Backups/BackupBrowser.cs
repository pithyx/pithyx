using System.Globalization;
using Microsoft.Win32.SafeHandles;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Files;

namespace Pithyx.Daemon.Backups;

/// <summary>Where browse sessions mount their repositories.</summary>
/// <param name="Root">Holds one mountpoint per job; only the daemon's user may enter it.</param>
public sealed record BackupBrowserOptions(string Root);

/// <summary>
/// Browse sessions (#395, D419): one <c>restic mount</c> per job, opened by a request to browse and closed after
/// <see cref="IdleTimeout"/> without a request, before a run, a check or a change of a version (restic's lock of a mount
/// would hold off their prune and tags), and when the daemon stops. Reading a version then only reads the mount.
/// </summary>
public sealed partial class BackupBrowser(
    BackupRestic restic,
    ICommandRunner commands,
    BackupBrowserOptions options,
    TimeProvider time,
    ILogger<BackupBrowser> logger) : IHostedService, IDisposable
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(2);

    /// <summary>The open sessions; read and changed under its own lock.</summary>
    private readonly Dictionary<long, Session> _sessions = [];

    /// <summary>One opening or closing at a time; mounting can take a while, so reads never wait on it.</summary>
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ITimer? _sweep;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Mounts of a daemon that crashed are left without their restic; they answer every call with an error.
        if (Directory.Exists(options.Root))
        {
            foreach (var folder in Directory.EnumerateDirectories(options.Root))
            {
                await UnmountAsync(folder);
            }
        }
        _sweep = time.CreateTimer(_ => _ = SweepAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _sweep?.Dispose();
        List<long> open;
        lock (_sessions)
        {
            open = [.. _sessions.Keys];
        }
        foreach (var jobId in open)
        {
            await CloseAsync(jobId);
        }
    }

    /// <summary>
    /// Mounts the job's repository unless a session is open, and keeps it open for another <see cref="IdleTimeout"/>.
    /// Throws restic's connection or repository error.
    /// </summary>
    public async Task OpenAsync(long jobId, Func<CancellationToken, Task<ResticRepository>> repository, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            Session? open;
            lock (_sessions)
            {
                if (_sessions.Remove(jobId, out open) && !open.Process.IsCompleted)
                {
                    open.LastUsed = time.GetUtcNow();
                    _sessions[jobId] = open;
                    return;
                }
            }
            if (open is not null)
            {
                await EndAsync(jobId, open);
            }
            var mountpoint = Mountpoint(jobId);
            CreatePrivate(options.Root);
            await UnmountAsync(mountpoint);
            CreatePrivate(mountpoint);
            var location = await repository(cancellationToken);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stop = new CancellationTokenSource();
            var process = restic.MountAsync(location, mountpoint, () => ready.TrySetResult(), stop.Token);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var timeout = Task.Delay(StartTimeout, time, wait.Token);
            var first = await Task.WhenAny(ready.Task, process, timeout);
            await wait.CancelAsync();
            var session = new Session(process, stop, mountpoint) { LastUsed = time.GetUtcNow() };
            if (first == ready.Task)
            {
                lock (_sessions)
                {
                    _sessions[jobId] = session;
                }
                LogOpened(jobId);
                return;
            }
            var result = await EndAsync(jobId, session);
            cancellationToken.ThrowIfCancellationRequested();
            if (result is { } finished && ResticOutput.Failure(finished.ExitCode, finished.StandardError, location.RemotePath) is { } failure)
            {
                LogMountFailed(jobId, finished.ExitCode, finished.StandardError.Length > 4000 ? finished.StandardError[..4000] : finished.StandardError);
                throw failure;
            }
            throw first == timeout
                ? new PithyxException(ErrorCodes.BackupTargetUnreachable, new Dictionary<string, object?> { ["reason"] = "timeout" })
                : new PithyxException(ErrorCodes.BackupRunFailed, new Dictionary<string, object?> { ["reason"] = "mount" });
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Whether a session of the job is open.</summary>
    public bool IsOpen(long jobId)
    {
        lock (_sessions)
        {
            return _sessions.TryGetValue(jobId, out var session) && !session.Process.IsCompleted;
        }
    }

    /// <summary>
    /// The folder of a version in the job's open session, for reading below it with <see cref="LinuxFs.OpenBeneath"/>.
    /// Throws <see cref="ErrorCodes.BackupBrowseClosed"/> without one.
    /// </summary>
    public SafeFileHandle Version(long jobId, string snapshot)
    {
        string mountpoint;
        lock (_sessions)
        {
            if (!_sessions.TryGetValue(jobId, out var session) || session.Process.IsCompleted)
            {
                throw new PithyxException(ErrorCodes.BackupBrowseClosed);
            }
            session.LastUsed = time.GetUtcNow();
            mountpoint = session.Mountpoint;
        }
        try
        {
            return LinuxFs.OpenRoot(Path.Combine(mountpoint, ResticCommands.MountIds, snapshot));
        }
        catch (FsError error) when (error.Errno == LinuxFs.ENOENT)
        {
            // The mount lists the versions it found when it started; a newer one needs a new session.
            throw new PithyxException(ErrorCodes.BackupBrowseClosed);
        }
    }

    /// <summary>Ends the job's session, if one is open.</summary>
    public async Task CloseAsync(long jobId)
    {
        await _lock.WaitAsync();
        try
        {
            Session? session;
            lock (_sessions)
            {
                _sessions.Remove(jobId, out session);
            }
            if (session is not null)
            {
                await EndAsync(jobId, session);
                LogClosed(jobId);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _sweep?.Dispose();
        _lock.Dispose();
        foreach (var session in _sessions.Values)
        {
            session.Stop.Cancel();
            session.Stop.Dispose();
        }
    }

    private async Task SweepAsync()
    {
        var now = time.GetUtcNow();
        List<long> idle;
        lock (_sessions)
        {
            idle = [.. _sessions.Where(pair => pair.Value.Process.IsCompleted || now - pair.Value.LastUsed >= IdleTimeout).Select(pair => pair.Key)];
        }
        foreach (var jobId in idle)
        {
            await CloseAsync(jobId);
        }
    }

    /// <summary>Stops restic, which unmounts, and makes sure the mount is gone. Returns restic's result when it ended by itself.</summary>
    private async Task<CommandResult?> EndAsync(long jobId, Session session)
    {
        CommandResult? result = null;
        await session.Stop.CancelAsync();
        try
        {
            result = await session.Process;
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or InvalidOperationException)
        {
            LogEnded(jobId, exception.Message);
        }
        finally
        {
            session.Stop.Dispose();
        }
        await UnmountAsync(session.Mountpoint);
        return result;
    }

    /// <summary>Unmounts a mount left behind, if any; an empty folder needs nothing.</summary>
    private async Task UnmountAsync(string mountpoint)
    {
        try
        {
            if (!Directory.Exists(mountpoint) || !Directory.EnumerateFileSystemEntries(mountpoint).Any())
            {
                return;
            }
        }
        catch (IOException)
        {
            // A mount whose restic is gone answers "transport endpoint is not connected".
        }
        var result = await commands.RunAsync(new CommandRequest("fusermount3", ["-u", "-z", mountpoint]), CancellationToken.None);
        if (result.ExitCode != 0)
        {
            LogUnmountFailed(mountpoint, result.StandardError);
        }
    }

    private string Mountpoint(long jobId) => Path.Combine(options.Root, jobId.ToString(CultureInfo.InvariantCulture));

    private static void CreatePrivate(string folder)
    {
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else
        {
            Directory.CreateDirectory(folder);
        }
    }

    private sealed class Session(Task<CommandResult> process, CancellationTokenSource stop, string mountpoint)
    {
        public Task<CommandResult> Process { get; } = process;
        public CancellationTokenSource Stop { get; } = stop;
        public string Mountpoint { get; } = mountpoint;
        public DateTimeOffset LastUsed { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Browse session of backup job {JobId} opened")]
    private partial void LogOpened(long jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Browse session of backup job {JobId} closed")]
    private partial void LogClosed(long jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "restic mount of backup job {JobId} ended: {Reason}")]
    private partial void LogEnded(long jobId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "restic mount of backup job {JobId} failed with exit {ExitCode}: {StandardError}")]
    private partial void LogMountFailed(long jobId, int exitCode, string standardError);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mount {Mountpoint} could not be removed: {StandardError}")]
    private partial void LogUnmountFailed(string mountpoint, string standardError);
}
