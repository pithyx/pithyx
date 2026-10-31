namespace Pithyx.Daemon.Notifications;

/// <summary>How a channel delivers: an email through the box's SMTP server, or an HTTP POST.</summary>
public enum ChannelKind
{
    Email,
    Webhook,
}

/// <summary>
/// What a webhook sends: <c>json</c> (title, message and priority as Gotify reads them, plus code, params, level, app,
/// box and time) or <c>ntfy</c> (the text as the body, with the title and priority as headers).
/// </summary>
public enum WebhookFormat
{
    Json,
    Ntfy,
}

/// <summary>How serious a notification is: from its tone, critical is <c>error</c>, a warning <c>warning</c>, the rest <c>info</c>.</summary>
public enum NotificationLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>What a notification is about, so a channel can pick its events.</summary>
public enum NotificationCategory
{
    Storage,
    Updates,
    Backups,
    Security,
    Apps,
    System,
}

/// <summary>A header a webhook sends, for example a token.</summary>
/// <param name="Name">The header name.</param>
/// <param name="Value">Its value; in a request null keeps the value saved before under this name.</param>
public sealed record WebhookHeader(string Name, string? Value = null);

/// <summary>A channel that takes notifications beyond the desktop (D379).</summary>
/// <param name="Id">Stable id.</param>
/// <param name="Name">The name its owner gave it.</param>
/// <param name="Kind">Email or webhook.</param>
/// <param name="Enabled">Off keeps it without sending.</param>
/// <param name="Recipients">The addresses of an email channel.</param>
/// <param name="Url">The address of a webhook.</param>
/// <param name="Format">What a webhook sends.</param>
/// <param name="Headers">The names of the headers a webhook sends; their values never leave the box.</param>
/// <param name="Categories">The events it gets.</param>
/// <param name="MinLevel">The least serious notification it gets.</param>
/// <param name="LastSentAt">When it last delivered something.</param>
/// <param name="ErrorCode">Why the last delivery failed, until one works again.</param>
/// <param name="ErrorParams">Its parameters.</param>
public sealed record NotificationChannel(
    long Id,
    string Name,
    ChannelKind Kind,
    bool Enabled,
    IReadOnlyList<string> Recipients,
    string? Url,
    WebhookFormat? Format,
    IReadOnlyList<string> Headers,
    IReadOnlyList<NotificationCategory> Categories,
    NotificationLevel MinLevel,
    DateTimeOffset? LastSentAt,
    string? ErrorCode,
    IReadOnlyDictionary<string, object?>? ErrorParams);

/// <summary>A user's channels or those of the box.</summary>
/// <param name="Items">The channels, oldest first.</param>
/// <param name="EmailAvailable">An administrator set up the SMTP server, so email channels can send.</param>
/// <param name="Max">How many channels there may be.</param>
public sealed record NotificationChannelList(IReadOnlyList<NotificationChannel> Items, bool EmailAvailable, int Max);

/// <summary>A new or changed channel.</summary>
/// <param name="Name">1 to 60 characters.</param>
/// <param name="Kind">Email or webhook; it cannot change later.</param>
/// <param name="Enabled">Defaults to on.</param>
/// <param name="Recipients">For email: 1 to 10 addresses.</param>
/// <param name="Url">For a webhook: an <c>http</c> or <c>https</c> address, never one of the box itself.</param>
/// <param name="Format">For a webhook; defaults to <c>json</c>.</param>
/// <param name="Headers">For a webhook: at most 10 headers.</param>
/// <param name="Categories">The events it gets; at least one.</param>
/// <param name="MinLevel">Defaults to <c>warning</c>.</param>
public sealed record NotificationChannelRequest(
    string Name,
    ChannelKind Kind,
    bool Enabled = true,
    IReadOnlyList<string>? Recipients = null,
    string? Url = null,
    WebhookFormat? Format = null,
    IReadOnlyList<WebhookHeader>? Headers = null,
    IReadOnlyList<NotificationCategory>? Categories = null,
    NotificationLevel MinLevel = NotificationLevel.Warning);

/// <summary>How the box talks to its SMTP server.</summary>
public enum SmtpSecurity
{
    /// <summary>TLS from the start, usually port 465.</summary>
    Tls,

    /// <summary>Plain, then STARTTLS, usually port 587; refused when the server offers no STARTTLS.</summary>
    StartTls,

    /// <summary>Unencrypted, only for a server in the local network.</summary>
    None,
}

/// <summary>The SMTP server as it is stored, with its password.</summary>
public sealed record SmtpSettings(string Host, int Port, SmtpSecurity Security, string? Username, string? Password, string From);

/// <summary>Settings &gt; Notifications, the SMTP server, without its password.</summary>
/// <param name="Configured">A server is set; email channels can send.</param>
/// <param name="Host">The server's name or address.</param>
/// <param name="Port">Its port.</param>
/// <param name="Security">How the box talks to it.</param>
/// <param name="Username">The sign-in name, if it needs one.</param>
/// <param name="PasswordSet">A password is saved.</param>
/// <param name="From">The sender address.</param>
public sealed record SmtpStatus(bool Configured, string? Host, int Port, SmtpSecurity Security, string? Username, bool PasswordSet, string? From);

/// <summary>A new SMTP server, or none.</summary>
/// <param name="Host">Empty removes the server.</param>
/// <param name="Port">1 to 65535; defaults to 587.</param>
/// <param name="Security">Defaults to STARTTLS.</param>
/// <param name="Username">The sign-in name, or none.</param>
/// <param name="Password">A new password; null keeps the saved one, empty removes it.</param>
/// <param name="From">The sender address.</param>
public sealed record SmtpRequest(
    string? Host,
    int Port = 587,
    SmtpSecurity Security = SmtpSecurity.StartTls,
    string? Username = null,
    string? Password = null,
    string? From = null);

/// <summary>Body of <c>POST /api/system/notifications/smtp/test</c>: the server as it is in the form, and where to send.</summary>
/// <param name="To">The address that gets the test email.</param>
/// <param name="Host">The server's name or address.</param>
/// <param name="Port">1 to 65535; defaults to 587.</param>
/// <param name="Security">Defaults to STARTTLS.</param>
/// <param name="Username">The sign-in name, or none.</param>
/// <param name="Password">The password; null takes the saved one, empty sends without.</param>
/// <param name="From">The sender address.</param>
public sealed record SmtpTestRequest(
    string? To,
    string? Host,
    int Port = 587,
    SmtpSecurity Security = SmtpSecurity.StartTls,
    string? Username = null,
    string? Password = null,
    string? From = null);

/// <summary>A row of the notification matrix (D407).</summary>
/// <param name="Code">The event's notification code; its texts are under <c>notifications.json</c>.</param>
/// <param name="Category">What it is about.</param>
/// <param name="Level">How serious it usually is.</param>
public sealed record NotificationMatrixEvent(string Code, NotificationCategory Category, NotificationLevel Level);

/// <summary>A column of the notification matrix: one of the box's channels.</summary>
/// <param name="Id">The channel's id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind">Email or webhook.</param>
/// <param name="Enabled">Off sends nothing, whatever its cells say.</param>
public sealed record NotificationMatrixChannel(long Id, string Name, ChannelKind Kind, bool Enabled);

/// <summary>Whether a column gets an event.</summary>
/// <param name="Event">The event's code.</param>
/// <param name="Channel">The channel's id, or null for the administrators' desktop.</param>
/// <param name="Enabled">It gets the event.</param>
/// <param name="Changed">An administrator set it; otherwise it is the default (the desktop gets everything, a channel what its categories and least level take).</param>
public sealed record NotificationMatrixCell(string Event, long? Channel, bool Enabled, bool Changed);

/// <summary>Settings &gt; Notifications, the matrix of events by the desktop and the box's channels (D407).</summary>
/// <param name="Events">Every event the box reports to its administrators, in display order.</param>
/// <param name="Channels">The box's channels.</param>
/// <param name="Cells">One cell per event and column: the desktop first, then each channel.</param>
public sealed record NotificationMatrix(IReadOnlyList<NotificationMatrixEvent> Events, IReadOnlyList<NotificationMatrixChannel> Channels, IReadOnlyList<NotificationMatrixCell> Cells);

/// <summary>A cell to set.</summary>
/// <param name="Event">The event's code.</param>
/// <param name="Channel">A channel of the box, or null for the administrators' desktop.</param>
/// <param name="Enabled">Whether it gets the event.</param>
public sealed record NotificationMatrixChange(string Event, long? Channel, bool Enabled);

/// <summary>Body of <c>PUT /api/system/notifications/matrix</c>.</summary>
/// <param name="Cells">The cells to set; the others stay.</param>
public sealed record NotificationMatrixRequest(IReadOnlyList<NotificationMatrixChange> Cells);

/// <summary>Body of <c>PUT /api/notifications/language</c>.</summary>
/// <param name="Language">The language the user's desktop uses, <c>en</c> or <c>de</c>.</param>
public sealed record NotificationLanguageRequest(string Language);
