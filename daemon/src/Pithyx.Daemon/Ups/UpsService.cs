using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Notifications;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.SystemSettings;

namespace Pithyx.Daemon.Ups;

/// <summary>
/// Watches the UPS (D377, #195): reads it every 5 seconds from a NUT server (the box's own for a UPS on USB, or another
/// one in the network; NUT has no events) and publishes <c>system/ups</c> when something changes. On battery it
/// notifies the administrators, and once the battery reaches the limits, the UPS says it is low, the server's primary
/// forces it or a UPS on battery stops answering for 15 seconds, it shuts the box down through systemd, which stops
/// the apps, the file services and the daemon and unmounts the pools. A UPS on USB then also cuts its power, so the box
/// starts again once the power is back.
/// </summary>
public sealed partial class UpsService(
    SqliteConnectionFactory connections,
    TopicHub hub,
    NotificationService notifications,
    SystemSettingsService system,
    ICommandRunner commands,
    DaemonMode mode,
    TimeProvider time,
    ILogger<UpsService> logger) : BackgroundService
{
    public const string Topic = "system/ups";
    public const string SettingsKey = "ups.settings";

    public const string OnBatteryCode = "system.ups.on_battery";
    public const string OnlineCode = "system.ups.online";
    public const string ShutdownCode = "system.ups.shutdown";
    public const string UnreachableCode = "system.ups.unreachable";

    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    /// <summary>How long a UPS on battery may stay silent before the box shuts down, as NUT's own monitor does.</summary>
    public static readonly TimeSpan DeadTime = TimeSpan.FromSeconds(15);

    /// <summary>How long a UPS may stay unreachable before the administrators hear of it.</summary>
    public static readonly TimeSpan UnreachableNotice = TimeSpan.FromMinutes(1);

    private const string NutDirectory = "/etc/nut";
    private const string UpsdProgram = "/usr/sbin/upsd";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _saving = new(1, 1);
    private UpsSettings? _settings;
    private UpsState _state = UpsState.Off;
    private CancellationTokenSource _restart = new();
    private bool _unreachableAnnounced;
    private bool _shuttingDown;

    /// <summary>Whether the NUT server for a UPS on USB is installed; simulation only pretends.</summary>
    public bool UsbAvailable => mode != DaemonMode.Real || File.Exists(UpsdProgram);

    public static TopicDefinition Definition(Func<UpsService> service) => new(
        Topic,
        typeof(UpsStatus),
        typeof(UpsStatus),
        async (_, cancellationToken) => await service().StatusAsync(cancellationToken),
        (user, _) => user.IsInRole(SessionAuthenticationHandler.AdminRole));

    public async Task<UpsStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var settings = await SettingsAsync(cancellationToken);
        lock (_lock)
        {
            return new UpsStatus(View(settings), _state, UsbAvailable);
        }
    }

    /// <summary>Saves new settings, writes the NUT files when a UPS on USB comes or goes, and starts watching anew.</summary>
    public async Task<UpsStatus> UpdateAsync(UpsSettingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _saving.WaitAsync(cancellationToken);
        try
        {
            var saved = await SettingsAsync(cancellationToken);
            var next = UpsRules.Apply(saved, request);
            if (next.Mode == UpsMode.Usb && !UsbAvailable)
            {
                throw new PithyxException(ErrorCodes.SystemUpsUnavailable);
            }
            await using (var connection = await connections.OpenAsync(cancellationToken))
            {
                await connection.ExecuteAsync(
                    "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
                    new { key = SettingsKey, value = JsonSerializer.Serialize(next, _json) });
            }
            lock (_lock)
            {
                _settings = next;
                _state = next.Mode == UpsMode.Off ? UpsState.Off : UpsState.Off with { Connection = UpsConnection.Connecting };
                _unreachableAnnounced = false;
                _restart.Cancel();
                _restart.Dispose();
                _restart = new CancellationTokenSource();
            }
            if (mode == DaemonMode.Real && UsbAvailable)
            {
                await ApplyNutAsync(next.Mode == UpsMode.Usb, cancellationToken);
            }
            Publish();
            return await StatusAsync(cancellationToken);
        }
        finally
        {
            _saving.Release();
        }
    }

    public override void Dispose()
    {
        _restart.Dispose();
        _saving.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (mode == DaemonMode.Real && UsbAvailable)
        {
            // A flag left by a shutdown that never came must not cut the power at the next one.
            File.Delete(UpsRules.KillPowerFlag);
            await ApplyNutAsync((await SettingsAsync(stoppingToken)).Mode == UpsMode.Usb, stoppingToken);
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            CancellationToken restart;
            lock (_lock)
            {
                restart = _restart.Token;
            }
            using var watch = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, restart);
            try
            {
                var settings = await SettingsAsync(stoppingToken);
                if (settings.Mode == UpsMode.Off)
                {
                    await Task.Delay(Timeout.Infinite, watch.Token);
                }
                else
                {
                    await WatchAsync(settings, watch.Token);
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // The settings changed; watch with the new ones.
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task WatchAsync(UpsSettings settings, CancellationToken cancellationToken)
    {
        var (host, port, name) = settings.Mode == UpsMode.Usb
            ? ("127.0.0.1", UpsSettings.DefaultPort, UpsRules.UsbName)
            : (settings.Host, settings.Port, settings.Name);
        DateTimeOffset? lostSince = null;
        while (true)
        {
            try
            {
                await using var client = await NutClient.ConnectAsync(host, port, cancellationToken);
                if (settings.Username.Length > 0)
                {
                    await client.LoginAsync(settings.Username, settings.Password, name, cancellationToken);
                }
                while (true)
                {
                    var reading = NutProtocol.Read(await client.ListVarsAsync(name, cancellationToken));
                    lostSince = null;
                    await ObserveAsync(reading, settings, cancellationToken);
                    await Task.Delay(PollInterval, time, cancellationToken);
                }
            }
            catch (NutException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lostSince ??= time.GetUtcNow();
                await LostAsync(exception.Problem, lostSince.Value, settings, cancellationToken);
                await Task.Delay(RetryInterval, time, cancellationToken);
            }
        }
    }

    private async Task ObserveAsync(UpsReading reading, UpsSettings settings, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        UpsState previous;
        UpsState next;
        lock (_lock)
        {
            previous = _state;
            var power = reading.OnBattery ? UpsPower.OnBattery : UpsPower.Online;
            next = new UpsState(
                UpsConnection.Connected,
                power,
                reading.LowBattery,
                reading.Charge,
                reading.Runtime,
                reading.Load,
                reading.Model,
                null,
                power == UpsPower.OnBattery ? previous.OnBatterySince ?? now : null,
                previous.ShutdownReason);
            _state = next;
            _unreachableAnnounced = false;
        }
        if (next != previous)
        {
            Publish();
        }
        if (next.Power == UpsPower.OnBattery && previous.Power != UpsPower.OnBattery)
        {
            LogOnBattery(reading.Charge, reading.Runtime);
            await NotifyAsync(OnBatteryCode, NotificationTone.Warning, cancellationToken, ("charge", reading.Charge), ("minutes", reading.Runtime / 60));
        }
        else if (next.Power == UpsPower.Online && previous.Power == UpsPower.OnBattery)
        {
            LogOnline(reading.Charge);
            await NotifyAsync(OnlineCode, NotificationTone.Success, cancellationToken, ("charge", reading.Charge));
        }
        if (UpsRules.ShutdownReason(reading, settings) is { } reason)
        {
            await ShutdownAsync(reason, settings, cancellationToken);
        }
    }

    private async Task LostAsync(string problem, DateTimeOffset since, UpsSettings settings, CancellationToken cancellationToken)
    {
        UpsState previous;
        bool changed;
        bool announce;
        var silent = time.GetUtcNow() - since;
        lock (_lock)
        {
            previous = _state;
            _state = previous with { Connection = UpsConnection.Unreachable, Problem = problem };
            changed = _state != previous;
            announce = silent >= UnreachableNotice && !_unreachableAnnounced;
            _unreachableAnnounced |= announce;
        }
        if (changed)
        {
            LogUnreachable(problem);
            Publish();
        }
        if (announce)
        {
            await NotifyAsync(UnreachableCode, NotificationTone.Warning, cancellationToken, ("problem", problem));
        }
        if (previous.Power == UpsPower.OnBattery && silent >= DeadTime)
        {
            await ShutdownAsync("lost", settings, cancellationToken);
        }
    }

    private async Task ShutdownAsync(string reason, UpsSettings settings, CancellationToken cancellationToken)
    {
        UpsState state;
        lock (_lock)
        {
            if (_shuttingDown)
            {
                return;
            }
            _shuttingDown = true;
            _state = state = _state with { ShutdownReason = reason };
        }
        LogShutdown(reason, state.Charge, state.Runtime);
        if (mode == DaemonMode.Real && settings.Mode == UpsMode.Usb)
        {
            await File.WriteAllTextAsync(UpsRules.KillPowerFlag, UpsRules.KillPowerMagic, cancellationToken);
        }
        // The power action waits two seconds, time enough for the notification and the topic.
        system.Power(PowerAction.Shutdown);
        Publish();
        await NotifyAsync(ShutdownCode, NotificationTone.Critical, cancellationToken, ("reason", reason));
    }

    /// <summary>Writes the NUT files that differ and, only then, starts or stops the driver and the server.</summary>
    private async Task ApplyNutAsync(bool usb, CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var (name, content) in usb ? UpsRules.UsbFiles() : UpsRules.OffFiles())
        {
            var path = Path.Combine(NutDirectory, name);
            if (File.Exists(path) && await File.ReadAllTextAsync(path, cancellationToken) == content)
            {
                continue;
            }
            // Overwriting keeps the package's owner and mode (root:nut, 0640).
            await File.WriteAllTextAsync(path, content, cancellationToken);
            changed = true;
        }
        if (!changed)
        {
            return;
        }
        IReadOnlyList<string[]> steps = usb
            ? [["restart", "nut-driver-enumerator.service"], ["enable", "nut-server.service"], ["restart", "nut-driver.target", "nut-server.service"]]
            : [["disable", "--now", "nut-server.service", "nut-driver.target"], ["restart", "nut-driver-enumerator.service"]];
        foreach (var arguments in steps)
        {
            var result = await commands.RunAsync(new CommandRequest("systemctl", arguments, TimeSpan.FromMinutes(1)), cancellationToken);
            if (result.ExitCode != 0)
            {
                LogNutFailed(string.Join(' ', arguments), result.ExitCode);
            }
        }
    }

    private async Task<UpsSettings> SettingsAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_settings is { } cached)
            {
                return cached;
            }
        }
        await using var connection = await connections.OpenAsync(cancellationToken);
        var json = await connection.ExecuteScalarAsync<string?>("SELECT value FROM settings WHERE key = @key", new { key = SettingsKey });
        var settings = json is null ? UpsSettings.Default : JsonSerializer.Deserialize<UpsSettings>(json, _json) ?? UpsSettings.Default;
        lock (_lock)
        {
            return _settings ??= settings;
        }
    }

    private void Publish()
    {
        UpsStatus status;
        lock (_lock)
        {
            status = new UpsStatus(View(_settings ?? UpsSettings.Default), _state, UsbAvailable);
        }
        hub.Publish(Topic, status);
    }

    private Task<int> NotifyAsync(string code, NotificationTone tone, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters) =>
        notifications.CreateForAdministratorsAsync(
            new NotificationDraft(code, parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Value), tone),
            cancellationToken);

    private static UpsSettingsView View(UpsSettings settings) => new(
        settings.Mode, settings.Host, settings.Port, settings.Name, settings.Username, settings.Password.Length > 0, settings.BatteryLow, settings.RuntimeLow);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The UPS runs on battery (charge {Charge} %, runtime {Runtime} s)")]
    private partial void LogOnBattery(int? charge, int? runtime);

    [LoggerMessage(Level = LogLevel.Information, Message = "The UPS is back on line power (charge {Charge} %)")]
    private partial void LogOnline(int? charge);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The UPS is unreachable: {Problem}")]
    private partial void LogUnreachable(string problem);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Shutting down for the UPS: {Reason} (charge {Charge} %, runtime {Runtime} s)")]
    private partial void LogShutdown(string reason, int? charge, int? runtime);

    [LoggerMessage(Level = LogLevel.Warning, Message = "systemctl {Arguments} for the UPS ended with {ExitCode}")]
    private partial void LogNutFailed(string arguments, int exitCode);
}
