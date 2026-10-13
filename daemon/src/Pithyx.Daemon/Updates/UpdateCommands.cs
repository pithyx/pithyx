using Dapper;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Compact;

namespace Pithyx.Daemon.Updates;

/// <summary>
/// The verbs of <c>pithyxd</c> besides the daemon itself (D338), run as root by systemd units and apt's hook, never by
/// the web host: <c>update-apply &lt;request&gt;</c> (the apply, from a copy of the daemon), <c>update-health</c> (after
/// an update's restart), <c>esp-sync [--if-changed]</c> (GRUB on every ESP) and <c>recovery</c> (the console menu of
/// "Pithyx recovery"). They log JSON to stderr, which their units send to the journal.
/// </summary>
public static class UpdateCommands
{
    public const string DatabasePath = "/var/lib/pithyx/pithyx.db";
    private const string EspStamp = "/var/lib/pithyx/update/esp-sync.stamp";

    public static readonly IReadOnlySet<string> Verbs = new HashSet<string>(StringComparer.Ordinal) { "update-apply", "update-health", "esp-sync", "recovery" };

    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        await using var serilog = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.With<Hosting.SecretMaskingEnricher>()
            .WriteTo.Console(new RenderedCompactJsonFormatter(), standardErrorFromLevel: LogEventLevel.Verbose)
            .CreateLogger();
        using var loggers = new SerilogLoggerFactory(serilog);
        ICommandRunner commands = new ProcessCommandRunner(loggers.CreateLogger<ProcessCommandRunner>());
        var system = new UpdateSystem(commands, UpdatePaths.Box, TimeProvider.System, loggers.CreateLogger<UpdateSystem>());
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        };
        return args[0] switch
        {
            "update-apply" when args.Length == 2 => await ApplyAsync(system, args[1], loggers, stop.Token),
            "update-health" => await HealthAsync(system, commands, loggers, stop.Token),
            "esp-sync" => await EspSyncAsync(system, commands, args.Contains("--if-changed"), stop.Token),
            "recovery" => await new RecoveryMenu(system, commands, loggers, Console.In, Console.Out).RunAsync(stop.Token),
            _ => 64,
        };
    }

    private static async Task<int> ApplyAsync(UpdateSystem system, string requestFile, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var request = UpdateSystem.ReadJson<UpdateApplyRequest>(requestFile);
        if (request is null)
        {
            return 66;
        }
        var progress = await new UpdateApplier(system, TimeProvider.System, loggers.CreateLogger<UpdateApplier>()).RunAsync(request, cancellationToken);
        return progress.State == "restarting" ? 0 : 1;
    }

    /// <summary>The SHA-256 hashes of the certificates the daemon may present: its own and an uploaded one.</summary>
    private static HashSet<string> PinnedCertificates(string tlsDirectory)
    {
        var pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new[] { Tls.BoxCertificates.ServerFile, Tls.BoxCertificates.CustomFile })
        {
            try
            {
                using var certificate = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(tlsDirectory, file)));
                pinned.Add(certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256));
            }
            catch (Exception exception) when (exception is IOException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
            {
                // Missing or unreadable: that certificate is not presented.
            }
        }
        return pinned;
    }

    /// <summary>The health check after an update's restart; nothing to do without <c>trial.json</c>.</summary>
    private static async Task<int> HealthAsync(UpdateSystem system, ICommandRunner commands, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (UpdateSystem.ReadJson<UpdateTrial>(system.Paths.TrialFile) is not { } trial)
        {
            return 0;
        }
        // The daemon on loopback presents the box's own or the uploaded certificate (D346); an uploaded one names no
        // 127.0.0.1, so the check pins both files instead of asking for the name.
        var pinned = PinnedCertificates(Path.Combine(Path.GetDirectoryName(DatabasePath) ?? ".", "tls"));
        using var handler = new SocketsHttpHandler
        {
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    errors == System.Net.Security.SslPolicyErrors.None
                    || (certificate is not null && pinned.Contains(certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256))),
            },
        };
        using var http = new HttpClient(handler);
        var health = new UpdateHealth(system, commands, http, TimeProvider.System, loggers.CreateLogger<UpdateHealth>());
        var failed = await health.CheckAsync(trial, MigrationsCurrentAsync, cancellationToken);
        await health.FinishAsync(trial, failed, await HistoryAsync(DatabasePath, cancellationToken), cancellationToken);
        return failed is null ? 0 : 1;
    }

    /// <summary>Whether the database has every migration this version brings, so the new daemon really started on it.</summary>
    private static async Task<bool> MigrationsCurrentAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await new SqliteConnectionFactory(DatabasePath).OpenAsync(cancellationToken);
            var applied = await connection.ExecuteScalarAsync<int?>("SELECT max(version) FROM schema_version");
            return applied == Migration.Embedded().Max(migration => migration.Version);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return false;
        }
    }

    /// <summary>The update history in a database, carried into the system a rollback restores.</summary>
    public static async Task<IReadOnlyList<UpdateHistoryEntry>> HistoryAsync(string databasePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
        {
            return [];
        }
        try
        {
            await using var connection = await new SqliteConnectionFactory(databasePath).OpenAsync(cancellationToken);
            var rows = await connection.QueryAsync<(long Id, string Kind, string Source, string FromVersion, string ToVersion, string? Snapshot, string State, string? ErrorCode, string StartedAt, string? FinishedAt)>(
                "SELECT id, kind, source, from_version, to_version, snapshot, state, error_code, started_at, finished_at FROM system_updates ORDER BY id");
            return [.. rows.Select(row => new UpdateHistoryEntry(row.Id, row.Kind, row.Source, row.FromVersion, row.ToVersion, row.Snapshot, row.State,
                row.ErrorCode, null, DateTimeOffset.Parse(row.StartedAt, System.Globalization.CultureInfo.InvariantCulture),
                row.FinishedAt is null ? null : DateTimeOffset.Parse(row.FinishedAt, System.Globalization.CultureInfo.InvariantCulture)))];
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return [];
        }
    }

    /// <summary>
    /// The ESP sync from apt's hook (<c>/etc/apt/apt.conf.d/90pithyx-esp-sync</c>): with <c>--if-changed</c> only when
    /// the GRUB package changed since the last sync. Does nothing on a machine the installer did not lay out.
    /// </summary>
    private static async Task<int> EspSyncAsync(UpdateSystem system, ICommandRunner commands, bool ifChanged, CancellationToken cancellationToken)
    {
        if (!(await system.LayoutAsync(cancellationToken)).Supported)
        {
            return 0;
        }
        var version = await commands.RunAsync(new CommandRequest("dpkg-query", ["--show", "--showformat=${Version}", "grub-efi-amd64-bin"], TimeSpan.FromSeconds(30)), cancellationToken);
        var stamp = version.ExitCode == 0 ? version.StandardOutput.Trim() : "";
        if (ifChanged && File.Exists(EspStamp) && (await File.ReadAllTextAsync(EspStamp, cancellationToken)).Trim() == stamp)
        {
            return 0;
        }
        await system.SyncEspsAsync(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(EspStamp)!);
        await File.WriteAllTextAsync(EspStamp, stamp + "\n", cancellationToken);
        return 0;
    }
}
