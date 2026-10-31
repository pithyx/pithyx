using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MimeKit;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Notifications;

/// <summary>The stored settings of a channel: the recipients of an email channel, or a webhook with its secrets.</summary>
public sealed record ChannelConfig(IReadOnlyList<string>? Recipients = null, string? Url = null, WebhookFormat? Format = null, IReadOnlyList<WebhookHeader>? Headers = null);

/// <summary>
/// What channels accept and which notifications they get (D379): validation of a channel, the category and level of
/// a notification, and which addresses a webhook may never reach.
/// </summary>
public static class ChannelRules
{
    public const int MaxNameLength = 60;
    public const int MaxRecipients = 10;
    public const int MaxHeaders = 10;
    public const int MaxHeaderValueLength = 1000;
    public const int MaxUrlLength = 2000;
    public const int MaxChannelsPerUser = 10;
    public const int MaxSystemChannels = 20;

    // Set by the client or the transport; a channel may not replace them.
    private static readonly HashSet<string> _reservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Content-Type", "Transfer-Encoding", "Connection", "Expect", "Upgrade", "Te", "Trailer",
    };

    /// <summary>
    /// The checked name, config and routing of a request; <paramref name="previous"/> keeps header values not sent again,
    /// and only tests set <paramref name="allowLoopback"/>.
    /// </summary>
    public static (string Name, ChannelConfig Config, NotificationCategory[] Categories) Check(NotificationChannelRequest request, ChannelConfig? previous, bool allowLoopback = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > MaxNameLength)
        {
            throw Invalid("name");
        }
        if (!Enum.IsDefined(request.Kind) || !Enum.IsDefined(request.MinLevel))
        {
            throw Invalid("kind");
        }
        NotificationCategory[] categories = [.. (request.Categories ?? []).Distinct().Order()];
        if (categories.Length == 0 || categories.Any(category => !Enum.IsDefined(category)))
        {
            throw Invalid("categories");
        }
        var config = request.Kind == ChannelKind.Email
            ? new ChannelConfig(Recipients: CheckRecipients(request.Recipients))
            : new ChannelConfig(Url: CheckUrl(request.Url, allowLoopback), Format: request.Format ?? WebhookFormat.Json, Headers: CheckHeaders(request.Headers, previous?.Headers));
        if (config.Format is { } format && !Enum.IsDefined(format))
        {
            throw Invalid("format");
        }
        return (name, config, categories);
    }

    private static string[] CheckRecipients(IReadOnlyList<string>? recipients)
    {
        string[] addresses = [.. (recipients ?? []).Select(address => address?.Trim() ?? "").Distinct(StringComparer.OrdinalIgnoreCase)];
        if (addresses.Length is 0 or > MaxRecipients || addresses.Any(address => !IsEmail(address)))
        {
            throw Invalid("recipients");
        }
        return addresses;
    }

    /// <summary>A plain address such as <c>admin@example.com</c>, without a display name.</summary>
    public static bool IsEmail(string? address) =>
        address is { Length: > 2 and <= 254 }
        && !address.Any(char.IsWhiteSpace)
        && address.IndexOf('@', StringComparison.Ordinal) is > 0 and var at
        && at < address.Length - 1
        && address.LastIndexOf('@') == at
        && MailboxAddress.TryParse(address, out var mailbox)
        && string.IsNullOrEmpty(mailbox.Name)
        && string.Equals(mailbox.Address, address, StringComparison.Ordinal);

    private static string CheckUrl(string? value, bool allowLoopback)
    {
        var url = value?.Trim() ?? "";
        if (url.Length is 0 or > MaxUrlLength
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || (!allowLoopback && IsRefusedHost(uri.Host)))
        {
            throw Invalid("url");
        }
        return url;
    }

    private static WebhookHeader[] CheckHeaders(IReadOnlyList<WebhookHeader>? headers, IReadOnlyList<WebhookHeader>? previous)
    {
        var given = headers ?? [];
        if (given.Count > MaxHeaders)
        {
            throw Invalid("headers");
        }
        var checkedHeaders = new List<WebhookHeader>();
        foreach (var header in given)
        {
            var name = header?.Name?.Trim() ?? "";
            if (name.Length is 0 or > 100 || !name.All(IsTokenChar) || _reservedHeaders.Contains(name)
                || checkedHeaders.Any(other => string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw Invalid("headers");
            }
            var value = header!.Value ?? previous?.FirstOrDefault(old => string.Equals(old.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
            if (value is null || value.Length > MaxHeaderValueLength || value.Any(c => c is '\r' or '\n' or '\0'))
            {
                throw Invalid("headers");
            }
            checkedHeaders.Add(new WebhookHeader(name, value));
        }
        return [.. checkedHeaders];
    }

    private static bool IsTokenChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c, StringComparison.Ordinal);

    /// <summary>A name that is the box itself: <c>localhost</c> or a loopback, unspecified or link-local address.</summary>
    public static bool IsRefusedHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var name = host.Trim('[', ']').TrimEnd('.');
        if (name.Length == 0 || name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return IPAddress.TryParse(name, out var address) && IsRefusedAddress(address);
    }

    /// <summary>An address a webhook may never connect to, also when a name resolves to it.</summary>
    public static bool IsRefusedAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal;
        }
        var bytes = address.GetAddressBytes();
        return bytes[0] == 0 || (bytes[0] == 169 && bytes[1] == 254);
    }

    /// <summary>What a notification is about, from its code (and for a job from its kind).</summary>
    public static NotificationCategory CategoryOf(string code, IReadOnlyDictionary<string, JsonElement> parameters)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(parameters);
        if (code.StartsWith("jobs.", StringComparison.Ordinal))
        {
            var kind = parameters.TryGetValue("kind", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            return kind switch
            {
                _ when kind.StartsWith("storage.", StringComparison.Ordinal) => NotificationCategory.Storage,
                _ when kind.StartsWith("apps.", StringComparison.Ordinal) || kind.StartsWith("store.", StringComparison.Ordinal) => NotificationCategory.Apps,
                "system.update" or "system.rollback" => NotificationCategory.Updates,
                _ => NotificationCategory.System,
            };
        }
        return code switch
        {
            _ when code.StartsWith("storage.", StringComparison.Ordinal) => NotificationCategory.Storage,
            _ when code.StartsWith("update.", StringComparison.Ordinal) || code.StartsWith("apps.update.", StringComparison.Ordinal) || code.StartsWith("store.updates.", StringComparison.Ordinal) => NotificationCategory.Updates,
            _ when code.StartsWith("system.backup.", StringComparison.Ordinal) => NotificationCategory.Backups,
            _ when code.StartsWith("auth.", StringComparison.Ordinal) => NotificationCategory.Security,
            _ when code.StartsWith("app.", StringComparison.Ordinal) || code.StartsWith("apps.", StringComparison.Ordinal) || code.StartsWith("store.", StringComparison.Ordinal) => NotificationCategory.Apps,
            _ => NotificationCategory.System,
        };
    }

    public static NotificationLevel LevelOf(NotificationTone tone) => tone switch
    {
        NotificationTone.Critical => NotificationLevel.Error,
        NotificationTone.Warning => NotificationLevel.Warning,
        _ => NotificationLevel.Info,
    };

    /// <summary>Whether a channel gets a notification of this category and level.</summary>
    public static bool Accepts(IReadOnlyList<NotificationCategory> categories, NotificationLevel minLevel, NotificationCategory category, NotificationLevel level) =>
        level >= minLevel && categories.Contains(category);

    private static PithyxException Invalid(string field) =>
        new(ErrorCodes.NotificationsChannelInvalid, new Dictionary<string, object?> { ["field"] = field });
}

/// <summary>
/// Keeps channels quiet (D379): the same notification goes out at most once in <see cref="Repeat"/>, and a channel
/// sends at most <see cref="PerHour"/> in an hour. Everything held back stays in the desktop.
/// </summary>
public sealed class DeliveryThrottle(TimeProvider time)
{
    public static readonly TimeSpan Repeat = TimeSpan.FromMinutes(15);
    public const int PerHour = 20;

    private readonly Dictionary<long, Queue<DateTimeOffset>> _sent = [];
    private readonly Dictionary<(long Channel, string Fingerprint), DateTimeOffset> _last = [];
    private readonly Lock _lock = new();

    /// <summary>Takes a slot for the channel, or answers false when this is a repeat or the hour is full.</summary>
    public bool TryTake(long channelId, string fingerprint)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            foreach (var key in _last.Where(entry => entry.Value + Repeat <= now).Select(entry => entry.Key).ToList())
            {
                _last.Remove(key);
            }
            if (_last.ContainsKey((channelId, fingerprint)))
            {
                return false;
            }
            var sent = _sent.TryGetValue(channelId, out var queue) ? queue : _sent[channelId] = new Queue<DateTimeOffset>();
            while (sent.Count > 0 && sent.Peek() + TimeSpan.FromHours(1) <= now)
            {
                sent.Dequeue();
            }
            if (sent.Count >= PerHour)
            {
                return false;
            }
            sent.Enqueue(now);
            _last[(channelId, fingerprint)] = now;
            return true;
        }
    }

    /// <summary>Forgets a removed channel.</summary>
    public void Forget(long channelId)
    {
        lock (_lock)
        {
            _sent.Remove(channelId);
            foreach (var key in _last.Keys.Where(key => key.Channel == channelId).ToList())
            {
                _last.Remove(key);
            }
        }
    }
}
