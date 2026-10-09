using System.Text.RegularExpressions;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Apps.Catalog;

/// <summary>Where a catalog repository is: a URL the box may fetch, and for SSH the host and port.</summary>
/// <param name="Url">The URL as the administrator gave it, without a trailing slash.</param>
/// <param name="Ssh">Fetched over SSH with the catalog's deploy key.</param>
/// <param name="Host">The server, for SSH its host key.</param>
/// <param name="Port">The SSH port (22 by default), 0 for https and file.</param>
public sealed partial record CatalogUrl(string Url, bool Ssh, string Host, int Port)
{
    /// <summary>
    /// Checks a URL an administrator entered: <c>https://host/path</c> without credentials, SSH as
    /// <c>user@host:path</c> or <c>ssh://user@host[:port]/path</c>, and <c>file://</c> only where the caller allows it
    /// (simulation and tests). Throws <see cref="ErrorCodes.StoreCatalogUrlInvalid"/>.
    /// </summary>
    public static CatalogUrl Parse(string? input, bool allowFile = false)
    {
        var url = (input ?? "").Trim().TrimEnd('/');
        if (url.Length is 0 or > 500 || url.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw Invalid("path");
        }
        if (allowFile && url.StartsWith("file:///", StringComparison.Ordinal))
        {
            return new CatalogUrl(url, false, "", 0);
        }
        if (ScpLike().Match(url) is { Success: true } scp)
        {
            return new CatalogUrl(url, true, CheckHost(scp.Groups["host"].Value), 22);
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "ssh"))
        {
            throw Invalid("scheme");
        }
        if (uri.UserInfo.Contains(':', StringComparison.Ordinal) || (uri.Scheme == "https" && uri.UserInfo.Length > 0))
        {
            // Credentials in a URL would be stored and logged; private catalogs use a deploy key.
            throw Invalid("host");
        }
        if (uri.AbsolutePath.Length <= 1 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw Invalid("path");
        }
        return uri.Scheme == "ssh"
            ? new CatalogUrl(url, true, CheckHost(uri.Host), uri.IsDefaultPort || uri.Port < 0 ? 22 : uri.Port)
            : new CatalogUrl(url, false, CheckHost(uri.Host), 0);
    }

    private static string CheckHost(string host) =>
        HostName().IsMatch(host) ? host.ToLowerInvariant() : throw Invalid("host");

    private static PithyxException Invalid(string reason) =>
        new(ErrorCodes.StoreCatalogUrlInvalid, new Dictionary<string, object?> { ["reason"] = reason });

    [GeneratedRegex(@"^[A-Za-z0-9._-]+@(?<host>[^:/]+):(?!/)[^\s]+$")]
    private static partial Regex ScpLike();

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9-]{0,62})(\.[A-Za-z0-9]([A-Za-z0-9-]{0,62}))*$")]
    private static partial Regex HostName();
}

/// <summary>
/// Git for catalogs, through <see cref="ICommandRunner"/> and hardened (D328): no system or user configuration, no
/// hooks, no submodules, no redirects, objects checked on transfer, symbolic links checked out as plain files, only
/// https and ssh (file only where allowed), SSH only with the catalog's own key against its pinned host key, and a
/// timeout on every call. The box fetches one branch shallowly into a bare repository and checks a commit out into a
/// folder it then verifies; it never runs anything from a catalog.
/// </summary>
public sealed class CatalogGit(ICommandRunner commands, bool allowFile)
{
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(3);

    /// <summary>GitHub's published host keys (<c>GET /meta</c>), so a box pins them without asking the network.</summary>
    public static readonly IReadOnlyList<string> GitHubHostKeys =
    [
        "github.com ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl",
        "github.com ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBEmKSENjQEezOmxkZMy7opKgwFB9nkt5YRrYMjNuG5N87uRgg6CLrbo5wAdT/y6v0mKV0U2w0WZ2YB/++Tpockg=",
        "github.com ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABgQCj7ndNxQowgcQnjshcLrqPEiiphnt+VTTvDP6mHBL9j1aNUkY4Ue1gvwnGLVlOhGeYrnZaMgRK6+PKCUXaDbC7qtbW8gIkhL7aGCsOr/C56SJMy/BCZfxd1nWzAOxSDPgVsmerOBYfNqltV9/hWCqBywINIR+5dIg6JTJ72pcEpEjcYgXkE2YEFXV1JHnsKgbLWNlhScqb2UmyRkQyytRLtL+38TGxkxCflmO+5Z8CSSNY7GidjMIZ7Q4zMjA2n1nGrlTDkzwDCsw+wqFPGQA179cnfGWOWRVruj16z6XyvxvjJwbz0wQZ75XK5tKSb7FNyeIEs4TT4jk+S4dhPeAUC5y+bDYirYgM4GC7uEnztnZyaVWQ7B381AK4Qdrwt51ZqExKbQpTUNn+EjqoTwvqNj4kqx5QUCI0ThS/YkOxJCXmPUWZbhjpCg56i+2aB6CmK2JGhn57K5mj0MNdBXA4/WnwH6XoPWJzK5Nyu2zB3nAZp+S5hpQs+p1vN1/wsjk=",
    ];

    public bool AllowsFile => allowFile;

    /// <summary>The known_hosts lines a catalog's SSH fetch accepts: GitHub's built in, otherwise the pinned line.</summary>
    public static IReadOnlyList<string> KnownHosts(CatalogUrl url, string? pinned) =>
        url.Host == "github.com" && url.Port == 22 ? GitHubHostKeys : pinned is null ? [] : [pinned];

    /// <summary>
    /// Fetches <paramref name="branch"/> with depth 1 into the bare repository <paramref name="repository"/> (made when
    /// missing) and returns the fetched commit. <paramref name="sshKey"/> is the deploy key in OpenSSH format for an SSH
    /// URL. Throws <see cref="ErrorCodes.StoreCatalogUnreachable"/>.
    /// </summary>
    public async Task<string> FetchAsync(CatalogUrl url, string branch, string repository, string? sshKey, IReadOnlyList<string> knownHosts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!Directory.Exists(repository))
        {
            Directory.CreateDirectory(repository);
            await RunAsync(["init", "--quiet", "--bare", repository], null, TimeSpan.FromSeconds(30), cancellationToken);
        }
        string? sshFolder = null;
        try
        {
            string? sshCommand = null;
            if (url.Ssh)
            {
                sshFolder = PrivateFolder("pithyx-catalog-");
                var key = Path.Combine(sshFolder, "key");
                File.WriteAllText(key, sshKey ?? "");
                // ssh refuses a key others can read; the daemon runs on Linux only.
                if (OperatingSystem.IsLinux())
                {
                    File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                var hosts = Path.Combine(sshFolder, "known_hosts");
                File.WriteAllLines(hosts, knownHosts.Select(line => url.Port == 22 ? line : $"[{url.Host}]:{url.Port} {line[(line.IndexOf(' ', StringComparison.Ordinal) + 1)..]}"));
                sshCommand = $"ssh -F /dev/null -i {key} -o IdentitiesOnly=yes -o IdentityAgent=none -o UserKnownHostsFile={hosts} -o GlobalKnownHostsFile=/dev/null -o StrictHostKeyChecking=yes -o BatchMode=yes -o ConnectTimeout=20";
            }
            var result = await RunAsync(
                ["--git-dir", repository, "fetch", "--quiet", "--depth", "1", "--no-tags", "--no-recurse-submodules", "--", url.Url, $"+refs/heads/{branch}:refs/heads/fetched"],
                sshCommand,
                FetchTimeout,
                cancellationToken);
            if (result.ExitCode != 0)
            {
                throw Unreachable(Classify(result.StandardError));
            }
            var commit = await RunAsync(["--git-dir", repository, "rev-parse", "--verify", "refs/heads/fetched^{commit}"], null, TimeSpan.FromSeconds(30), cancellationToken);
            return commit.ExitCode == 0 ? commit.StandardOutput.Trim() : throw Unreachable("failed");
        }
        catch (TimeoutException)
        {
            throw Unreachable("timeout");
        }
        finally
        {
            if (sshFolder is not null && Directory.Exists(sshFolder))
            {
                Directory.Delete(sshFolder, recursive: true);
            }
        }
    }

    /// <summary>Writes the files of <paramref name="commit"/> into the new folder <paramref name="target"/>.</summary>
    public async Task CheckoutAsync(string repository, string commit, string target, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(target);
        var result = await RunAsync(
            ["--git-dir", repository, "--work-tree", target, "checkout", "--quiet", "--force", commit, "--", "."],
            null,
            TimeSpan.FromMinutes(2),
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw Unreachable("failed");
        }
    }

    /// <summary>Makes an Ed25519 deploy key; returns the private key in OpenSSH format and the public line.</summary>
    public async Task<(string PrivateKey, string PublicKey)> KeygenAsync(string comment, CancellationToken cancellationToken)
    {
        var folder = PrivateFolder("pithyx-keygen-");
        try
        {
            var key = Path.Combine(folder, "key");
            var result = await commands.RunAsync(new CommandRequest("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-C", comment, "-f", key], TimeSpan.FromSeconds(30)), cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new PithyxException(ErrorCodes.SystemInternalUnexpected);
            }
            return (File.ReadAllText(key), File.ReadAllText(key + ".pub").Trim());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>The server's Ed25519 host key as a known_hosts line, for pinning at the first fetch; null when it has none.</summary>
    public async Task<string?> KeyscanAsync(CatalogUrl url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        var result = await commands.RunAsync(new CommandRequest("ssh-keyscan", ["-T", "15", "-t", "ed25519", "-p", url.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), url.Host], TimeSpan.FromSeconds(30)), cancellationToken);
        var line = result.StandardOutput.Split('\n').Select(entry => entry.Trim()).FirstOrDefault(entry => entry.Length > 0 && !entry.StartsWith('#'));
        if (line is null)
        {
            return null;
        }
        // ssh-keyscan names a port other than 22 as [host]:port; the box writes the plain host and adds the port itself.
        var space = line.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? null : $"{url.Host} {line[(space + 1)..]}";
    }

    /// <summary>Why a fetch failed, from git's error output.</summary>
    public static string Classify(string standardError)
    {
        ArgumentNullException.ThrowIfNull(standardError);
        if (standardError.Contains("Host key verification failed", StringComparison.Ordinal)
            || standardError.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.Ordinal)
            || standardError.Contains("No ED25519 host key is known", StringComparison.Ordinal)
            || standardError.Contains("host key for", StringComparison.OrdinalIgnoreCase) && standardError.Contains("not known", StringComparison.Ordinal))
        {
            return "host_key";
        }
        if (standardError.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase))
        {
            return "branch";
        }
        if (standardError.Contains("Permission denied", StringComparison.Ordinal)
            || standardError.Contains("Repository not found", StringComparison.OrdinalIgnoreCase)
            || standardError.Contains("could not read Username", StringComparison.Ordinal)
            || standardError.Contains("Authentication failed", StringComparison.Ordinal)
            || standardError.Contains("does not appear to be a git repository", StringComparison.Ordinal)
            || standardError.Contains("returned error: 401", StringComparison.Ordinal)
            || standardError.Contains("returned error: 403", StringComparison.Ordinal)
            || standardError.Contains("returned error: 404", StringComparison.Ordinal)
            || standardError.Contains("returned error: 30", StringComparison.Ordinal))
        {
            return "auth";
        }
        if (standardError.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || standardError.Contains("Could not resolve host", StringComparison.Ordinal)
            || standardError.Contains("Could not resolve hostname", StringComparison.Ordinal)
            || standardError.Contains("Connection refused", StringComparison.Ordinal))
        {
            return "timeout";
        }
        return "failed";
    }

    /// <summary>A new folder in the temporary folder that only the daemon's user can enter, for keys.</summary>
    private static string PrivateFolder(string prefix)
    {
        var folder = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else
        {
            Directory.CreateDirectory(folder);
        }
        return folder;
    }

    private static PithyxException Unreachable(string reason) =>
        new(ErrorCodes.StoreCatalogUnreachable, new Dictionary<string, object?> { ["reason"] = reason });

    private Task<CommandResult> RunAsync(IReadOnlyList<string> arguments, string? sshCommand, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // env -i: nothing of the daemon's environment (a proxy, an askpass, a git configuration) reaches git.
        List<string> prefix =
        [
            "-i", "PATH=/usr/bin:/bin", "LC_ALL=C.UTF-8", "HOME=/nonexistent", "GIT_CONFIG_NOSYSTEM=1", "GIT_CONFIG_GLOBAL=/dev/null",
            "GIT_TERMINAL_PROMPT=0", "GIT_ASKPASS=/bin/false", "SSH_ASKPASS=/bin/false", "GIT_PROTOCOL_FROM_USER=0",
            "git",
            "-c", "core.hooksPath=/dev/null", "-c", "core.symlinks=false", "-c", "core.fsmonitor=false", "-c", "core.sshCommand=" + (sshCommand ?? "false"),
            "-c", "credential.helper=", "-c", "http.followRedirects=false", "-c", "submodule.recurse=false",
            "-c", "transfer.fsckObjects=true", "-c", "fetch.fsckObjects=true", "-c", "protocol.allow=never",
            "-c", "protocol.https.allow=always", "-c", "protocol.ssh.allow=always", "-c", "protocol.file.allow=" + (allowFile ? "always" : "never"),
        ];
        return commands.RunAsync(new CommandRequest("env", [.. prefix, .. arguments], timeout), cancellationToken);
    }
}
