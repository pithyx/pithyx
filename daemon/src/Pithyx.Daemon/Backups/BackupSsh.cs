using System.Text.Json;
using System.Text.RegularExpressions;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Backups;

/// <summary>What a target needs for a connection: where it is and the keys on both ends.</summary>
public sealed record SshEndpoint(string Host, int Port, string User, string HostKey, string PrivateKey);

/// <summary>The size of the file system behind an SFTP folder and what its user can still write, in bytes.</summary>
public sealed record SftpSpace(long TotalBytes, long FreeBytes);

/// <summary>What a target's connection test found: null for no error, and the space when the server reported it.</summary>
public sealed record SftpProbe(PithyxException? Error, SftpSpace? Space);

/// <summary>
/// SSH for backup targets, through <see cref="ICommandRunner"/> and hardened like the catalogs' git (D414): no system or
/// user configuration (<c>-F /dev/null</c>, <c>env -i</c>), no agent, only the target's own key, only the pinned host key
/// (<c>StrictHostKeyChecking=yes</c> against a known_hosts file holding nothing else), and the server named only through
/// options, so no host or user can be read as an option. Keys go to ssh in a folder only root can enter, removed after
/// each call.
/// </summary>
public sealed partial class BackupSsh(ICommandRunner commands)
{
    /// <summary>The name ssh knows the target by in its known_hosts file, whatever its address and port.</summary>
    public const string Alias = "pithyx-target";

    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Makes an Ed25519 key; returns the private key in OpenSSH format and the public line.</summary>
    public async Task<(string PrivateKey, string PublicKey)> KeygenAsync(string comment, CancellationToken cancellationToken)
    {
        using var folder = new KeyFolder();
        var key = Path.Combine(folder.Path, "key");
        var result = await commands.RunAsync(new CommandRequest("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-C", comment, "-f", key], TimeSpan.FromSeconds(30), SecretOutput: true), cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new PithyxException(ErrorCodes.SystemInternalUnexpected);
        }
        return (File.ReadAllText(key), File.ReadAllText(key + ".pub").Trim());
    }

    /// <summary>The host keys the server offers. Throws <see cref="ErrorCodes.BackupHostKeyScanFailed"/>.</summary>
    public async Task<IReadOnlyList<BackupHostKey>> ScanAsync(string host, int port, CancellationToken cancellationToken)
    {
        CommandResult result;
        try
        {
            result = await commands.RunAsync(
                new CommandRequest("ssh-keyscan", ["-T", "15", "-t", string.Join(',', SshHostKeys.Types), "-p", BackupTargetRules.Text(port), "--", host], TimeSpan.FromSeconds(30)),
                cancellationToken);
        }
        catch (TimeoutException)
        {
            throw ScanFailed("timeout");
        }
        var keys = SshHostKeys.ParseScan(result.StandardOutput);
        if (keys.Count > 0)
        {
            return keys;
        }
        throw ScanFailed(result.StandardError.Contains("Name or service not known", StringComparison.Ordinal)
            || result.StandardError.Contains("No address associated", StringComparison.Ordinal)
            || result.StandardError.Contains("Temporary failure in name resolution", StringComparison.Ordinal)
            ? "dns"
            : "none");
    }

    /// <summary>
    /// Signs in to the target, creates its folder if missing, writes and removes a test file, and asks for the space.
    /// The error is null when all worked, else <see cref="ErrorCodes.BackupTargetUnreachable"/>,
    /// <see cref="ErrorCodes.BackupTargetHostKeyChanged"/>, <see cref="ErrorCodes.BackupTargetAuthFailed"/> or
    /// <see cref="ErrorCodes.BackupTargetNotWritable"/>.
    /// </summary>
    public async Task<SftpProbe> ProbeAsync(SshEndpoint endpoint, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        using var folder = new KeyFolder();
        var empty = Path.Combine(folder.Path, "empty");
        File.WriteAllBytes(empty, []);
        var testFile = ".pithyx-test-" + Auth.Secrets.NewToken(8).ToLowerInvariant();
        var script = ProbeScript(path, empty, testFile);
        CommandResult result;
        try
        {
            result = await commands.RunAsync(
                new CommandRequest("env", [.. CleanEnvironment(), "sftp", .. Options(folder, endpoint), "-b", "-", Alias], ProbeTimeout, StandardInput: script),
                cancellationToken);
        }
        catch (TimeoutException)
        {
            return new SftpProbe(Unreachable("timeout"), null);
        }
        var error = ProbeOutcome(result, path);
        return new SftpProbe(error, error is null ? ParseSpace(result.StandardOutput) : null);
    }

    /// <summary>
    /// Signs in to a server as a pull does and lists the top of the folder its key may read. Null when that worked, else
    /// the connection's error, <see cref="ErrorCodes.BackupServerRsyncMissing"/> or <see cref="ErrorCodes.BackupServerRefused"/>.
    /// </summary>
    public async Task<PithyxException?> ProbeServerAsync(SshEndpoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        using var folder = new KeyFolder();
        try
        {
            return BackupRsync.ProbeOutcome(await commands.RunAsync(BackupRsync.Probe(folder, endpoint), cancellationToken));
        }
        catch (TimeoutException)
        {
            return Unreachable("timeout");
        }
    }

    /// <summary>The space behind the target's folder; null when the server cannot be reached or does not report it.</summary>
    public async Task<SftpSpace?> SpaceAsync(SshEndpoint endpoint, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(path);
        using var folder = new KeyFolder();
        var script = (path.Length > 0 ? $"cd \"{path}\"\n" : "") + "df\n";
        try
        {
            var result = await commands.RunAsync(
                new CommandRequest("env", [.. CleanEnvironment(), "sftp", .. Options(folder, endpoint), "-b", "-", Alias], ProbeTimeout, StandardInput: script),
                cancellationToken);
            return result.ExitCode == 0 ? ParseSpace(result.StandardOutput) : null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads sftp's <c>df</c> table (OpenSSH, statvfs@openssh.com): a header with Size, Used and Avail, then a line of
    /// KiB. Null without the table, which a server without the extension leaves out.
    /// </summary>
    public static SftpSpace? ParseSpace(string standardOutput)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        var lines = standardOutput.Split('\n');
        for (var index = 0; index < lines.Length - 1; index++)
        {
            var header = lines[index].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (header is not ["Size", "Used", "Avail", ..])
            {
                continue;
            }
            var values = lines[index + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (values.Length >= 3
                && long.TryParse(values[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var size)
                && long.TryParse(values[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var available)
                && size > 0)
            {
                return new SftpSpace(size * 1024, Math.Min(available, size) * 1024);
            }
            return null;
        }
        return null;
    }

    /// <summary>
    /// The sftp batch of a probe: each folder of the path made if missing (the leading <c>-</c> ignores an error), then
    /// the test file, then the space, whose error is ignored too (not every server has the extension).
    /// </summary>
    public static string ProbeScript(string path, string localEmptyFile, string testFile)
    {
        ArgumentNullException.ThrowIfNull(path);
        var lines = new List<string>();
        foreach (var folder in BackupTargetRules.Ancestry(path))
        {
            lines.Add($"-mkdir \"{folder}\"");
        }
        if (path.Length > 0)
        {
            lines.Add($"cd \"{path}\"");
        }
        lines.Add($"put \"{localEmptyFile}\" \"{testFile}\"");
        lines.Add($"rm \"{testFile}\"");
        lines.Add("-df");
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>
    /// What a probe's sftp run means. sftp echoes each batch command before it runs it and stops at the first that fails
    /// (exit 1); ssh failures before the session end with 255.
    /// </summary>
    public static PithyxException? ProbeOutcome(CommandResult result, string path)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ExitCode == 0)
        {
            return null;
        }
        var error = result.StandardError;
        if (ConnectionFailure(error) is { Code: not ErrorCodes.BackupTargetUnreachable } refused)
        {
            return refused;
        }
        if (result.ExitCode == 255 || !result.StandardOutput.Contains("sftp> ", StringComparison.Ordinal))
        {
            return Unreachable(
                error.Contains("Could not resolve hostname", StringComparison.Ordinal) ? "dns"
                : error.Contains("Connection refused", StringComparison.Ordinal) ? "refused"
                : error.Contains("timed out", StringComparison.OrdinalIgnoreCase) ? "timeout"
                : error.Contains("No route to host", StringComparison.Ordinal) || error.Contains("Network is unreachable", StringComparison.Ordinal) ? "network"
                : "failed");
        }
        var last = result.StandardOutput.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.StartsWith("sftp> ", StringComparison.Ordinal)) ?? "";
        return new PithyxException(ErrorCodes.BackupTargetNotWritable, new Dictionary<string, object?>
        {
            ["reason"] = last.StartsWith("sftp> cd ", StringComparison.Ordinal) ? "path" : "write",
            // The login folder has no name of its own; ~ is how SSH users know it.
            ["path"] = path.Length == 0 ? "~" : path,
        });
    }

    /// <summary>
    /// What ssh's messages in <paramref name="standardError"/> say about a connection that failed: a changed host key, a
    /// refused key, or a server it could not reach; null when they name none of these.
    /// </summary>
    public static PithyxException? ConnectionFailure(string standardError)
    {
        ArgumentNullException.ThrowIfNull(standardError);
        if (standardError.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.Ordinal)
            || standardError.Contains("Host key verification failed", StringComparison.Ordinal))
        {
            var parameters = new Dictionary<string, object?>();
            if (OfferedFingerprint().Match(standardError) is { Success: true } offered)
            {
                parameters["fingerprint"] = offered.Groups["fingerprint"].Value;
            }
            return new PithyxException(ErrorCodes.BackupTargetHostKeyChanged, parameters);
        }
        if (standardError.Contains("Permission denied (", StringComparison.Ordinal) || standardError.Contains("Too many authentication failures", StringComparison.Ordinal))
        {
            return new PithyxException(ErrorCodes.BackupTargetAuthFailed);
        }
        var reason =
            standardError.Contains("Could not resolve hostname", StringComparison.Ordinal) ? "dns"
            : standardError.Contains("Connection refused", StringComparison.Ordinal) ? "refused"
            : standardError.Contains("Connection timed out", StringComparison.Ordinal) || standardError.Contains("Operation timed out", StringComparison.Ordinal) ? "timeout"
            : standardError.Contains("No route to host", StringComparison.Ordinal) || standardError.Contains("Network is unreachable", StringComparison.Ordinal) ? "network"
            : null;
        return reason is null ? null : Unreachable(reason);
    }

    /// <summary>The error a stored test outcome carries, as JSON for the table.</summary>
    public static string Params(PithyxException error) => JsonSerializer.Serialize(error.Parameters);

    /// <summary>
    /// The ssh options of every connection to a target; writes the target's key and its known_hosts line into
    /// <paramref name="folder"/>. The destination is always <see cref="Alias"/>.
    /// </summary>
    public static IReadOnlyList<string> Options(KeyFolder folder, SshEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(endpoint);
        var key = folder.Write("key", endpoint.PrivateKey.EndsWith('\n') ? endpoint.PrivateKey : endpoint.PrivateKey + "\n");
        var hosts = folder.Write("known_hosts", $"{Alias} {endpoint.HostKey}\n");
        return
        [
            "-F", "/dev/null",
            "-i", key,
            "-o", "IdentitiesOnly=yes",
            "-o", "IdentityAgent=none",
            "-o", "UserKnownHostsFile=" + hosts,
            "-o", "GlobalKnownHostsFile=/dev/null",
            "-o", "StrictHostKeyChecking=yes",
            "-o", "UpdateHostKeys=no",
            "-o", "BatchMode=yes",
            "-o", "ConnectTimeout=" + BackupTargetRules.Text((int)ConnectTimeout.TotalSeconds),
            "-o", "ServerAliveInterval=30",
            "-o", "LogLevel=ERROR",
            "-o", "HostKeyAlias=" + Alias,
            "-o", "HostName=" + endpoint.Host,
            "-o", "Port=" + BackupTargetRules.Text(endpoint.Port),
            "-o", "User=" + endpoint.User,
        ];
    }

    /// <summary><c>env</c>'s arguments that clear the daemon's environment for an ssh tool.</summary>
    public static IReadOnlyList<string> CleanEnvironment() => ["-i", "PATH=/usr/bin:/bin", "LC_ALL=C.UTF-8", "HOME=/nonexistent"];

    private static PithyxException Unreachable(string reason) =>
        new(ErrorCodes.BackupTargetUnreachable, new Dictionary<string, object?> { ["reason"] = reason });

    private static PithyxException ScanFailed(string reason) =>
        new(ErrorCodes.BackupHostKeyScanFailed, new Dictionary<string, object?> { ["reason"] = reason });

    [GeneratedRegex(@"key sent by the remote host is\s+(?<fingerprint>SHA256:[A-Za-z0-9+/]+)")]
    private static partial Regex OfferedFingerprint();
}

/// <summary>A folder in the temporary folder only the daemon's user can enter, for keys; deleted when disposed.</summary>
public sealed class KeyFolder : IDisposable
{
    public KeyFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pithyx-backup-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else
        {
            Directory.CreateDirectory(Path);
        }
    }

    public string Path { get; }

    /// <summary>Writes a file only the daemon's user can read and returns its path.</summary>
    public string Write(string name, string content)
    {
        var file = System.IO.Path.Combine(Path, name);
        File.WriteAllText(file, content);
        // ssh refuses a key others can read.
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return file;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
