using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dapper;
using Pithyx.Daemon.Apps.Docker;
using Pithyx.Daemon.Audit;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Logs;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.SystemSettings;

namespace Pithyx.Daemon.SupportBundles;

/// <param name="Directory">Where the bundle is written; root only, emptied at every start.</param>
/// <param name="MemoryDumps">Whether the daemon can dump its own memory (not in simulation).</param>
public sealed record SupportBundleOptions(string Directory, bool MemoryDumps);

/// <summary>
/// The support bundle (D412): what somebody helping with a problem needs, in one zip the administrator downloads and
/// shares by hand. It is made as a job, kept for an hour and replaced by the next one. Secrets never go in
/// (<see cref="SupportDatabase"/>, <see cref="ScrubText"/>); user names, paths and addresses stay readable. Only the
/// memory dump holds secrets, so it is never chosen by default and needs a step-up. Every command or file that could
/// not be read is named in <c>failures.json</c> instead of failing the bundle.
/// </summary>
public sealed partial class SupportBundleService : IDisposable
{
    public const string JobKind = "system.support_bundle";

    /// <summary>How long a bundle stays on the box.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    /// <summary>How far back the Log Center's entries go, and at most how many.</summary>
    public static readonly TimeSpan LogSpan = TimeSpan.FromDays(7);

    public const int MaxLogEntries = 200_000;

    /// <summary>Free space a bundle without a memory dump needs.</summary>
    public const long BaseSpaceBytes = 256L * 1024 * 1024;

    /// <summary>Configuration files of the box the bundle reads; secrets in them are blanked line by line.</summary>
    public static readonly IReadOnlyList<string> ConfigurationFiles =
    [
        "/etc/samba/smb.conf",
        "/etc/samba/pithyx.local.conf",
        "/etc/ssh/sshd_config.d/50-pithyx.conf",
        "/etc/docker/daemon.json",
        "/etc/systemd/system/docker.service.d/pithyx.conf",
        "/etc/systemd/timesyncd.conf.d/pithyx.conf",
        "/etc/fstab",
    ];

    public static readonly IReadOnlyList<string> NetworkFiles =
    [
        "/etc/hostname",
        "/etc/hosts",
        "/etc/resolv.conf",
        "/etc/network/interfaces",
        "/etc/systemd/resolved.conf.d/pithyx.conf",
    ];

    public static readonly IReadOnlyList<string> NetworkDirectories = ["/etc/network/interfaces.d", "/etc/systemd/network"];

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly SqliteConnectionFactory _connections;
    private readonly LogService _logs;
    private readonly SystemSettingsService _system;
    private readonly DiskInventory _disks;
    private readonly DockerEngine _docker;
    private readonly ICommandRunner _commands;
    private readonly JobService _jobs;
    private readonly AuditLog _audit;
    private readonly SupportBundleOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SupportBundleService> _logger;
    private readonly Lock _lock = new();
    private readonly ITimer _expiry;
    private Kept? _bundle;
    private long? _runningJob;

    public SupportBundleService(
        SqliteConnectionFactory connections,
        LogService logs,
        SystemSettingsService system,
        DiskInventory disks,
        DockerEngine docker,
        ICommandRunner commands,
        JobService jobs,
        AuditLog audit,
        SupportBundleOptions options,
        TimeProvider time,
        ILogger<SupportBundleService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _connections = connections;
        _logs = logs;
        _system = system;
        _disks = disks;
        _docker = docker;
        _commands = commands;
        _jobs = jobs;
        _audit = audit;
        _options = options;
        _time = time;
        _logger = logger;
        // Nothing survives a restart; a bundle of an earlier run goes.
        if (Directory.Exists(options.Directory))
        {
            Directory.Delete(options.Directory, recursive: true);
        }
        _expiry = time.CreateTimer(_ => Expire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _expiry.Dispose();
    }

    /// <summary>The bundle on the box and the job that makes one, if any.</summary>
    public SupportBundleState State()
    {
        Expire();
        lock (_lock)
        {
            return new SupportBundleState(_bundle?.Info, _runningJob is > 0 ? _runningJob : null);
        }
    }

    /// <summary>Starts the job that makes a new bundle; it replaces the one on the box.</summary>
    public async Task<Job> StartAsync(ClaimsPrincipal caller, IReadOnlyList<SupportBundlePart> parts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var chosen = parts.Distinct().Order().ToList();
        if (chosen.Count == 0 || chosen.Any(part => !Enum.IsDefined(part)))
        {
            throw new PithyxException(ErrorCodes.RequestBodyInvalid);
        }
        lock (_lock)
        {
            if (_runningJob is not null)
            {
                throw new PithyxException(ErrorCodes.SystemSupportBusy);
            }
            _runningJob = 0;
        }
        try
        {
            Discard();
            PrepareDirectory();
            var needed = BaseSpaceBytes + (chosen.Contains(SupportBundlePart.MemoryDump) ? 2 * Environment.WorkingSet : 0);
            var available = new DriveInfo(_options.Directory).AvailableFreeSpace;
            if (available < needed)
            {
                throw new PithyxException(ErrorCodes.SystemSupportNoSpace, new Dictionary<string, object?> { ["neededBytes"] = needed, ["availableBytes"] = available });
            }
            await _audit.RecordAsync(caller, AuditLog.SupportBundleCreated, null, new Dictionary<string, string>
            {
                ["parts"] = string.Join(',', chosen.Select(Name)),
            }, cancellationToken);
            var job = await _jobs.StartAsync(CurrentUser.Id(caller), JobKind, new { parts = chosen.Select(Name).ToList() }, context => CreateAsync(chosen, context), cancellationToken);
            lock (_lock)
            {
                _runningJob = job.Id;
            }
            return job;
        }
        catch
        {
            lock (_lock)
            {
                _runningJob = null;
            }
            throw;
        }
    }

    /// <summary>The file of the bundle with this id, or <c>system.support.not_found</c>.</summary>
    public (string Path, string FileName) File(string id)
    {
        Expire();
        lock (_lock)
        {
            if (_bundle is not { } bundle || bundle.Info.Id != id || !System.IO.File.Exists(bundle.Path))
            {
                throw new PithyxException(ErrorCodes.SystemSupportNotFound);
            }
            return (bundle.Path, bundle.Info.FileName);
        }
    }

    /// <summary>Deletes the bundle with this id.</summary>
    public void Delete(string id)
    {
        lock (_lock)
        {
            if (_bundle?.Info.Id != id)
            {
                throw new PithyxException(ErrorCodes.SystemSupportNotFound);
            }
        }
        Discard();
    }

    /// <summary>
    /// Blanks the value of every line that names a password, secret, token, pre-shared or private key, as in
    /// <c>smb.conf</c>, <c>interfaces</c> (<c>wpa-psk</c>) or a WireGuard <c>.netdev</c> (<c>PrivateKey=</c>).
    /// </summary>
    public static string ScrubText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return SecretLine().Replace(SupportDatabase.WithoutUrlPasswords(text), match => match.Groups["name"].Value + SupportDatabase.Omitted);
    }

    private async Task CreateAsync(List<SupportBundlePart> parts, JobContext context)
    {
        var createdAt = _time.GetUtcNow();
        var id = Secrets.NewToken(12);
        var host = Environment.MachineName;
        var fileName = string.Create(CultureInfo.InvariantCulture, $"pithyx-support-{FileSafe(host)}-{createdAt.UtcDateTime:yyyyMMdd-HHmmss}.zip");
        var path = Path.Combine(_options.Directory, $"{id}.zip");
        var writing = path + ".part";
        var failures = new List<SupportFailure>();
        try
        {
            await using (var stream = new FileStream(writing, WriteOptions()))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
            {
                var bundle = new Bundle(zip, failures);
                for (var index = 0; index < parts.Count; index++)
                {
                    context.Cancellation.ThrowIfCancellationRequested();
                    context.Report(new JobProgress(index, parts.Count, 0, 0, Name(parts[index])));
                    await CollectAsync(parts[index], bundle, context.Cancellation);
                }
                var manifest = new SupportManifest(1, createdAt, host, BuildInfo.Current.Version, BuildInfo.Current.Commit, [.. parts], bundle.Missing);
                bundle.Json("manifest.json", manifest);
                bundle.Json("failures.json", failures);
            }
            System.IO.File.Move(writing, path);
            var info = new SupportBundleInfo(id, fileName, new FileInfo(path).Length, [.. parts], failures.Count, createdAt, createdAt + Lifetime);
            lock (_lock)
            {
                _bundle = new Kept(info, path);
                _runningJob = null;
            }
            _expiry.Change(Lifetime, Timeout.InfiniteTimeSpan);
            LogCreated(info.SizeBytes, info.Parts.Count, failures.Count);
            context.Report(new JobProgress(parts.Count, parts.Count, info.SizeBytes, info.SizeBytes, null));
            context.SetResult(info);
        }
        catch
        {
            TryDelete(writing);
            lock (_lock)
            {
                _runningJob = null;
            }
            throw;
        }
    }

    private Task CollectAsync(SupportBundlePart part, Bundle bundle, CancellationToken cancellationToken) => part switch
    {
        SupportBundlePart.System => SystemAsync(bundle, cancellationToken),
        SupportBundlePart.Storage => StorageAsync(bundle, cancellationToken),
        SupportBundlePart.Network => NetworkAsync(bundle, cancellationToken),
        SupportBundlePart.Configuration => ConfigurationAsync(bundle, cancellationToken),
        SupportBundlePart.Logs => LogsAsync(bundle, cancellationToken),
        SupportBundlePart.Apps => AppsAsync(bundle, cancellationToken),
        SupportBundlePart.MemoryDump => MemoryDumpAsync(bundle, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(part)),
    };

    private async Task SystemAsync(Bundle bundle, CancellationToken cancellationToken)
    {
        await bundle.TryAsync("system/overview.json", () => Task.FromResult<object?>(_system.Overview()));
        await bundle.TryAsync("system/daemon.json", () => Task.FromResult<object?>(DaemonProcess()));
        foreach (var (name, file) in new[]
        {
            ("system/os-release", "/etc/os-release"),
            ("system/environment", "/etc/pithyx/environment"),
            ("system/cmdline", "/proc/cmdline"),
            ("system/meminfo", "/proc/meminfo"),
            ("system/loadavg", "/proc/loadavg"),
            ("system/mounts", "/proc/self/mounts"),
        })
        {
            await bundle.FileAsync(name, file, scrub: false, cancellationToken);
        }
        await CommandAsync(bundle, "system/packages.txt", new CommandRequest("dpkg-query", ["-W", "-f", "${Package}\t${Version}\n"], TimeSpan.FromSeconds(30)), cancellationToken);
        await CommandAsync(bundle, "system/failed-units.txt", new CommandRequest("systemctl", ["list-units", "--failed", "--no-pager", "--plain"], TimeSpan.FromSeconds(30)), cancellationToken);
    }

    private async Task StorageAsync(Bundle bundle, CancellationToken cancellationToken)
    {
        await CommandAsync(bundle, "storage/zpool-status.json", new CommandRequest("zpool", ["status", "-j", "--json-int"], TimeSpan.FromSeconds(30)), cancellationToken);
        await CommandAsync(bundle, "storage/zpool-get.json", new CommandRequest("zpool", ["get", "-j", "--json-int", "all"], TimeSpan.FromSeconds(30)), cancellationToken);
        await CommandAsync(bundle, "storage/zfs-get.json", new CommandRequest("zfs", ["get", "-j", "--json-int", "-t", "filesystem,volume", "all"], TimeSpan.FromSeconds(60)), cancellationToken);
        await CommandAsync(bundle, "storage/zfs-snapshots.json", new CommandRequest("zfs", ["list", "-j", "--json-int", "-t", "snapshot", "-o", "name,used,referenced,creation"], TimeSpan.FromSeconds(60)), cancellationToken);
        await CommandAsync(bundle, "storage/zpool-events.txt", new CommandRequest("zpool", ["events", "-v"], TimeSpan.FromSeconds(30)), cancellationToken);
        await CommandAsync(bundle, "storage/lsblk.json", SystemStateService.Lsblk, cancellationToken);
        await bundle.FileAsync("storage/arcstats", "/proc/spl/kstat/zfs/arcstats", scrub: false, cancellationToken);
        await bundle.FileAsync("storage/zfs-version", "/sys/module/zfs/version", scrub: false, cancellationToken);
        foreach (var disk in _disks.Current.Disks)
        {
            // -n standby: a sleeping disk stays asleep and answers nothing.
            await CommandAsync(bundle, $"storage/smart/{FileSafe(disk.Name)}.json", new CommandRequest("smartctl", ["-n", "standby", "-j", "-x", disk.Path], TimeSpan.FromSeconds(30)), cancellationToken);
        }
    }

    private async Task NetworkAsync(Bundle bundle, CancellationToken cancellationToken)
    {
        await CommandAsync(bundle, "network/addresses.json", new CommandRequest("ip", ["-j", "address", "show"], TimeSpan.FromSeconds(10)), cancellationToken);
        await CommandAsync(bundle, "network/routes4.json", new CommandRequest("ip", ["-j", "-4", "route", "show", "table", "all"], TimeSpan.FromSeconds(10)), cancellationToken);
        await CommandAsync(bundle, "network/routes6.json", new CommandRequest("ip", ["-j", "-6", "route", "show", "table", "all"], TimeSpan.FromSeconds(10)), cancellationToken);
        await CommandAsync(bundle, "network/nftables.json", new CommandRequest("nft", ["-j", "list", "ruleset"], TimeSpan.FromSeconds(10)), cancellationToken);
        foreach (var file in NetworkFiles)
        {
            await bundle.FileAsync("network/files" + file, file, scrub: true, cancellationToken);
        }
        foreach (var directory in NetworkDirectories.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
            {
                await bundle.FileAsync("network/files" + file, file, scrub: true, cancellationToken);
            }
        }
    }

    private async Task ConfigurationAsync(Bundle bundle, CancellationToken cancellationToken)
    {
        await bundle.TryAsync("configuration/database", async () =>
        {
            await using var connection = await _connections.OpenAsync(cancellationToken);
            // One read transaction, so every table comes from the same moment.
            await using var transaction = connection.BeginTransaction(deferred: true);
            var version = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT coalesce(max(version), 0) FROM schema_version", transaction: transaction, cancellationToken: cancellationToken));
            bundle.Json("configuration/database/schema.json", new { version });
            foreach (var (table, json) in await SupportDatabase.ExportAsync(connection, transaction, cancellationToken))
            {
                bundle.Bytes($"configuration/database/{table}.json", json);
            }
            return null;
        });
        foreach (var file in ConfigurationFiles)
        {
            await bundle.FileAsync("configuration/files" + file, file, scrub: true, cancellationToken);
        }
    }

    private async Task LogsAsync(Bundle bundle, CancellationToken cancellationToken)
    {
        await bundle.TryAsync("logs/log-center.jsonl", async () =>
        {
            var query = new LogQuery(_time.GetUtcNow() - LogSpan, null, null, null, null, null, LogService.MaxLimit);
            await using var entry = bundle.Open("logs/log-center.jsonl");
            await _logs.ExportAsync(query, text: false, entry, cancellationToken, MaxLogEntries);
            return null;
        });
        await CommandAsync(bundle, "logs/kernel.txt", new CommandRequest("journalctl", ["-k", "-b", "--no-pager", "-o", "short-iso-precise", "-n", "5000"], TimeSpan.FromSeconds(30)), cancellationToken);
    }

    private async Task AppsAsync(Bundle bundle, CancellationToken cancellationToken)
    {
        if (!await _docker.PingAsync(cancellationToken))
        {
            bundle.Json("apps/docker.json", new { running = false });
            return;
        }
        await bundle.TryAsync("apps/docker-info.json", async () =>
        {
            using var info = await _docker.InspectAsync("info", cancellationToken);
            return (object?)Project(info?.RootElement, "ServerVersion", "Driver", "DockerRootDir", "Containers", "ContainersRunning", "Images", "MemTotal", "NCPU", "SecurityOptions", "Warnings");
        });
        await bundle.TryAsync("apps/containers.json", async () =>
        {
            using var list = await _docker.InspectAsync("containers/json?all=1", cancellationToken);
            var containers = new JsonArray();
            if (list?.RootElement is not { ValueKind: JsonValueKind.Array } all)
            {
                return containers;
            }
            foreach (var container in all.EnumerateArray())
            {
                // Labels and the command line of other containers can carry secrets; only the box's own labels go in.
                var labels = new JsonObject();
                if (container.TryGetProperty("Labels", out var found) && found.ValueKind == JsonValueKind.Object)
                {
                    foreach (var label in found.EnumerateObject().Where(label => label.Name.StartsWith("com.pithyx.", StringComparison.Ordinal) || label.Name.StartsWith("com.docker.compose.", StringComparison.Ordinal)))
                    {
                        labels[label.Name] = label.Value.GetString();
                    }
                }
                var projected = Project(container, "Id", "Names", "Image", "ImageID", "Created", "State", "Status");
                projected["Labels"] = labels;
                containers.Add(projected);
            }
            return containers;
        });
    }

    private async Task MemoryDumpAsync(Bundle bundle, CancellationToken cancellationToken)
    {
        const string name = "memory/daemon.dmp";
        if (!_options.MemoryDumps)
        {
            bundle.Fail(name, "unsupported", null);
            return;
        }
        var dump = Path.Combine(_options.Directory, $"daemon-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.dmp");
        try
        {
            var createdump = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "createdump");
            var result = await _commands.RunAsync(
                new CommandRequest(createdump, ["--withheap", "-f", dump, Environment.ProcessId.ToString(CultureInfo.InvariantCulture)], TimeSpan.FromMinutes(10)),
                cancellationToken);
            if (result.ExitCode != 0 || !System.IO.File.Exists(dump))
            {
                bundle.Fail(name, "exit", new { exitCode = result.ExitCode, error = Truncate(result.StandardError) });
                return;
            }
            await bundle.CopyAsync(name, dump, CompressionLevel.Fastest, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            bundle.Fail(name, "error", new { error = exception.Message });
        }
        finally
        {
            TryDelete(dump);
        }
    }

    private async Task CommandAsync(Bundle bundle, string name, CommandRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _commands.RunAsync(request, cancellationToken);
            if (result.StandardOutput.Length > 0)
            {
                bundle.Text(name, result.StandardOutput);
            }
            if (result.ExitCode != 0)
            {
                bundle.Fail(name, "exit", new { command = request.FileName, exitCode = result.ExitCode, error = Truncate(result.StandardError) });
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            bundle.Fail(name, "error", new { command = request.FileName, error = exception.Message });
        }
    }

    private static object DaemonProcess()
    {
        using var process = Process.GetCurrentProcess();
        var memory = GC.GetGCMemoryInfo();
        return new
        {
            pid = Environment.ProcessId,
            startedAt = process.StartTime.ToUniversalTime(),
            workingSetBytes = process.WorkingSet64,
            privateBytes = process.PrivateMemorySize64,
            threads = process.Threads.Count,
            handles = process.HandleCount,
            gcHeapBytes = memory.HeapSizeBytes,
            gcCommittedBytes = memory.TotalCommittedBytes,
            gcCollections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) },
            gcServer = GCSettings.IsServerGC,
            runtime = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        };
    }

    private static JsonObject Project(JsonElement? element, params string[] names)
    {
        var projected = new JsonObject();
        if (element is { ValueKind: JsonValueKind.Object } value)
        {
            foreach (var name in names)
            {
                if (value.TryGetProperty(name, out var property))
                {
                    projected[name] = JsonNode.Parse(property.GetRawText());
                }
            }
        }
        return projected;
    }

    private void Expire()
    {
        Kept? expired;
        lock (_lock)
        {
            expired = _bundle is { } bundle && bundle.Info.ExpiresAt <= _time.GetUtcNow() ? bundle : null;
            if (expired is not null)
            {
                _bundle = null;
            }
        }
        if (expired is not null)
        {
            TryDelete(expired.Path);
        }
    }

    private void Discard()
    {
        Kept? kept;
        lock (_lock)
        {
            kept = _bundle;
            _bundle = null;
        }
        if (kept is not null)
        {
            TryDelete(kept.Path);
        }
        _expiry.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private static FileStreamOptions WriteOptions()
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        return options;
    }

    private void PrepareDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_options.Directory);
        }
        else
        {
            Directory.CreateDirectory(_options.Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch (IOException exception)
        {
            LogDeleteFailed(exception, path);
        }
    }

    private static string Name(SupportBundlePart part) => JsonNamingPolicy.CamelCase.ConvertName(part.ToString());

    private static string FileSafe(string text) =>
        new([.. text.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')]);

    private static string Truncate(string text) => text.Length <= 2000 ? text : text[..2000];

    [LoggerMessage(Level = LogLevel.Information, Message = "Created a support bundle of {SizeBytes} bytes with {Parts} parts; {Failures} items could not be read")]
    private partial void LogCreated(long sizeBytes, int parts, int failures);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete {Path}")]
    private partial void LogDeleteFailed(Exception exception, string path);

    [GeneratedRegex(@"^(?<name>[ \t]*[^\r\n=:]*?(?:password|passwd|passphrase|psk|secret|token|private[ _-]?key|preshared[ _-]?key)[^\r\n=:]*?(?:[ \t]*[=:][ \t]*|[ \t]+))\S[^\r\n]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SecretLine();

    private sealed record Kept(SupportBundleInfo Info, string Path);

    /// <summary>The contents of <c>manifest.json</c>.</summary>
    private sealed record SupportManifest(int Format, DateTimeOffset CreatedAt, string HostName, string Version, string? Commit, IReadOnlyList<SupportBundlePart> Parts, IReadOnlyList<string> Missing);

    /// <summary>A command or file that could not be read: <c>exit</c>, <c>error</c> or <c>unsupported</c>, with details.</summary>
    private sealed record SupportFailure(string Item, string Reason, object? Details);

    /// <summary>The zip being written, with the failures and the files that do not exist on this box.</summary>
    private sealed class Bundle(ZipArchive zip, List<SupportFailure> failures)
    {
        public List<string> Missing { get; } = [];

        public Stream Open(string name, CompressionLevel level = CompressionLevel.Optimal) => zip.CreateEntry(name, level).Open();

        public void Bytes(string name, byte[] content)
        {
            using var stream = Open(name);
            stream.Write(content);
        }

        public void Text(string name, string content) => Bytes(name, Encoding.UTF8.GetBytes(content));

        public void Json(string name, object? value) => Bytes(name, JsonSerializer.SerializeToUtf8Bytes(value, _json));

        public void Fail(string item, string reason, object? details) => failures.Add(new SupportFailure(item, reason, details));

        /// <summary>Writes what <paramref name="read"/> returns as JSON (nothing for null); an exception becomes a failure.</summary>
        public async Task TryAsync(string name, Func<Task<object?>> read)
        {
            try
            {
                if (await read() is { } value)
                {
                    Json(name, value);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Fail(name, "error", new { error = exception.Message });
            }
        }

        public async Task FileAsync(string name, string path, bool scrub, CancellationToken cancellationToken)
        {
            if (!System.IO.File.Exists(path))
            {
                Missing.Add(path);
                return;
            }
            try
            {
                var text = await System.IO.File.ReadAllTextAsync(path, cancellationToken);
                Text(name, scrub ? ScrubText(text) : text);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Fail(name, "error", new { error = exception.Message });
            }
        }

        public async Task CopyAsync(string name, string path, CompressionLevel level, CancellationToken cancellationToken)
        {
            await using var source = System.IO.File.OpenRead(path);
            await using var target = Open(name, level);
            await source.CopyToAsync(target, cancellationToken);
        }
    }
}
