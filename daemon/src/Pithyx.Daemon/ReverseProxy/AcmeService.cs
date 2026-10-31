using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using Dapper;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Notifications;
using Pithyx.Daemon.Tls;

namespace Pithyx.Daemon.ReverseProxy;

/// <summary>
/// Certificates from an ACME authority for proxy hosts and redirect hosts (D378, #194): Let's Encrypt unless an
/// administrator names another directory. The box keeps one account key per authority in the TLS directory, answers
/// HTTP-01 on port 80 through the proxy and stores each certificate as <c>tls/proxy-host-&lt;id&gt;-acme.pem</c> and
/// <c>.key</c>, so the backup part Certificates takes them along. It asks at once for a route without a certificate or
/// with other names, a third of the lifetime before the end, and after a failure again with a backoff from one hour to
/// a day. Every request is a box job; a failed renewal notifies the administrators.
/// </summary>
public sealed partial class AcmeService(
    SqliteConnectionFactory connections,
    ProxyService proxy,
    AcmeRuntime runtime,
    TlsOptions tlsOptions,
    JobService jobs,
    NotificationService notifications,
    TimeProvider time,
    ILogger<AcmeService> logger) : BackgroundService
{
    public const string SettingsKey = "proxy.acme";
    public const string Job = "proxy.certificate";
    public const string RenewalFailedCode = "proxy.acme.renewal_failed";

    private static readonly TimeSpan _checkInterval = TimeSpan.FromHours(12);
    private static readonly TimeSpan _termsLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan _validationPoll = TimeSpan.FromSeconds(2);
    private const int MaxValidationPolls = 60;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly SemaphoreSlim _requesting = new(1, 1);
    private (string Directory, string? Terms, DateTimeOffset At)? _terms;

    public async Task<AcmeAccount> AccountAsync(CancellationToken cancellationToken)
    {
        var settings = await SettingsAsync(cancellationToken);
        var terms = await TermsAsync(settings.Directory, fresh: false, cancellationToken);
        return new AcmeAccount(settings.Directory, settings.Email, terms, settings.TermsAgreed is not null, terms is not null);
    }

    public async Task<AcmeAccount> SetAccountAsync(AcmeAccountRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var saved = await SettingsAsync(cancellationToken);
        var directory = string.IsNullOrWhiteSpace(request.Directory) ? AcmeSettings.LetsEncrypt : request.Directory.Trim();
        var terms = request.AgreeToTerms ? await TermsAsync(directory, fresh: true, cancellationToken) : null;
        var next = AcmeRules.Apply(saved, request, terms);
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync(
                "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
                new { key = SettingsKey, value = JsonSerializer.Serialize(next, _json) });
        }
        runtime.Wake();
        return await AccountAsync(cancellationToken);
    }

    /// <summary>Asks the authority for a route's certificate now, as a job, regardless of the backoff.</summary>
    public async Task<Job> RenewAsync(ClaimsPrincipal caller, string route, CancellationToken cancellationToken)
    {
        var target = (await proxy.AcmeRoutesAsync(cancellationToken)).FirstOrDefault(entry => entry.Name == route)
            ?? throw new PithyxException(ErrorCodes.ProxyAcmeNotAcme);
        return await StartAsync(CurrentUser.Id(caller), target, cancellationToken);
    }

    public override void Dispose()
    {
        _requesting.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var next = await CheckAsync(stoppingToken);
                var wait = next - time.GetUtcNow();
                await runtime.WaitAsync(wait < TimeSpan.Zero ? TimeSpan.Zero : wait > _checkInterval ? _checkInterval : wait, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    // Requests every due certificate one after the other and says when the next one is due.
    private async Task<DateTimeOffset> CheckAsync(CancellationToken cancellationToken)
    {
        var next = time.GetUtcNow() + _checkInterval;
        var settings = await SettingsAsync(cancellationToken);
        if (settings.TermsAgreed is null)
        {
            return next;
        }
        foreach (var route in await proxy.AcmeRoutesAsync(cancellationToken))
        {
            if (!route.Enabled || runtime.IsRunning(route.Name))
            {
                continue;
            }
            var due = await DueAsync(route, cancellationToken);
            if (due > time.GetUtcNow())
            {
                next = due < next ? due : next;
                continue;
            }
            var done = new TaskCompletionSource();
            try
            {
                await StartAsync(0, route, cancellationToken, done);
                await done.Task.WaitAsync(cancellationToken);
            }
            catch (PithyxException)
            {
                // Already running from a manual request.
            }
        }
        return next;
    }

    private async Task<DateTimeOffset> DueAsync(AcmeRoute route, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<(string Domains, string AttemptedAt, long Failures)?>(
            "SELECT domains, attempted_at, failures FROM proxy_acme WHERE route = @route", new { route = route.Name });
        var (notBefore, notAfter) = proxy.AcmeDates(route.Name);
        var ordered = row is { } known ? JsonSerializer.Deserialize<List<string>>(known.Domains, _json) : notAfter is null ? null : route.Domains;
        return AcmeRules.NextAttempt(
            route.Domains,
            ordered,
            notBefore,
            notAfter,
            row is { } attempt ? DateTimeOffset.Parse(attempt.AttemptedAt, CultureInfo.InvariantCulture) : null,
            (int)(row?.Failures ?? 0));
    }

    private async Task<Job> StartAsync(long userId, AcmeRoute route, CancellationToken cancellationToken, TaskCompletionSource? done = null)
    {
        if (!runtime.Start(route.Name))
        {
            throw new PithyxException(ErrorCodes.ProxyAcmeRunning);
        }
        try
        {
            return await jobs.StartAsync(userId, Job, new { route = route.Name, domains = route.Domains }, async context =>
            {
                try
                {
                    await RequestAsync(route, context.Cancellation);
                }
                finally
                {
                    runtime.Finish(route.Name);
                    done?.TrySetResult();
                }
            }, cancellationToken);
        }
        catch
        {
            runtime.Finish(route.Name);
            throw;
        }
    }

    private async Task RequestAsync(AcmeRoute route, CancellationToken cancellationToken)
    {
        await _requesting.WaitAsync(cancellationToken);
        try
        {
            var settings = await SettingsAsync(cancellationToken);
            if (settings.TermsAgreed is null)
            {
                throw new PithyxException(ErrorCodes.ProxyAcmeTermsRequired);
            }
            try
            {
                var (certificate, key) = await OrderAsync(settings, route.Domains, cancellationToken);
                WriteSecret(proxy.AcmePath(route.Name, ".key"), key);
                WriteSecret(proxy.AcmePath(route.Name, ".pem"), certificate);
                await RecordAsync(route, null, cancellationToken);
                await proxy.LoadAsync(cancellationToken);
                LogIssued(route.Name, route.Domains);
            }
            catch (Exception exception) when (Failure(exception, route.Domains) is { } failure)
            {
                var renewal = proxy.AcmeDates(route.Name).NotAfter is not null;
                var failures = await RecordAsync(route, failure, cancellationToken);
                LogFailed(route.Name, failure.Code, failure.Parameters.GetValueOrDefault("detail") as string ?? "");
                if (renewal && failures == 1)
                {
                    await notifications.CreateForAdministratorsAsync(new NotificationDraft(RenewalFailedCode, new Dictionary<string, object?>
                    {
                        ["name"] = route.Domains[0],
                        ["code"] = failure.Code,
                    }, NotificationTone.Warning), cancellationToken);
                }
                throw failure;
            }
        }
        finally
        {
            _requesting.Release();
        }
    }

    // The ACME flow (RFC 8555): the account, an order for the names, an HTTP-01 check per name, then the certificate
    // for a new P-256 key.
    private async Task<(string Certificate, string Key)> OrderAsync(AcmeSettings settings, IReadOnlyList<string> domains, CancellationToken cancellationToken)
    {
        var directory = new Uri(settings.Directory);
        var context = new AcmeContext(directory, AccountKey(settings.Directory), new AcmeHttpClient(directory, _http));
        await context.NewAccount(settings.Email is null ? [] : [$"mailto:{settings.Email}"], termsOfServiceAgreed: true);
        var order = await context.NewOrder([.. domains]);
        foreach (var authorization in await order.Authorizations())
        {
            var resource = await authorization.Resource();
            if (resource.Status == AuthorizationStatus.Valid)
            {
                continue;
            }
            var challenge = await authorization.Http() ?? throw new PithyxException(ErrorCodes.ProxyAcmeFailed, new Dictionary<string, object?>
            {
                ["name"] = resource.Identifier?.Value ?? domains[0],
                ["detail"] = "The authority offers no HTTP-01 check.",
            });
            runtime.Offer(challenge.Token, challenge.KeyAuthz);
            try
            {
                await challenge.Validate();
                for (var poll = 0; poll < MaxValidationPolls && resource.Status == AuthorizationStatus.Pending; poll++)
                {
                    await Task.Delay(_validationPoll, time, cancellationToken);
                    resource = await authorization.Resource();
                }
            }
            finally
            {
                runtime.Withdraw(challenge.Token);
            }
            if (resource.Status != AuthorizationStatus.Valid)
            {
                var error = resource.Challenges?.FirstOrDefault(entry => entry.Type == ChallengeTypes.Http01)?.Error;
                var (code, parameters) = AcmeRules.Problem(error?.Type, error?.Detail ?? $"The check ended as {resource.Status}.", resource.Identifier?.Value);
                throw new PithyxException(code, parameters);
            }
        }
        var key = KeyFactory.NewKey(KeyAlgorithm.ES256);
        var chain = await order.Generate(new CsrInfo { CommonName = domains[0] }, key);
        return (chain.ToPem(), key.ToPem());
    }

    // One account key per authority, made at the first request and kept with the box's other TLS secrets.
    private IKey AccountKey(string directory)
    {
        var name = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(directory)))[..16];
        var path = Path.Combine(tlsOptions.Directory, $"acme-account-{name}.key");
        if (File.Exists(path))
        {
            return KeyFactory.FromPem(File.ReadAllText(path));
        }
        var key = KeyFactory.NewKey(KeyAlgorithm.ES256);
        System.IO.Directory.CreateDirectory(tlsOptions.Directory);
        WriteSecret(path, key.ToPem());
        return key;
    }

    // The error a failed request ends with, or null for one that is no ACME failure (cancellation).
    private static PithyxException? Failure(Exception exception, IReadOnlyList<string> domains)
    {
        switch (exception)
        {
            case PithyxException pithyx when pithyx.Code.StartsWith("proxy.acme.", StringComparison.Ordinal):
                return pithyx;
            case AcmeRequestException request:
                var (code, parameters) = AcmeRules.Problem(request.Error?.Type, request.Error?.Detail ?? request.Message, request.Error?.Identifier?.Value ?? domains[0]);
                return new PithyxException(code, parameters, request);
            case HttpRequestException or TaskCanceledException { InnerException: TimeoutException }:
                return new PithyxException(ErrorCodes.ProxyAcmeUnreachable, new Dictionary<string, object?> { ["detail"] = exception.Message }, exception);
            case AcmeException acme:
                return new PithyxException(ErrorCodes.ProxyAcmeFailed, new Dictionary<string, object?> { ["name"] = domains[0], ["detail"] = acme.Message }, acme);
            default:
                return null;
        }
    }

    // Keeps how the last request ended; returns the failures in a row.
    private async Task<long> RecordAsync(AcmeRoute route, PithyxException? failure, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO proxy_acme (route, domains, status, not_after, attempted_at, failures, error_code, error_params)
            VALUES (@route, @domains, @status, @notAfter, @now, @failures, @code, @params)
            ON CONFLICT (route) DO UPDATE SET domains = excluded.domains, status = excluded.status, not_after = excluded.not_after,
                attempted_at = excluded.attempted_at, failures = CASE WHEN excluded.failures = 0 THEN 0 ELSE proxy_acme.failures + 1 END,
                error_code = excluded.error_code, error_params = excluded.error_params
            RETURNING failures
            """,
            new
            {
                route = route.Name,
                domains = JsonSerializer.Serialize(route.Domains, _json),
                status = failure is null ? "valid" : "failed",
                notAfter = proxy.AcmeDates(route.Name).NotAfter?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                now = time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                failures = failure is null ? 0 : 1,
                code = failure?.Code,
                @params = failure is null ? null : JsonSerializer.Serialize(failure.Parameters, _json),
            });
    }

    private async Task<AcmeSettings> SettingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var json = await connection.ExecuteScalarAsync<string?>("SELECT value FROM settings WHERE key = @key", new { key = SettingsKey });
        return json is null ? AcmeSettings.Default : JsonSerializer.Deserialize<AcmeSettings>(json, _json) ?? AcmeSettings.Default;
    }

    // The authority's terms of service from its directory, cached for an hour; null when it cannot be read.
    private async Task<string?> TermsAsync(string directory, bool fresh, CancellationToken cancellationToken)
    {
        if (!fresh && _terms is { } cached && cached.Directory == directory && time.GetUtcNow() - cached.At < _termsLifetime)
        {
            return cached.Terms;
        }
        string? terms = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.GetAsync(new Uri(directory), timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                terms = document.RootElement.TryGetProperty("meta", out var meta) && meta.TryGetProperty("termsOfService", out var link) ? link.GetString() : null;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogDirectoryUnreadable(directory, exception.Message);
        }
        _terms = (directory, terms, time.GetUtcNow());
        return terms;
    }

    // Written next to the target and renamed over it, readable by root only.
    private static void WriteSecret(string path, string content)
    {
        var temporary = path + ".new";
        File.WriteAllText(temporary, content);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(temporary, path, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The ACME authority issued the certificate of {Route} for {Domains}")]
    private partial void LogIssued(string route, IReadOnlyList<string> domains);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The ACME authority issued no certificate for {Route}: {Code} {Detail}")]
    private partial void LogFailed(string route, string code, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The ACME directory {Directory} could not be read: {Reason}")]
    private partial void LogDirectoryUnreadable(string directory, string reason);
}
