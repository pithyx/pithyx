using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Dapper;
using Pithyx.Daemon.Apps;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Localization;

namespace Pithyx.Daemon.Notifications;

/// <summary>
/// A notification waiting for its channels: one for a user's own channels, or one for the box's channels when the
/// system told every administrator.
/// </summary>
/// <param name="UserId">The user it was for; null for the box's channels.</param>
/// <param name="Code">Its message code.</param>
/// <param name="Params">Its parameters.</param>
/// <param name="Tone">Its tone.</param>
/// <param name="App">The app that sent it, if any.</param>
/// <param name="CreatedAt">When it was created.</param>
public sealed record PendingDelivery(long? UserId, string Code, IReadOnlyDictionary<string, JsonElement> Params, NotificationTone Tone, string? App, DateTimeOffset CreatedAt);

/// <summary>The queue between the notification history and the channels; posting never waits.</summary>
public sealed class NotificationOutbox
{
    private readonly Channel<PendingDelivery> _queue = Channel.CreateBounded<PendingDelivery>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public void Post(PendingDelivery delivery) => _queue.Writer.TryWrite(delivery);

    public ChannelReader<PendingDelivery> Reader => _queue.Reader;
}

/// <summary>
/// Notifications beyond the desktop (D379): the channels of the users and of the box, the SMTP server, the language
/// each user reads, and the delivery of every notification to the channels that take it. A user's channel gets that
/// user's notifications; the box's channels get what the system tells every administrator. An app's notification
/// leaves the desktop only with the permission <c>notifications.external</c>, and only to its user's channels.
/// </summary>
public sealed partial class ChannelService(
    SqliteConnectionFactory connections,
    ChannelSender sender,
    DeliveryThrottle throttle,
    AppRegistry apps,
    NotificationDeliveryOptions options,
    TimeProvider time,
    ILogger<ChannelService> logger)
{
    public const string SmtpSettingsKey = "notifications.smtp";
    public const string ExternalPermission = "notifications.external";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly string _box = Dns.GetHostName();

    private const string Columns =
        "id AS Id, user_id AS UserId, name AS Name, kind AS Kind, enabled AS Enabled, config AS Config, categories AS Categories, "
        + "min_level AS MinLevel, last_sent_at AS LastSentAt, error_code AS ErrorCode, error_params AS ErrorParams";

    /// <summary>The channels of a user, or of the box when <paramref name="owner"/> is null.</summary>
    public async Task<NotificationChannelList> ListAsync(long? owner, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<ChannelRow>(
            new CommandDefinition($"SELECT {Columns} FROM notification_channels WHERE user_id IS @owner ORDER BY id", new { owner }, cancellationToken: cancellationToken));
        var smtp = await SmtpAsync(connection, cancellationToken);
        return new NotificationChannelList([.. rows.Select(View)], smtp is not null, owner is null ? ChannelRules.MaxSystemChannels : ChannelRules.MaxChannelsPerUser);
    }

    public async Task<NotificationChannel> CreateAsync(long? owner, NotificationChannelRequest request, CancellationToken cancellationToken)
    {
        var (name, config, categories) = ChannelRules.Check(request, null, options.AllowLoopback);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await EnsureEmailPossibleAsync(connection, request.Kind, cancellationToken);
        var max = owner is null ? ChannelRules.MaxSystemChannels : ChannelRules.MaxChannelsPerUser;
        var count = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition("SELECT COUNT(*) FROM notification_channels WHERE user_id IS @owner", new { owner }, cancellationToken: cancellationToken));
        if (count >= max)
        {
            throw new PithyxException(ErrorCodes.NotificationsChannelLimitReached, new Dictionary<string, object?> { ["max"] = max });
        }
        var now = SessionService.Format(time.GetUtcNow());
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO notification_channels (user_id, name, kind, enabled, config, categories, min_level, created_at, updated_at)
            VALUES (@owner, @name, @kind, @enabled, @config, @categories, @level, @now, @now)
            RETURNING id
            """,
            new
            {
                owner,
                name,
                kind = Text(request.Kind),
                enabled = request.Enabled ? 1 : 0,
                config = JsonSerializer.Serialize(config, _json),
                categories = JsonSerializer.Serialize(categories, _json),
                level = Text(request.MinLevel),
                now,
            },
            cancellationToken: cancellationToken));
        LogSaved(id, owner);
        return View(await RowAsync(connection, owner, id, cancellationToken));
    }

    public async Task<NotificationChannel> UpdateAsync(long? owner, long id, NotificationChannelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await connections.OpenAsync(cancellationToken);
        var row = await RowAsync(connection, owner, id, cancellationToken);
        if (Text(request.Kind) != row.Kind)
        {
            throw new PithyxException(ErrorCodes.NotificationsChannelInvalid, new Dictionary<string, object?> { ["field"] = "kind" });
        }
        var (name, config, categories) = ChannelRules.Check(request, Config(row), options.AllowLoopback);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE notification_channels
            SET name = @name, enabled = @enabled, config = @config, categories = @categories, min_level = @level, updated_at = @now
            WHERE id = @id
            """,
            new
            {
                id,
                name,
                enabled = request.Enabled ? 1 : 0,
                config = JsonSerializer.Serialize(config, _json),
                categories = JsonSerializer.Serialize(categories, _json),
                level = Text(request.MinLevel),
                now = SessionService.Format(time.GetUtcNow()),
            },
            cancellationToken: cancellationToken));
        LogSaved(id, owner);
        return View(await RowAsync(connection, owner, id, cancellationToken));
    }

    public async Task DeleteAsync(long? owner, long id, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var deleted = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM notification_channels WHERE id = @id AND user_id IS @owner", new { id, owner }, cancellationToken: cancellationToken));
        if (deleted == 0)
        {
            throw new PithyxException(ErrorCodes.NotificationsChannelNotFound);
        }
        throttle.Forget(id);
    }

    /// <summary>Sends a test message through the channel now, also when it is off, and answers with its new state.</summary>
    public async Task<NotificationChannel> TestAsync(long? owner, long id, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var row = await RowAsync(connection, owner, id, cancellationToken);
        var language = await LanguageAsync(connection, owner, cancellationToken);
        var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["name"] = JsonSerializer.SerializeToElement(row.Name) };
        var message = new OutgoingMessage(
            NotificationRenderer.Render(language, NotificationRenderer.TestCode, parameters, null, _box),
            NotificationRenderer.TestCode,
            parameters,
            NotificationLevel.Info,
            NotificationCategory.System,
            null,
            _box,
            time.GetUtcNow());
        var failure = await SendAsync(connection, row, message, cancellationToken);
        if (failure is not null)
        {
            throw failure;
        }
        return View(await RowAsync(connection, owner, id, cancellationToken));
    }

    public async Task<SmtpStatus> SmtpStatusAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return Status(await SmtpAsync(connection, cancellationToken));
    }

    /// <summary>Saves the SMTP server, or removes it with an empty host. The password stays unless a new one is given.</summary>
    public async Task<SmtpStatus> SetSmtpAsync(SmtpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await connections.OpenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(request.Host))
        {
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM settings WHERE key = @key", new { key = SmtpSettingsKey }, cancellationToken: cancellationToken));
            return Status(null);
        }
        var settings = CheckedSmtp(request, await SmtpAsync(connection, cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            new { key = SmtpSettingsKey, value = JsonSerializer.Serialize(settings, _json) },
            cancellationToken: cancellationToken));
        LogSmtpSaved(settings.Host, settings.Port);
        return Status(settings);
    }

    /// <summary>
    /// Sends a test email to one address through the server as it is in the form, saved or not; a missing password takes
    /// the saved one. Nothing is stored.
    /// </summary>
    public async Task TestSmtpAsync(SmtpTestRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await connections.OpenAsync(cancellationToken);
        var to = request.To?.Trim() ?? "";
        if (!ChannelRules.IsEmail(to))
        {
            throw SmtpInvalid("to");
        }
        if (string.IsNullOrWhiteSpace(request.Host))
        {
            throw SmtpInvalid("host");
        }
        var settings = CheckedSmtp(
            new SmtpRequest(request.Host, request.Port, request.Security, request.Username, request.Password, request.From),
            await SmtpAsync(connection, cancellationToken));
        var language = await LanguageAsync(connection, null, cancellationToken);
        var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["host"] = JsonSerializer.SerializeToElement(settings.Host) };
        var message = new OutgoingMessage(
            NotificationRenderer.Render(language, NotificationRenderer.SmtpTestCode, parameters, null, _box),
            NotificationRenderer.SmtpTestCode,
            parameters,
            NotificationLevel.Info,
            NotificationCategory.System,
            null,
            _box,
            time.GetUtcNow());
        try
        {
            await sender.SendEmailAsync(settings, [to], message, cancellationToken);
        }
        catch (PithyxException exception)
        {
            LogSmtpTestFailed(settings.Host, settings.Port, exception.Code, exception.Parameters.GetValueOrDefault("detail")?.ToString());
            throw;
        }
        LogSmtpTested(settings.Host, settings.Port);
    }

    // A non-empty request checked field by field; a null password keeps the saved one, an empty one removes it.
    private static SmtpSettings CheckedSmtp(SmtpRequest request, SmtpSettings? previous)
    {
        var host = request.Host?.Trim() ?? "";
        if (host.Length > 253 || host.Any(c => char.IsWhiteSpace(c) || c is '/' or '@') || Uri.CheckHostName(host.Trim('[', ']')) == UriHostNameType.Unknown)
        {
            throw SmtpInvalid("host");
        }
        if (request.Port is < 1 or > 65535)
        {
            throw SmtpInvalid("port");
        }
        if (!Enum.IsDefined(request.Security))
        {
            throw SmtpInvalid("security");
        }
        var username = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim();
        if (username is { Length: > 200 })
        {
            throw SmtpInvalid("username");
        }
        var password = request.Password is null ? previous?.Password : request.Password.Length == 0 ? null : request.Password;
        if (password is { Length: > 500 })
        {
            throw SmtpInvalid("password");
        }
        var from = request.From?.Trim() ?? "";
        if (!ChannelRules.IsEmail(from))
        {
            throw SmtpInvalid("from");
        }
        return new SmtpSettings(host, request.Port, request.Security, username, password, from);
    }

    /// <summary>The matrix of every event the box reports by the administrators' desktop and the box's channels (D407).</summary>
    public async Task<NotificationMatrix> MatrixAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var channels = (await connection.QueryAsync<ChannelRow>(new CommandDefinition(
            $"SELECT {Columns} FROM notification_channels WHERE user_id IS NULL ORDER BY id", cancellationToken: cancellationToken))).ToList();
        var rules = (await connection.QueryAsync<RuleRow>(new CommandDefinition(
            "SELECT event, channel_id AS ChannelId, enabled FROM notification_rules", cancellationToken: cancellationToken)))
            .ToDictionary(rule => (rule.Event, rule.ChannelId ?? 0), rule => rule.Enabled != 0);
        var cells = new List<NotificationMatrixCell>();
        foreach (var entry in NotificationEvents.All)
        {
            cells.Add(rules.TryGetValue((entry.Code, 0), out var desktop)
                ? new NotificationMatrixCell(entry.Code, null, desktop, true)
                : new NotificationMatrixCell(entry.Code, null, true, false));
            foreach (var channel in channels)
            {
                cells.Add(rules.TryGetValue((entry.Code, channel.Id), out var set)
                    ? new NotificationMatrixCell(entry.Code, channel.Id, set, true)
                    : new NotificationMatrixCell(entry.Code, channel.Id, Default(channel, entry), false));
            }
        }
        return new NotificationMatrix(
            [.. NotificationEvents.All.Select(entry => new NotificationMatrixEvent(entry.Code, entry.Category, entry.Level))],
            [.. channels.Select(channel => new NotificationMatrixChannel(channel.Id, channel.Name, Enum.Parse<ChannelKind>(channel.Kind, ignoreCase: true), channel.Enabled != 0))],
            cells);
    }

    /// <summary>
    /// Sets cells of the matrix; the others stay. A cell set to its default is stored all the same, so a later change of
    /// the channel's categories leaves it as it is.
    /// </summary>
    public async Task<NotificationMatrix> SetMatrixAsync(NotificationMatrixRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var changes = request.Cells ?? [];
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            var boxChannels = (await connection.QueryAsync<long>(new CommandDefinition(
                "SELECT id FROM notification_channels WHERE user_id IS NULL", cancellationToken: cancellationToken))).ToHashSet();
            foreach (var cell in changes)
            {
                if (cell?.Event is null || !NotificationEvents.ByCode.ContainsKey(cell.Event))
                {
                    throw new PithyxException(ErrorCodes.NotificationsEventUnknown, new Dictionary<string, object?> { ["event"] = cell?.Event });
                }
                if (cell.Channel is { } channel && !boxChannels.Contains(channel))
                {
                    throw new PithyxException(ErrorCodes.NotificationsChannelNotFound, new Dictionary<string, object?> { ["id"] = channel });
                }
            }
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            foreach (var cell in changes)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    DELETE FROM notification_rules WHERE event = @Event AND channel_id IS @Channel;
                    INSERT INTO notification_rules (event, channel_id, enabled) VALUES (@Event, @Channel, @enabled);
                    """,
                    new { cell.Event, cell.Channel, enabled = cell.Enabled ? 1 : 0 },
                    transaction,
                    cancellationToken: cancellationToken));
            }
            await transaction.CommitAsync(cancellationToken);
        }
        LogMatrixSaved(changes.Count);
        return await MatrixAsync(cancellationToken);
    }

    /// <summary>Whether the administrators get this event on their desktop; true for every event the matrix does not list.</summary>
    public static async Task<bool> DesktopTakesAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string code, CancellationToken cancellationToken) =>
        !NotificationEvents.ByCode.ContainsKey(code) || (await RulesAsync(connection, code, cancellationToken)).GetValueOrDefault(0, true);

    // The set cells of one event by channel id, the desktop under 0.
    private static async Task<Dictionary<long, bool>> RulesAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string code, CancellationToken cancellationToken) =>
        (await connection.QueryAsync<RuleRow>(new CommandDefinition(
            "SELECT event, channel_id AS ChannelId, enabled FROM notification_rules WHERE event = @code", new { code }, cancellationToken: cancellationToken)))
        .ToDictionary(rule => rule.ChannelId ?? 0, rule => rule.Enabled != 0);

    private static bool Default(ChannelRow row, BoxEvent entry) =>
        ChannelRules.Accepts(Categories(row), ParseLevel(row.MinLevel), entry.Category, entry.Level);

    /// <summary>Remembers the language the user's desktop uses for what the box writes to them.</summary>
    public async Task SetLanguageAsync(long userId, string language, CancellationToken cancellationToken)
    {
        if (language is null || !MessageTexts.Languages.Contains(language))
        {
            throw new PithyxException(ErrorCodes.NotificationsLanguageUnsupported, new Dictionary<string, object?> { ["language"] = language });
        }
        await using var connection = await connections.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO user_languages (user_id, language, updated_at) VALUES (@userId, @language, @now)
            ON CONFLICT (user_id) DO UPDATE SET language = excluded.language, updated_at = excluded.updated_at
            """,
            new { userId, language, now = SessionService.Format(time.GetUtcNow()) },
            cancellationToken: cancellationToken));
    }

    /// <summary>Hands one notification to every channel that takes it.</summary>
    public async Task DeliverAsync(PendingDelivery delivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        InstalledApp? app = null;
        if (delivery.App is not null)
        {
            // An app's notification leaves the desktop only with the permission, and never to the box's channels.
            app = apps.Find(delivery.App);
            if (delivery.UserId is null || app is null || CoreApps.All.Contains(delivery.App) || !app.Holds(ExternalPermission))
            {
                return;
            }
        }
        await using var connection = await connections.OpenAsync(cancellationToken);
        var rows = (await connection.QueryAsync<ChannelRow>(new CommandDefinition(
            $"SELECT {Columns} FROM notification_channels WHERE user_id IS @owner AND enabled = 1 ORDER BY id", new { owner = delivery.UserId }, cancellationToken: cancellationToken))).ToList();
        if (rows.Count == 0)
        {
            return;
        }
        var category = ChannelRules.CategoryOf(delivery.Code, delivery.Params);
        var level = ChannelRules.LevelOf(delivery.Tone);
        var language = await LanguageAsync(connection, delivery.UserId, cancellationToken);
        var message = new OutgoingMessage(
            NotificationRenderer.Render(language, delivery.Code, delivery.Params, app is null ? null : AppName(app, language), _box),
            delivery.Code,
            delivery.Params,
            level,
            category,
            delivery.App,
            _box,
            delivery.CreatedAt);
        var fingerprint = $"{delivery.Code}|{delivery.App}|{JsonSerializer.Serialize(delivery.Params)}";
        // The box's channels follow the matrix for the events it lists (D407); everything else, and every user's own
        // channel, follows the channel's categories and least level.
        var matrixEvent = delivery.UserId is null ? NotificationEvents.ByCode.GetValueOrDefault(delivery.Code) : null;
        var rules = matrixEvent is null ? [] : await RulesAsync(connection, matrixEvent.Code, cancellationToken);
        foreach (var row in rows)
        {
            var takes = matrixEvent is null
                ? ChannelRules.Accepts(Categories(row), ParseLevel(row.MinLevel), category, level)
                : rules.TryGetValue(row.Id, out var set) ? set : Default(row, matrixEvent);
            if (!takes)
            {
                continue;
            }
            if (!throttle.TryTake(row.Id, fingerprint))
            {
                LogHeldBack(row.Id, delivery.Code);
                continue;
            }
            await SendAsync(connection, row, message, cancellationToken);
        }
    }

    private async Task<PithyxException?> SendAsync(Microsoft.Data.Sqlite.SqliteConnection connection, ChannelRow row, OutgoingMessage message, CancellationToken cancellationToken)
    {
        var config = Config(row);
        PithyxException? failure = null;
        try
        {
            if (row.Kind == "email")
            {
                var smtp = await SmtpAsync(connection, cancellationToken) ?? throw new PithyxException(ErrorCodes.NotificationsSmtpMissing);
                await sender.SendEmailAsync(smtp, config.Recipients ?? [], message, cancellationToken);
            }
            else
            {
                await sender.SendWebhookAsync(config, message, cancellationToken);
            }
            LogDelivered(row.Id, message.Code);
        }
        catch (PithyxException exception)
        {
            failure = exception;
            LogFailed(row.Id, message.Code, exception.Code, exception.Parameters.GetValueOrDefault("detail")?.ToString());
        }
        var id = row.Id;
        var command = failure is null
            ? new CommandDefinition(
                "UPDATE notification_channels SET last_sent_at = @now, error_code = NULL, error_params = NULL WHERE id = @id",
                new { id, now = SessionService.Format(time.GetUtcNow()) },
                cancellationToken: cancellationToken)
            : new CommandDefinition(
                "UPDATE notification_channels SET error_code = @code, error_params = @parameters WHERE id = @id",
                new { id, code = failure.Code, parameters = JsonSerializer.Serialize(failure.Parameters, _json) },
                cancellationToken: cancellationToken);
        await connection.ExecuteAsync(command);
        return failure;
    }

    // The manifest's name in the language, else in English (required by the schema).
    private static string AppName(InstalledApp app, string language) =>
        app.Manifest.GetProperty("name") is var names && names.TryGetProperty(language, out var name) && name.GetString() is { } text
            ? text
            : names.GetProperty("en").GetString() ?? app.Id;

    private static async Task<string> LanguageAsync(Microsoft.Data.Sqlite.SqliteConnection connection, long? userId, CancellationToken cancellationToken)
    {
        var language = userId is null
            ? null
            : await connection.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT language FROM user_languages WHERE user_id = @userId", new { userId }, cancellationToken: cancellationToken));
        language ??= await connection.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT language FROM system_region WHERE id = 1", cancellationToken: cancellationToken));
        return MessageTexts.Pick(language);
    }

    private static async Task EnsureEmailPossibleAsync(Microsoft.Data.Sqlite.SqliteConnection connection, ChannelKind kind, CancellationToken cancellationToken)
    {
        if (kind == ChannelKind.Email && await SmtpAsync(connection, cancellationToken) is null)
        {
            throw new PithyxException(ErrorCodes.NotificationsSmtpMissing);
        }
    }

    private static async Task<SmtpSettings?> SmtpAsync(Microsoft.Data.Sqlite.SqliteConnection connection, CancellationToken cancellationToken)
    {
        var json = await connection.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT value FROM settings WHERE key = @key", new { key = SmtpSettingsKey }, cancellationToken: cancellationToken));
        return json is null ? null : JsonSerializer.Deserialize<SmtpSettings>(json, _json);
    }

    private static SmtpStatus Status(SmtpSettings? smtp) => smtp is null
        ? new SmtpStatus(false, null, 587, SmtpSecurity.StartTls, null, false, null)
        : new SmtpStatus(true, smtp.Host, smtp.Port, smtp.Security, smtp.Username, smtp.Password is not null, smtp.From);

    private static async Task<ChannelRow> RowAsync(Microsoft.Data.Sqlite.SqliteConnection connection, long? owner, long id, CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<ChannelRow>(new CommandDefinition(
            $"SELECT {Columns} FROM notification_channels WHERE id = @id AND user_id IS @owner", new { id, owner }, cancellationToken: cancellationToken))
        ?? throw new PithyxException(ErrorCodes.NotificationsChannelNotFound);

    private static NotificationChannel View(ChannelRow row)
    {
        var config = Config(row);
        return new NotificationChannel(
            row.Id,
            row.Name,
            row.Kind == "email" ? ChannelKind.Email : ChannelKind.Webhook,
            row.Enabled == 1,
            config.Recipients ?? [],
            config.Url,
            config.Format,
            [.. (config.Headers ?? []).Select(header => header.Name)],
            Categories(row),
            ParseLevel(row.MinLevel),
            row.LastSentAt is null ? null : DateTimeOffset.Parse(row.LastSentAt, System.Globalization.CultureInfo.InvariantCulture),
            row.ErrorCode,
            row.ErrorParams is null ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(row.ErrorParams, _json));
    }

    private static ChannelConfig Config(ChannelRow row) => JsonSerializer.Deserialize<ChannelConfig>(row.Config, _json) ?? new ChannelConfig();

    private static NotificationCategory[] Categories(ChannelRow row) => JsonSerializer.Deserialize<NotificationCategory[]>(row.Categories, _json) ?? [];

    private static NotificationLevel ParseLevel(string level) => Enum.Parse<NotificationLevel>(level, ignoreCase: true);

    private static string Text<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static PithyxException SmtpInvalid(string field) =>
        new(ErrorCodes.NotificationsSmtpInvalid, new Dictionary<string, object?> { ["field"] = field });

    private sealed class RuleRow
    {
        public string Event { get; init; } = "";
        public long? ChannelId { get; init; }
        public long Enabled { get; init; }
    }

    private sealed class ChannelRow
    {
        public long Id { get; init; }
        public long? UserId { get; init; }
        public string Name { get; init; } = "";
        public string Kind { get; init; } = "";
        public long Enabled { get; init; }
        public string Config { get; init; } = "{}";
        public string Categories { get; init; } = "[]";
        public string MinLevel { get; init; } = "warning";
        public string? LastSentAt { get; init; }
        public string? ErrorCode { get; init; }
        public string? ErrorParams { get; init; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification channel {ChannelId} saved (owner {Owner})")]
    private partial void LogSaved(long channelId, long? owner);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification matrix: {Count} cells set")]
    private partial void LogMatrixSaved(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "SMTP server set to {Host}:{Port}")]
    private partial void LogSmtpSaved(string host, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Test email sent through {Host}:{Port}")]
    private partial void LogSmtpTested(string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Test email through {Host}:{Port} failed: {Code} {Detail}")]
    private partial void LogSmtpTestFailed(string host, int port, string code, string? detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification {Code} delivered through channel {ChannelId}")]
    private partial void LogDelivered(long channelId, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {Code} not delivered through channel {ChannelId}: {ErrorCode} {Detail}")]
    private partial void LogFailed(long channelId, string code, string errorCode, string? detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification {Code} held back for channel {ChannelId}: a repeat or too many this hour")]
    private partial void LogHeldBack(long channelId, string code);
}

/// <summary>Takes notifications from the outbox to their channels, one after the other.</summary>
public sealed partial class NotificationDelivery(NotificationOutbox outbox, ChannelService channels, ILogger<NotificationDelivery> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var delivery in outbox.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await channels.DeliverAsync(delivery, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogUnexpected(exception, delivery.Code);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivering notification {Code} to its channels failed")]
    private partial void LogUnexpected(Exception exception, string code);
}
