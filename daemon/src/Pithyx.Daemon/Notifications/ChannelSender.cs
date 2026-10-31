using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Notifications;

/// <summary>Where a channel's notifications may go; tests reach their own listeners on loopback.</summary>
/// <param name="AllowLoopback">Lets webhooks reach the box's own addresses. Only tests set it.</param>
public sealed record NotificationDeliveryOptions(bool AllowLoopback = false);

/// <summary>One notification for a channel, rendered, with what a webhook also sends.</summary>
public sealed record OutgoingMessage(
    RenderedNotification Text,
    string Code,
    IReadOnlyDictionary<string, JsonElement> Params,
    NotificationLevel Level,
    NotificationCategory Category,
    string? App,
    string Box,
    DateTimeOffset CreatedAt);

/// <summary>
/// Delivers to one channel (D379): an email through the SMTP server with MailKit, or an HTTP POST that never follows
/// a redirect and never reaches the box itself. A failure becomes <c>notifications.channel.failed</c> with the
/// server's words; secrets never reach the log or the error.
/// </summary>
public sealed class ChannelSender : IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private const int MaxDetailLength = 300;

    private readonly HttpClient _http;

    public ChannelSender(NotificationDeliveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = Timeout,
            ConnectCallback = async (context, cancellationToken) =>
            {
                // The name is checked after it resolved, so a name that points at the box is refused as well.
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
                var allowed = addresses.Where(address => options.AllowLoopback || !ChannelRules.IsRefusedAddress(address)).ToArray();
                if (allowed.Length == 0)
                {
                    throw new HttpRequestException("The address is the box itself.");
                }
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        _http = new HttpClient(handler) { Timeout = Timeout };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Pithyx", "1"));
    }

    public async Task SendEmailAsync(SmtpSettings smtp, IReadOnlyList<string> recipients, OutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(smtp);
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(message);
        using var mail = new MimeMessage();
        mail.From.Add(new MailboxAddress(message.Box, smtp.From));
        foreach (var recipient in recipients)
        {
            mail.To.Add(MailboxAddress.Parse(recipient));
        }
        mail.Subject = message.Text.Title;
        var body = message.Text.Body.Length == 0 ? message.Text.Footer : $"{message.Text.Body}\n\n-- \n{message.Text.Footer}";
        mail.Body = new TextPart("plain") { Text = body };
        mail.Headers.Add("Auto-Submitted", "auto-generated");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout * 2);
        using var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        try
        {
            var security = smtp.Security switch
            {
                SmtpSecurity.Tls => SecureSocketOptions.SslOnConnect,
                SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
                _ => SecureSocketOptions.None,
            };
            await client.ConnectAsync(smtp.Host, smtp.Port, security, timeout.Token);
            if (!string.IsNullOrEmpty(smtp.Username))
            {
                await client.AuthenticateAsync(smtp.Username, smtp.Password ?? "", timeout.Token);
            }
            await client.SendAsync(mail, timeout.Token);
            await client.DisconnectAsync(quit: true, timeout.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw Failed(exception is OperationCanceledException ? "timeout" : exception.Message);
        }
    }

    public async Task SendWebhookAsync(ChannelConfig config, OutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(message);
        using var request = new HttpRequestMessage(HttpMethod.Post, config.Url);
        if (config.Format == WebhookFormat.Ntfy)
        {
            request.Content = new StringContent(message.Text.Body.Length == 0 ? message.Text.Title : message.Text.Body, Encoding.UTF8, "text/plain");
            request.Headers.TryAddWithoutValidation("Title", HeaderText(message.Text.Title));
            request.Headers.TryAddWithoutValidation("Priority", message.Level switch { NotificationLevel.Error => "5", NotificationLevel.Warning => "4", _ => "3" });
            request.Headers.TryAddWithoutValidation("Tags", message.Level.ToString().ToLowerInvariant());
        }
        else
        {
            var body = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["title"] = message.Text.Title,
                ["message"] = message.Text.Body.Length == 0 ? message.Text.Title : message.Text.Body,
                // Gotify's scale of 0 to 10.
                ["priority"] = message.Level switch { NotificationLevel.Error => 8, NotificationLevel.Warning => 6, _ => 4 },
                ["level"] = message.Level.ToString().ToLowerInvariant(),
                ["category"] = message.Category.ToString().ToLowerInvariant(),
                ["code"] = message.Code,
                ["params"] = message.Params,
                ["app"] = message.App,
                ["box"] = message.Box,
                ["time"] = message.CreatedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            };
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }
        foreach (var header in config.Headers ?? [])
        {
            request.Headers.TryAddWithoutValidation(header.Name, header.Value);
        }
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw Failed($"HTTP {(int)response.StatusCode}");
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw Failed(exception is TaskCanceledException ? "timeout" : exception.InnerException?.Message ?? exception.Message);
        }
    }

    // ntfy reads RFC 2047 encoded headers, so a title with umlauts survives.
    private static string HeaderText(string text) =>
        text.All(char.IsAscii) ? text : $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}?=";

    private static PithyxException Failed(string detail)
    {
        var line = string.Join(' ', detail.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return new PithyxException(ErrorCodes.NotificationsChannelFailed, new Dictionary<string, object?>
        {
            ["detail"] = line.Length > MaxDetailLength ? line[..MaxDetailLength] : line,
        });
    }

    public void Dispose() => _http.Dispose();
}
