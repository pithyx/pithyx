using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.FileServices;

/// <summary>A shared folder in a user's SFTP view: the folder <c>&lt;root&gt;/&lt;user&gt;/&lt;share&gt;</c> shows <paramref name="Source"/>.</summary>
public sealed record SftpViewEntry(string User, string Share, string Source);

/// <summary>One step that brings the SFTP views in line.</summary>
public abstract record SftpViewAction(string Target);

public sealed record UnmountView(string Target) : SftpViewAction(Target);

public sealed record MountView(string Source, string Target) : SftpViewAction(Target);

/// <summary>
/// The SFTP view of every user with the SFTP right (D288): <c>/run/pithyx/sftp/&lt;user&gt;</c> is the user's chroot,
/// owned by root as sshd requires, with one bind mount per shared folder the user may use and nothing else. The views
/// live on the tmpfs <c>/run</c>, so nothing of them survives a reboot; they are built again at start and whenever the
/// users, the shares or the pools change. Exporting or destroying a pool takes the bind mounts of its datasets with it
/// (libzfs unmounts every mount of a dataset), and the next run notices.
/// </summary>
public sealed partial class SftpViews(ICommandRunner commands, SystemFiles files, FileServiceOptions options, ILogger<SftpViews> logger)
{
    /// <summary>The difference between the wanted views and the bind mounts below <paramref name="root"/>.</summary>
    /// <param name="root">The folder of the views.</param>
    /// <param name="wanted">Every shared folder of every view.</param>
    /// <param name="mounts">The mount table.</param>
    public static IReadOnlyList<SftpViewAction> Plan(string root, IReadOnlyList<SftpViewEntry> wanted, IReadOnlyList<MountEntry> mounts)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(mounts);
        var prefix = root.TrimEnd('/') + "/";
        var targets = wanted.ToDictionary(entry => Target(root, entry), entry => entry.Source, StringComparer.Ordinal);
        var actions = new List<SftpViewAction>();
        var present = new HashSet<string>(StringComparer.Ordinal);
        // The deepest first, so a mount on top of another goes before the one below it.
        foreach (var mount in mounts.Where(mount => mount.Target.StartsWith(prefix, StringComparison.Ordinal)).OrderByDescending(mount => mount.Target.Length).ThenBy(mount => mount.Target, StringComparer.Ordinal))
        {
            // A bind mount has the device of its source; one that shows something else (the dataset was replaced) goes.
            if (targets.TryGetValue(mount.Target, out var source)
                && MountTable.Containing(mounts, source) is { } origin
                && origin.Target == source
                && origin.MajorMinor == mount.MajorMinor
                && present.Add(mount.Target))
            {
                continue;
            }
            actions.Add(new UnmountView(mount.Target));
        }
        foreach (var (target, source) in targets.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            // Only a source that is a mount point of its own: a share whose dataset is not mounted would show the empty folder below.
            if (!present.Contains(target) && MountTable.Containing(mounts, source) is { } origin && origin.Target == source)
            {
                actions.Add(new MountView(source, target));
            }
        }
        return actions;
    }

    public static string Target(string root, SftpViewEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return $"{root.TrimEnd('/')}/{entry.User}/{entry.Share}";
    }

    /// <summary>Builds the views of <paramref name="users"/> from <paramref name="wanted"/>; every other view goes. Real mode only.</summary>
    public async Task ReconcileAsync(IReadOnlyCollection<string> users, IReadOnlyList<SftpViewEntry> wanted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(wanted);
        if (!options.Apply)
        {
            return;
        }
        var root = options.SftpRoot;
        MakeRootFolder(root);
        foreach (var user in users)
        {
            MakeRootFolder(Path.Combine(root, user));
        }
        foreach (var entry in wanted)
        {
            // Never a mode here: once the folder is mounted, it is the shared folder itself.
            Directory.CreateDirectory(Target(root, entry));
        }
        var mounts = MountTable.ParseMountInfo(await File.ReadAllTextAsync(files.MountInfo, cancellationToken));
        foreach (var action in Plan(root, wanted, mounts))
        {
            var command = action switch
            {
                UnmountView unmount => new CommandRequest("umount", ["--", unmount.Target]),
                MountView mount => new CommandRequest("mount", ["--bind", "--", mount.Source, mount.Target]),
                _ => throw new InvalidOperationException(),
            };
            var result = await commands.RunAsync(command, cancellationToken);
            if (result.ExitCode != 0 && action is UnmountView busy)
            {
                // A session still has a file open; the folder goes from the view now and from the kernel when it closes.
                await commands.RunAsync(new CommandRequest("umount", ["--lazy", "--", busy.Target]), cancellationToken);
            }
            else if (result.ExitCode != 0)
            {
                LogMountFailed(action.Target);
            }
        }
        RemoveLeftovers(root, users, wanted);
    }

    /// <summary>A folder owned by root that nobody else may write, as sshd wants every part of a chroot path.</summary>
    private static void MakeRootFolder(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>Removes the empty folders of views and shared folders that are no longer wanted; a folder that is still a mount point stays.</summary>
    private void RemoveLeftovers(string root, IReadOnlyCollection<string> users, IReadOnlyList<SftpViewEntry> wanted)
    {
        var keep = wanted.Select(entry => Target(root, entry)).ToHashSet(StringComparer.Ordinal);
        foreach (var userFolder in Directory.EnumerateDirectories(root))
        {
            var user = Path.GetFileName(userFolder);
            foreach (var shareFolder in Directory.EnumerateDirectories(userFolder).Where(folder => !keep.Contains(folder)))
            {
                TryRemove(shareFolder);
            }
            if (!users.Contains(user))
            {
                TryRemove(userFolder);
            }
        }
    }

    private void TryRemove(string folder)
    {
        try
        {
            // Never recursive: a folder that still shows a share is busy or not empty and stays.
            Directory.Delete(folder);
        }
        catch (IOException)
        {
            LogLeftover(folder);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mounting the SFTP view {Target} failed")]
    private partial void LogMountFailed(string target);

    [LoggerMessage(Level = LogLevel.Information, Message = "The SFTP view folder {Folder} is still in use and stays for now")]
    private partial void LogLeftover(string folder);
}
