using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Pithyx.Daemon.Apps.Catalog;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Notifications;
using Pithyx.Daemon.Realtime;

namespace Pithyx.Daemon.Updates;

/// <summary>How the updater runs here.</summary>
/// <param name="Simulated">Simulation: no system is touched; an install walks through its steps and succeeds.</param>
/// <param name="SourceFile">The update source configuration (<c>/etc/pithyx/update-source.json</c>).</param>
/// <param name="Keyring">The keys signatures are checked against.</param>
/// <param name="InstalledVersion">The running version when the release file is not there (simulation, development).</param>
/// <param name="DefaultSource">The source without a configuration file: the project's releases on a box, a folder in simulation.</param>
public sealed record UpdateOptions(bool Simulated, string SourceFile, IReadOnlyList<TrustedKey> Keyring, string InstalledVersion, UpdateSourceConfig DefaultSource);

/// <summary>
/// Updates of the system (D297, D338): the daily check of the channel and of Debian, which only notifies; installing
/// an uploaded or downloaded update file as a box job through a copy of the daemon (<see cref="UpdateApplier"/>); the
/// rollback to a snapshot; the history; and, after the restart, the outcome the health check or a rollback left in
/// <c>result.json</c>. The check runs once a day at a time chosen once per box, never polling otherwise.
/// </summary>
public sealed partial class UpdateService(
    SqliteConnectionFactory connections,
    UpdateSystem system,
    UpdateSource source,
    UpdateOptions options,
    ICommandRunner commands,
    JobService jobs,
    NotificationService notifications,
    TopicHub hub,
    DeploymentEnvironment environment,
    TimeProvider time,
    ILogger<UpdateService> logger) : BackgroundService
{
    public const string Topic = "system/updates";
    public const string CheckSetting = "updates.check";
    public const string ChannelSetting = "updates.channel";
    public const string TimeSetting = "updates.time";
    public const string StatusSetting = "updates.status";
    public const string SequenceSetting = "updates.sequence.";

    public const string AvailableCode = "update.available";
    public const string DebianCode = "update.debian.available";
    public const string InstalledCode = "update.installed";
    public const string RolledBackCode = "update.rolled_back";
    public const string RestoredCode = "update.restored";

    public const string UpdateJob = "system.update";
    public const string RollbackJob = "system.rollback";

    private static readonly IReadOnlyList<string> _steps = ["download", "snapshot", "install", "debian", "bootloader", "restart"];

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _checking = new(1, 1);
    private readonly SemaphoreSlim _applying = new(1, 1);
    private SystemLayout _layout = new("unknown", null);
    private IReadOnlyList<SnapshotInfo> _snapshots = [];
    private CheckState _check = new(null, null, null, null, null);
    private VerifiedUpdate? _staged;
    private long? _runningJob;
    private bool _isChecking;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource _reschedule = new();

    /// <summary>What the last check found, kept in <c>updates.status</c> across restarts.</summary>
    private sealed record CheckState(DateTimeOffset? At, UpdateChannelDocument? Channel, DebianUpdates? Debian, UpdateProblem? Problem, string? Announced);

    private string InstalledVersion => options.Simulated ? options.InstalledVersion : system.InstalledVersion() is "0.0.0" ? options.InstalledVersion : system.InstalledVersion();

    /// <summary>Unsigned files only on a development box (D338).</summary>
    private bool AllowUnsigned => environment.Kind == DeploymentKind.Dev;

    public static TopicDefinition Definition(Func<UpdateService> service) => new(
        Topic,
        typeof(UpdateStatus),
        typeof(UpdateStatus),
        async (_, cancellationToken) => await service().StatusAsync(cancellationToken),
        (user, _) => user.IsInRole(SessionAuthenticationHandler.AdminRole));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await InitializeAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                CancellationTokenSource reschedule;
                lock (_lock)
                {
                    reschedule = _reschedule;
                }
                var settings = await SettingsAsync(stoppingToken);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, reschedule.Token);
                try
                {
                    var wait = settings.Next - time.GetUtcNow();
                    if (!settings.Enabled)
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, time, linked.Token);
                    }
                    else if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, time, linked.Token);
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    continue;
                }
                await CheckAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await EnsureTimeAsync(cancellationToken);
        _check = await LoadCheckAsync(cancellationToken);
        if (options.Simulated)
        {
            _layout = new SystemLayout(null, "simulated");
        }
        else
        {
            try
            {
                _layout = await system.LayoutAsync(cancellationToken);
                if (_layout.Supported)
                {
                    _snapshots = await system.SnapshotsAsync(cancellationToken);
                }
                await ImportOutcomeAsync(cancellationToken);
                WatchOutcome();
            }
#pragma warning disable CA1031 // The updater must never keep the daemon from starting; Settings shows it as unsupported.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                _layout = new SystemLayout("unknown", null);
                LogStartFailed(exception);
            }
        }
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            // An update that ran when the daemon stopped without a restart of the box did not finish.
            await connection.ExecuteAsync(
                "UPDATE system_updates SET state = 'failed', error_code = @code, finished_at = @now WHERE state = 'running'",
                new { code = ErrorCodes.UpdateApplyFailed, now = Format(time.GetUtcNow()) });
        }
        LogStarted(InstalledVersion, _layout.Reason ?? "supported");
        await PublishAsync(cancellationToken);
    }

    /// <summary>The health check writes its verdict while this daemon runs; the file's arrival is the event.</summary>
    private void WatchOutcome()
    {
        Directory.CreateDirectory(system.Paths.State);
        _watcher = new FileSystemWatcher(system.Paths.State, Path.GetFileName(system.Paths.ResultFile))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
        };
        _watcher.Created += (_, _) => _ = ImportLaterAsync();
        _watcher.Renamed += (_, _) => _ = ImportLaterAsync();
        _watcher.EnableRaisingEvents = true;
    }

    private async Task ImportLaterAsync()
    {
        try
        {
            await ImportOutcomeNowAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // A file that cannot be read now is read at the next start.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogImportFailed(exception);
        }
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        _reschedule.Dispose();
        _checking.Dispose();
        _applying.Dispose();
        base.Dispose();
    }

    // Status and settings.

    public async Task<UpdateStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var settings = await SettingsAsync(cancellationToken);
        var installed = InstalledVersion;
        var check = _check;
        var available = check.Channel is { } channel && channel.Channel == settings.Channel && AppServiceCompare(channel.Version, installed) > 0
            ? new AvailableUpdate(channel.Version, channel.MinimumVersion, channel.Changelog, channel.File.Size, channel.PublishedAt, AppServiceCompare(installed, channel.MinimumVersion) >= 0)
            : (AvailableUpdate?)null;
        var staged = _staged is { } file
            ? new StagedUpdate(file.Manifest.Version, file.Manifest.Channel, file.Manifest.MinimumVersion, file.Manifest.Changelog, file.Bytes,
                file.TrustedSignature, file.Manifest.Packages.Count, file.Manifest.Kernel.Release, file.Manifest.PublishedAt)
            : null;
        return new UpdateStatus(
            installed,
            settings.Channel,
            settings.Enabled,
            check.At,
            settings.Enabled ? settings.Next : null,
            _isChecking,
            available,
            check.Debian,
            check.Problem,
            _layout.Supported,
            _layout.Reason,
            staged,
            _runningJob,
            await HistoryAsync(cancellationToken),
            _snapshots);
    }

    public async Task<UpdateStatus> GetStatusAsync(ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        return await StatusAsync(cancellationToken);
    }

    private sealed record UpdateSettings(bool Enabled, string Channel, DateTimeOffset Next);

    private async Task<UpdateSettings> SettingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var values = (await connection.QueryAsync<(string Key, string Value)>(
                "SELECT key, value FROM settings WHERE key IN @keys", new { keys = new[] { CheckSetting, ChannelSetting, TimeSetting } }))
            .ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
        var enabled = !values.TryGetValue(CheckSetting, out var check) || check != "off";
        var channel = values.TryGetValue(ChannelSetting, out var value) && UpdateDocuments.IsChannel(value) ? value : UpdateDocuments.Stable;
        var minutes = values.TryGetValue(TimeSetting, out var at) && int.TryParse(at, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        var checkTime = string.Create(CultureInfo.InvariantCulture, $"{minutes / 60:00}:{minutes % 60:00}");
        return new UpdateSettings(enabled, channel, StoreService.NextCheck(checkTime, 0, time.GetUtcNow(), TimeZoneInfo.Local).ToUniversalTime());
    }

    /// <summary>The time of day of the daily check, chosen once per box at random so boxes do not all ask at once.</summary>
    private async Task EnsureTimeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT (key) DO NOTHING",
            new { key = TimeSetting, value = RandomNumberGenerator.GetInt32(24 * 60).ToString(CultureInfo.InvariantCulture) });
    }

    public async Task<UpdateStatus> SetSettingsAsync(ClaimsPrincipal caller, UpdateSettingsRequest request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        if (!UpdateDocuments.IsChannel(request.Channel))
        {
            throw new PithyxException(ErrorCodes.RequestBodyInvalid);
        }
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            foreach (var (key, value) in new[] { (CheckSetting, request.CheckEnabled ? "on" : "off"), (ChannelSetting, request.Channel) })
            {
                await connection.ExecuteAsync(
                    "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value", new { key, value });
            }
        }
        LogSettings(request.CheckEnabled, request.Channel);
        Reschedule();
        await PublishAsync(cancellationToken);
        return await StatusAsync(cancellationToken);
    }

    private void Reschedule()
    {
        CancellationTokenSource old;
        lock (_lock)
        {
            old = _reschedule;
            _reschedule = new CancellationTokenSource();
        }
        old.Cancel();
        old.Dispose();
    }

    // The check.

    public async Task<UpdateStatus> CheckNowAsync(ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        await CheckAsync(cancellationToken);
        return await StatusAsync(cancellationToken);
    }

    /// <summary>Reads the channel and Debian's updates and notifies administrators of what is new; installs nothing.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        await _checking.WaitAsync(cancellationToken);
        _isChecking = true;
        await PublishAsync(cancellationToken);
        try
        {
            var settings = await SettingsAsync(cancellationToken);
            var previous = _check;
            var channel = previous.Channel?.Channel == settings.Channel ? previous.Channel : null;
            UpdateProblem? problem = null;
            try
            {
                channel = await ReadChannelAsync(settings.Channel, cancellationToken);
            }
            catch (PithyxException exception) when (exception.Code.StartsWith("update.", StringComparison.Ordinal))
            {
                problem = new UpdateProblem(exception.Code, exception.Parameters);
                LogCheckProblem(exception.Code);
            }
            var debian = options.Simulated ? previous.Debian ?? new DebianUpdates(0, 0, []) : await DebianAsync(cancellationToken) ?? previous.Debian;
            var announced = previous.Announced;
            if (channel is not null && AppServiceCompare(channel.Version, InstalledVersion) > 0 && announced != channel.Version)
            {
                await notifications.CreateForAdministratorsAsync(new NotificationDraft(AvailableCode,
                    new Dictionary<string, object?> { ["version"] = channel.Version, ["installed"] = InstalledVersion }, NotificationTone.Info), cancellationToken);
                announced = channel.Version;
            }
            if (debian is { Security: > 0 } && previous.Debian?.Security != debian.Security)
            {
                await notifications.CreateForAdministratorsAsync(new NotificationDraft(DebianCode,
                    new Dictionary<string, object?> { ["count"] = debian.Count, ["security"] = debian.Security }, NotificationTone.Warning), cancellationToken);
            }
            _check = new CheckState(time.GetUtcNow(), channel, debian, problem, announced);
            await SaveCheckAsync(_check, cancellationToken);
        }
        finally
        {
            _isChecking = false;
            _checking.Release();
        }
        await PublishAsync(cancellationToken);
    }

    private async Task<UpdateChannelDocument> ReadChannelAsync(string channel, CancellationToken cancellationToken)
    {
        var config = UpdateSourceConfig.Load(options.SourceFile, options.DefaultSource);
        var (document, signature) = await source.ReadChannelAsync(config, channel, cancellationToken);
        var key = SequenceSetting + channel;
        long? known;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            known = await connection.ExecuteScalarAsync<long?>("SELECT CAST(value AS INTEGER) FROM settings WHERE key = @key", new { key });
        }
        var result = UpdateDocuments.CheckChannel(document, signature, options.Keyring, channel, known);
        if (result.Document is not { } checkedChannel)
        {
            throw result.Issues[0].ToException();
        }
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync(
                "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
                new { key, value = checkedChannel.Sequence.ToString(CultureInfo.InvariantCulture) });
        }
        return checkedChannel;
    }

    /// <summary>Debian's updates through apt's own lists; null when apt cannot read them.</summary>
    private async Task<DebianUpdates?> DebianAsync(CancellationToken cancellationToken)
    {
        var plan = UpdatePlans.CheckDebian();
        var refresh = await commands.RunAsync(plan[0], cancellationToken);
        var simulate = await commands.RunAsync(plan[1], cancellationToken);
        if (refresh.ExitCode != 0 || simulate.ExitCode != 0)
        {
            LogDebianFailed(refresh.ExitCode, simulate.ExitCode);
            return null;
        }
        var packages = SystemParsers.ParseSimulatedUpgrade(simulate.StandardOutput);
        return new DebianUpdates(packages.Count, packages.Count(package => package.Security), packages);
    }

    private async Task<CheckState> LoadCheckAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var json = await connection.ExecuteScalarAsync<string?>("SELECT value FROM settings WHERE key = @key", new { key = StatusSetting });
        try
        {
            return json is null ? new CheckState(null, null, null, null, null) : JsonSerializer.Deserialize<CheckState>(json, UpdateSystem.Json) ?? new CheckState(null, null, null, null, null);
        }
        catch (JsonException)
        {
            return new CheckState(null, null, null, null, null);
        }
    }

    private async Task SaveCheckAsync(CheckState check, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            new { key = StatusSetting, value = JsonSerializer.Serialize(check, UpdateSystem.Json) });
    }

    // Uploads.

    /// <summary>
    /// Reads an uploaded update file and checks it completely (signature, every package, the version against this box)
    /// before anything changes; it then waits for "Install". A new upload replaces it.
    /// </summary>
    public async Task<UpdateStatus> UploadAsync(ClaimsPrincipal caller, Stream body, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(body);
        await _applying.WaitAsync(cancellationToken);
        try
        {
            _staged = null;
            var settings = await SettingsAsync(cancellationToken);
            _staged = await UpdateFile.ReadAsync(body, system.Paths.StagedDirectory, Expectation(settings.Channel), cancellationToken);
            LogStaged(_staged.Manifest.Version, _staged.TrustedSignature);
        }
        finally
        {
            _applying.Release();
        }
        await PublishAsync(cancellationToken);
        return await StatusAsync(cancellationToken);
    }

    public async Task<UpdateStatus> DiscardUploadAsync(ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        _staged = null;
        if (Directory.Exists(system.Paths.StagedDirectory))
        {
            Directory.Delete(system.Paths.StagedDirectory, recursive: true);
        }
        await PublishAsync(cancellationToken);
        return await StatusAsync(cancellationToken);
    }

    private UpdateExpectation Expectation(string channel) =>
        new(options.Keyring, channel, AllowUnsigned, InstalledVersion, UpdateFile.BoxArchitecture);

    // Install and rollback.

    /// <summary>Installs the uploaded file or the channel's newest as a job of the box; the box restarts at its end.</summary>
    public async Task<Job> InstallAsync(ClaimsPrincipal caller, InstallUpdateRequest request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        RequireSupported();
        var online = request.Source switch
        {
            "online" => true,
            "file" => false,
            _ => throw new PithyxException(ErrorCodes.RequestBodyInvalid),
        };
        if (!online && _staged is null)
        {
            throw new PithyxException(ErrorCodes.UpdateFileNotFound);
        }
        var settings = await SettingsAsync(cancellationToken);
        var channel = _check.Channel;
        if (online && (channel is null || channel.Channel != settings.Channel || AppServiceCompare(channel.Version, InstalledVersion) <= 0))
        {
            throw new PithyxException(ErrorCodes.UpdateNoneAvailable);
        }
        if (!await _applying.WaitAsync(0, cancellationToken))
        {
            throw new PithyxException(ErrorCodes.UpdateApplyRunning);
        }
        try
        {
            var target = online ? channel!.Version : _staged!.Manifest.Version;
            var job = await jobs.StartAsync(UserId(caller), UpdateJob, new { version = target, source = request.Source }, async context =>
            {
                try
                {
                    await RunInstallAsync(context, online ? channel : null, settings.Channel, request.IncludeDebian, cancellationToken: context.Cancellation);
                }
                finally
                {
                    _runningJob = null;
                    _applying.Release();
                    await PublishAsync(CancellationToken.None);
                }
            }, cancellationToken);
            _runningJob = job.Id;
            await PublishAsync(cancellationToken);
            return job;
        }
        catch
        {
            _applying.Release();
            throw;
        }
    }

    private async Task RunInstallAsync(JobContext context, UpdateChannelDocument? channel, string boxChannel, bool includeDebian, CancellationToken cancellationToken)
    {
        var verified = _staged;
        if (channel is not null)
        {
            Step(context, "download");
            verified = await DownloadAsync(channel, boxChannel, cancellationToken);
        }
        var update = verified ?? throw new PithyxException(ErrorCodes.UpdateFileNotFound);
        var from = InstalledVersion;
        var historyId = await InsertHistoryAsync("update", channel is not null ? "online" : "file", from, update.Manifest.Version, cancellationToken);
        await PublishAsync(cancellationToken);
        UpdateProgress progress;
        try
        {
            if (options.Simulated)
            {
                await SimulateAsync(context, includeDebian, cancellationToken);
                await FinishHistoryAsync(historyId, "succeeded", null, null, cancellationToken);
                context.SetResult(new { version = update.Manifest.Version, state = "succeeded" });
                return;
            }
            var request = new UpdateApplyRequest(historyId, update.Directory, from, update.Manifest.Version, includeDebian, await HistoryAsync(cancellationToken));
            progress = await StartApplyAsync(context, request, cancellationToken);
        }
        catch (Exception exception)
        {
            var problem = exception as PithyxException;
            await FinishHistoryAsync(historyId, "failed", problem?.Code ?? ErrorCodes.UpdateApplyFailed, problem?.Parameters, CancellationToken.None);
            throw;
        }
        if (progress.State == "restarting")
        {
            await FinishHistoryAsync(historyId, "restarting", null, null, cancellationToken, finished: false);
            context.SetResult(new { version = update.Manifest.Version, state = "restarting" });
            return;
        }
        await FinishHistoryAsync(historyId, progress.State == "rolled_back" ? "rolled_back" : "failed", progress.ErrorCode, progress.ErrorParams, CancellationToken.None);
        throw new PithyxException(progress.ErrorCode ?? ErrorCodes.UpdateApplyFailed, progress.ErrorParams ?? new Dictionary<string, object?>());
    }

    /// <summary>Downloads the channel's file through the same checks as an upload, plus its size and SHA-256 from the channel.</summary>
    private async Task<VerifiedUpdate> DownloadAsync(UpdateChannelDocument channel, string boxChannel, CancellationToken cancellationToken)
    {
        var config = UpdateSourceConfig.Load(options.SourceFile, options.DefaultSource);
        await using var download = await source.OpenFileAsync(config, boxChannel, channel.File.Url, cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var hashing = new HashingStream(download, hash);
        var verified = await UpdateFile.ReadAsync(hashing, Path.Combine(system.Paths.Cache, "download"), Expectation(boxChannel), cancellationToken);
        if (verified.Bytes != channel.File.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != channel.File.Sha256
            || verified.Manifest.Version != channel.Version)
        {
            Directory.Delete(verified.Directory, recursive: true);
            throw new PithyxException(ErrorCodes.UpdateDownloadMismatch);
        }
        return verified;
    }

    /// <summary>Starts the apply in its own process and reports its steps until the box restarts or it failed.</summary>
    private async Task<UpdateProgress> StartApplyAsync(JobContext context, UpdateApplyRequest request, CancellationToken cancellationToken)
    {
        await system.LaunchApplyAsync(request, cancellationToken);
        while (true)
        {
            var progress = await system.WaitForProgressAsync(cancellationToken);
            if (progress is null)
            {
                continue;
            }
            Step(context, progress.Step);
            if (progress.State is "restarting" or "failed" or "rolled_back")
            {
                return progress;
            }
        }
    }

    private async Task SimulateAsync(JobContext context, bool includeDebian, CancellationToken cancellationToken)
    {
        foreach (var step in new[] { "snapshot", "install", "debian", "bootloader", "restart" })
        {
            if (step == "debian" && !includeDebian)
            {
                continue;
            }
            Step(context, step);
            // Real time, not the box's clock: the steps only have to be visible on the page.
            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        }
        lock (_lock)
        {
            _snapshots = [new SnapshotInfo(SystemSnapshots.Name(time.GetUtcNow()), InstalledVersion, time.GetUtcNow(), "6.12.111+deb13-amd64", "update"), .. _snapshots.Take(SystemSnapshots.Keep - 1)];
        }
    }

    private static void Step(JobContext context, string step)
    {
        var index = _steps.ToList().IndexOf(step);
        context.Report(new JobProgress(Math.Max(index, 0), _steps.Count, 0, 0, step));
    }

    /// <summary>Restores a snapshot after the administrator typed its version, then restarts into it.</summary>
    public async Task<Job> RollbackAsync(ClaimsPrincipal caller, RollbackRequest request, CancellationToken cancellationToken)
    {
        RequireAdmin(caller);
        ArgumentNullException.ThrowIfNull(request);
        RequireSupported();
        var snapshot = _snapshots.FirstOrDefault(snapshot => snapshot.Name == request.Snapshot)
            ?? throw new PithyxException(ErrorCodes.UpdateSnapshotNotFound, new Dictionary<string, object?> { ["snapshot"] = request.Snapshot });
        if (!string.Equals(request.Version?.Trim(), snapshot.Version, StringComparison.Ordinal))
        {
            throw new PithyxException(ErrorCodes.UpdateRollbackUnconfirmed, new Dictionary<string, object?> { ["typed"] = request.Version, ["version"] = snapshot.Version });
        }
        if (!await _applying.WaitAsync(0, cancellationToken))
        {
            throw new PithyxException(ErrorCodes.UpdateApplyRunning);
        }
        try
        {
            var job = await jobs.StartAsync(UserId(caller), RollbackJob, new { snapshot = snapshot.Name, version = snapshot.Version }, async context =>
            {
                try
                {
                    context.Report(new JobProgress(0, 1, 0, 0, "rollback"));
                    var historyId = await InsertHistoryAsync("rollback", "manual", InstalledVersion, snapshot.Version, context.Cancellation, snapshot.Name);
                    if (options.Simulated)
                    {
                        await FinishHistoryAsync(historyId, "succeeded", null, null, context.Cancellation);
                        return;
                    }
                    var history = (await HistoryAsync(context.Cancellation))
                        .Select(entry => entry.Id == historyId ? entry with { State = "succeeded", FinishedAt = time.GetUtcNow() } : entry).ToList();
                    await system.RollbackAsync(snapshot.Name, _layout.FileSystemUuid!,
                        new UpdateOutcome(historyId, "succeeded", null, null, time.GetUtcNow(), history), context.Cancellation);
                    await FinishHistoryAsync(historyId, "restarting", null, null, context.Cancellation, finished: false);
                    context.SetResult(new { version = snapshot.Version, state = "restarting" });
                    await system.RebootAsync();
                }
                finally
                {
                    _runningJob = null;
                    _applying.Release();
                    await PublishAsync(CancellationToken.None);
                }
            }, cancellationToken);
            _runningJob = job.Id;
            await PublishAsync(cancellationToken);
            return job;
        }
        catch
        {
            _applying.Release();
            throw;
        }
    }

    private void RequireSupported()
    {
        if (!_layout.Supported)
        {
            throw new PithyxException(ErrorCodes.UpdateSystemUnsupported, new Dictionary<string, object?> { ["reason"] = _layout.Reason });
        }
    }

    // History and the outcome after a restart.

    public async Task<IReadOnlyList<UpdateHistoryEntry>> HistoryAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<HistoryRow>(
            """
            SELECT id AS Id, kind AS Kind, source AS Source, from_version AS FromVersion, to_version AS ToVersion, snapshot AS Snapshot,
                state AS State, error_code AS ErrorCode, error_params AS ErrorParams, started_at AS StartedAt, finished_at AS FinishedAt
            FROM system_updates ORDER BY id DESC LIMIT 50
            """);
        return [.. rows.Select(row => row.ToEntry())];
    }

    private sealed record HistoryRow(long Id, string Kind, string Source, string FromVersion, string ToVersion, string? Snapshot, string State,
        string? ErrorCode, string? ErrorParams, string StartedAt, string? FinishedAt)
    {
        public UpdateHistoryEntry ToEntry() => new(Id, Kind, Source, FromVersion, ToVersion, Snapshot, State, ErrorCode,
            ErrorParams is null ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(ErrorParams),
            DateTimeOffset.Parse(StartedAt, CultureInfo.InvariantCulture), FinishedAt is null ? null : DateTimeOffset.Parse(FinishedAt, CultureInfo.InvariantCulture));
    }

    private async Task<long> InsertHistoryAsync(string kind, string from, string fromVersion, string toVersion, CancellationToken cancellationToken, string? snapshot = null)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO system_updates (kind, source, from_version, to_version, snapshot, state, started_at)
            VALUES (@kind, @from, @fromVersion, @toVersion, @snapshot, 'running', @now) RETURNING id
            """,
            new { kind, from, fromVersion, toVersion, snapshot, now = Format(time.GetUtcNow()) });
    }

    private async Task FinishHistoryAsync(long id, string state, string? errorCode, IReadOnlyDictionary<string, object?>? errorParams, CancellationToken cancellationToken, bool finished = true)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "UPDATE system_updates SET state = @state, error_code = @errorCode, error_params = @errorParams, finished_at = @finishedAt WHERE id = @id",
            new
            {
                id,
                state,
                errorCode,
                errorParams = errorParams is null ? null : JsonSerializer.Serialize(errorParams),
                finishedAt = finished ? Format(time.GetUtcNow()) : null,
            });
    }

    /// <summary>
    /// Takes over what the health check or a rollback left in <c>result.json</c>: the history the restored database
    /// lacks, the outcome of the update, and a notification to administrators.
    /// </summary>
    private async Task ImportOutcomeAsync(CancellationToken cancellationToken)
    {
        if (options.Simulated || UpdateSystem.ReadJson<UpdateOutcome>(system.Paths.ResultFile) is not { } outcome)
        {
            return;
        }
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            foreach (var carried in outcome.History)
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO system_updates (id, kind, source, from_version, to_version, snapshot, state, error_code, error_params, started_at, finished_at)
                    VALUES (@Id, @Kind, @Source, @FromVersion, @ToVersion, @Snapshot, @State, @ErrorCode, @ErrorParams, @StartedAt, @FinishedAt)
                    ON CONFLICT (id) DO NOTHING
                    """,
                    new
                    {
                        carried.Id,
                        carried.Kind,
                        carried.Source,
                        carried.FromVersion,
                        carried.ToVersion,
                        carried.Snapshot,
                        carried.State,
                        carried.ErrorCode,
                        ErrorParams = carried.ErrorParams is null ? null : JsonSerializer.Serialize(carried.ErrorParams),
                        StartedAt = Format(carried.StartedAt),
                        FinishedAt = carried.FinishedAt is { } at ? Format(at) : null,
                    }, transaction);
            }
            await connection.ExecuteAsync(
                "UPDATE system_updates SET state = @State, error_code = @ErrorCode, error_params = @ErrorParams, finished_at = @FinishedAt WHERE id = @HistoryId",
                new
                {
                    outcome.State,
                    outcome.ErrorCode,
                    outcome.HistoryId,
                    ErrorParams = outcome.ErrorParams is null ? null : JsonSerializer.Serialize(outcome.ErrorParams),
                    FinishedAt = Format(outcome.FinishedAt),
                }, transaction);
            await transaction.CommitAsync(cancellationToken);
        }
        var entry = (await HistoryAsync(cancellationToken)).FirstOrDefault(entry => entry.Id == outcome.HistoryId);
        if (entry is not null)
        {
            var (code, tone) = (entry.Kind, outcome.State) switch
            {
                ("update", "succeeded") => (InstalledCode, NotificationTone.Success),
                ("update", _) => (RolledBackCode, NotificationTone.Critical),
                _ => (RestoredCode, NotificationTone.Warning),
            };
            await notifications.CreateForAdministratorsAsync(new NotificationDraft(code, new Dictionary<string, object?>
            {
                ["version"] = entry.ToVersion,
                ["restored"] = entry.FromVersion,
                ["reason"] = outcome.ErrorParams is { } parameters && parameters.TryGetValue("command", out var command) ? command?.ToString() : null,
            }, tone), cancellationToken);
        }
        File.Delete(system.Paths.ResultFile);
        LogOutcome(outcome.HistoryId, outcome.State);
    }

    /// <summary>Called when the health check left its verdict while this daemon runs (the success of an update).</summary>
    public async Task ImportOutcomeNowAsync(CancellationToken cancellationToken)
    {
        await ImportOutcomeAsync(cancellationToken);
        await PublishAsync(cancellationToken);
    }

    // Helpers.

    private async Task PublishAsync(CancellationToken cancellationToken) => hub.Publish(Topic, await StatusAsync(cancellationToken));

    private static int AppServiceCompare(string left, string right) => Apps.AppService.CompareVersions(left, right);

    private static string Format(DateTimeOffset value) => SessionService.Format(value);

    private static long UserId(ClaimsPrincipal caller) =>
        long.TryParse(caller.FindFirstValue(ClaimTypes.NameIdentifier), CultureInfo.InvariantCulture, out var id) ? id : JobService.Box;

    private static void RequireAdmin(ClaimsPrincipal user)
    {
        if (!user.IsInRole(SessionAuthenticationHandler.AdminRole))
        {
            throw new PithyxException(ErrorCodes.AuthPermissionDenied);
        }
    }

    /// <summary>Hashes what a reader reads.</summary>
    private sealed class HashingStream(Stream inner, IncrementalHash hash) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            hash.AppendData(buffer, offset, read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            hash.AppendData(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Updates: Pithyx {Version} installed, system {Layout}")]
    private partial void LogStarted(string version, string layout);

    [LoggerMessage(Level = LogLevel.Information, Message = "Update settings: check {Enabled}, channel {Channel}")]
    private partial void LogSettings(bool enabled, string channel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The update check could not read the channel: {Code}")]
    private partial void LogCheckProblem(string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "apt could not list Debian's updates (update {Update}, simulate {Simulate})")]
    private partial void LogDebianFailed(int update, int simulate);

    [LoggerMessage(Level = LogLevel.Information, Message = "Update file {Version} uploaded and verified (signed {Signed})")]
    private partial void LogStaged(string version, bool signed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The updater could not read the system's state; updates stay off until the next start")]
    private partial void LogStartFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The outcome of an update could not be read")]
    private partial void LogImportFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Update {HistoryId} ended after the restart: {State}")]
    private partial void LogOutcome(long historyId, string state);
}
