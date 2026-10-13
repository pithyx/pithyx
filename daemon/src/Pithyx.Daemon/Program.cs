using System.Text.Json;
using System.Text.Json.Serialization;
using Pithyx.Daemon;
using Pithyx.Daemon.Apps;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Backup;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Files;
using Pithyx.Daemon.FileServices;
using Pithyx.Daemon.Health;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Installer;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Layouts;
using Pithyx.Daemon.Logs;
using Pithyx.Daemon.Monitoring;
using Pithyx.Daemon.Network;
using Pithyx.Daemon.Notifications;
using Pithyx.Daemon.Permissions;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Health;
using Pithyx.Daemon.Storage.Snapshots;
using Pithyx.Daemon.Storage.Zfs;
using Pithyx.Daemon.SystemSettings;
using Pithyx.Daemon.Tls;
using Pithyx.Daemon.Updates;
using Pithyx.Daemon.Users;
using Scalar.AspNetCore;
using Serilog;

const int HttpPort = 7484;

// The updater's verbs (update-apply, update-health, esp-sync, recovery) run without the web host (D338).
if (args.Length > 0 && UpdateCommands.Verbs.Contains(args[0]))
{
    return await UpdateCommands.RunAsync(args);
}

Tracing.EnsureActivities();

var builder = WebApplication.CreateSlimBuilder(args);

var mode = builder.Configuration["PITHYX_SIMULATE"] == "1" ? DaemonMode.Simulated : DaemonMode.Real;
var paths = DaemonPaths.Resolve(builder.Configuration, builder.Environment, mode);
if (!BuildTimeDocument.IsGenerating
    && DaemonPaths.IsSecondInstallation(builder.Configuration, builder.Environment, mode, DeploymentEnvironment.DefaultDirectory))
{
    await Console.Error.WriteLineAsync(
        $"This machine has its Pithyx installation already ({DeploymentEnvironment.DefaultDirectory} exists). Refusing to start a second one in {Path.GetDirectoryName(paths.DatabasePath)}.");
    return DaemonExitCode.SecondInstallation;
}

builder.Services.AddPithyxLogs(paths.LogDirectory);
builder.AddPithyxLogging(paths.LogDirectory);
// The port is fixed (CLAUDE.md, Network). Only a development build takes PITHYX_DEV_PORT, so a second daemon (tests,
// another worktree) can run next to the dev unit on the same machine.
var httpPort = builder.Environment.IsDevelopment() && int.TryParse(builder.Configuration["PITHYX_DEV_PORT"], out var devPort)
    ? devPort
    : HttpPort;
var installer = InstallerEnvironment.Resolve(builder.Configuration, mode, Path.GetDirectoryName(paths.DatabasePath) ?? ".");
// HTTPS in real mode (D346); the live system's installer and simulation stay on plain HTTP. A box (production build)
// also listens on 80 and, with HTTPS, on 443; both redirect to 7484 (WebTransport).
var https = mode == DaemonMode.Real && !installer.Active && !BuildTimeDocument.IsGenerating;
var transport = WebTransport.For(httpPort, https, box: mode == DaemonMode.Real && !builder.Environment.IsDevelopment());
builder.WebHost.ConfigureKestrel(transport.Listen);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.RespectNullableAnnotations = true;
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new SqliteConnectionFactory(paths.DatabasePath));
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<MigrationRunner>();
builder.Services.AddPithyxErrors();
builder.Services.AddPithyxAuth();
builder.Services.AddPithyxLayouts();
builder.Services.AddPithyxNotifications();
builder.Services.AddPithyxJobs();
builder.Services.AddPithyxPermissions();
builder.Services.AddPithyxShares();
builder.Services.AddPithyxUsers(mode == DaemonMode.Real
    ? new AccountOptions("/etc/passwd", Apply: true)
    : new AccountOptions(
        Path.Combine(builder.Configuration["PITHYX_FIXTURES"] ?? Path.Combine(builder.Environment.ContentRootPath, "..", "..", "tests", "fixtures"), "etc", "passwd"),
        Apply: false,
        ShadowPath: Path.Combine(builder.Configuration["PITHYX_FIXTURES"] ?? Path.Combine(builder.Environment.ContentRootPath, "..", "..", "tests", "fixtures"), "etc", "shadow"),
        GroupPath: Path.Combine(builder.Configuration["PITHYX_FIXTURES"] ?? Path.Combine(builder.Environment.ContentRootPath, "..", "..", "tests", "fixtures"), "etc", "group")));
builder.Services.AddPithyxFileServices(new FileServiceOptions(
    Apply: mode == DaemonMode.Real,
    TlsDirectory: Path.Combine(Path.GetDirectoryName(paths.DatabasePath) ?? ".", "tls"),
    FtpPort: int.TryParse(builder.Configuration["PITHYX_FTP_PORT"], System.Globalization.CultureInfo.InvariantCulture, out var ftpPort) ? ftpPort : mode == DaemonMode.Real ? 21 : 0));
builder.Services.AddPithyxFiles();
builder.Services.AddPithyxApps(
    mode == DaemonMode.Real
        ? new AppStorageOptions(UseZfs: true, FolderRoot: "/")
        : new AppStorageOptions(UseZfs: false, FolderRoot: Path.Combine(Path.GetDirectoryName(paths.DatabasePath) ?? ".", "pools")),
    new Pithyx.Daemon.Apps.Docker.DockerOptions(Apply: mode == DaemonMode.Real),
    new Pithyx.Daemon.Apps.Catalog.StoreOptions(
        Path.Combine(Path.GetDirectoryName(paths.DatabasePath) ?? ".", "catalogs"),
        Pithyx.Daemon.Apps.Catalog.StoreOptions.ProjectKeys,
        AllowFile: mode == DaemonMode.Simulated,
        Pithyx.Daemon.SystemSettings.BuildInfo.Current.Version));
builder.Services.AddSingleton<TopicRegistry>();
builder.Services.AddSingleton<TopicHub>();
builder.Services.AddPithyxMonitoring();
builder.Services.AddPithyxSystemSettings(mode);
builder.Services.AddPithyxNetwork(mode);
builder.Services.AddPithyxTls(new TlsOptions(Path.Combine(Path.GetDirectoryName(paths.DatabasePath) ?? ".", "tls"), https, ReadHost: mode == DaemonMode.Real));
builder.Services.AddSingleton(services => new DaemonRestart(services.GetRequiredService<IHostApplicationLifetime>(), mode, services.GetRequiredService<TimeProvider>()));
builder.Services.AddPithyxInstaller(installer);
builder.Services.AddPithyxBackup(mode == DaemonMode.Real
    ? new BackupOptions(Apply: true, TlsDirectory: Path.Combine(Path.GetDirectoryName(paths.DatabasePath) ?? ".", "tls"), SshDirectory: "/etc/ssh", ShadowPath: "/etc/shadow", WorkDirectory: "/run/pithyx/backup")
    : new BackupOptions(Apply: false, TlsDirectory: Path.Combine(Path.GetDirectoryName(paths.DatabasePath) ?? ".", "tls"), SshDirectory: null, ShadowPath: null, WorkDirectory: Path.Combine(Path.GetDirectoryName(paths.DatabasePath) ?? ".", "backup-work")));
builder.Services.AddSingleton<DiskInventory>();
builder.Services.AddSingleton(services => services.GetRequiredService<DiskInventory>().Definition());
builder.Services.AddSingleton<PoolInventory>();
builder.Services.AddSingleton<StorageUsage>();
builder.Services.AddSingleton(services => services.GetRequiredService<PoolInventory>().Definition());
builder.Services.AddSingleton<IStorageBackend, ZfsBackend>();
builder.Services.AddSingleton<DiskProbe>();
builder.Services.AddSingleton<StorageFacts>();
builder.Services.AddSingleton<DatasetPlanner>();
builder.Services.AddSingleton<PoolImportScanner>();
builder.Services.AddSingleton<StoragePlanner>();
builder.Services.AddSingleton<CommandPlanStore>();
builder.Services.AddSingleton<CommandPlanRunner>();
builder.Services.AddSingleton<SystemStateService>();
builder.Services.AddPithyxSnapshots();
builder.Services.AddPithyxStorageHealth();
// The build-time OpenAPI run must not read, let alone probe, the build machine's disks.
if (!BuildTimeDocument.IsGenerating)
{
    builder.Services.AddHostedService(services => services.GetRequiredService<SystemStateService>());
    // The installer of the live system only needs the disks; nothing may touch accounts, shares or pools there.
    if (!installer.Active)
    {
        builder.Services.AddPithyxStorageHealthMonitor();
    }
    if (mode == DaemonMode.Real && !installer.Active)
    {
        builder.Services.AddHostedService<JournalReader>();
        builder.Services.AddHostedService<Pithyx.Daemon.Files.Thumbnails.ThumbnailCacheProvisioner>();
        builder.Services.AddHostedService<LinuxAccountSync>();
        builder.Services.AddHostedService(services => services.GetRequiredService<FileServiceSync>());
        builder.Services.AddHostedService(services => services.GetRequiredService<Pithyx.Daemon.Network.Firewall>());
    }
    if (!installer.Active)
    {
        builder.Services.AddHostedService(services => services.GetRequiredService<BoxCertificates>());
        builder.Services.AddHostedService(services => services.GetRequiredService<Pithyx.Daemon.FileServices.Ftp.FtpServer>());
    }
}
builder.Services.AddPithyxOpenApi();
builder.Services.AddDesktopHosting();

// A real daemon always reads /etc/pithyx: nothing but that file may widen the set of disks the guard allows.
var etcDirectory = DeploymentEnvironment.DefaultDirectory;
if (mode == DaemonMode.Simulated)
{
    var fixtures = builder.Configuration["PITHYX_FIXTURES"]
        ?? Path.Combine(builder.Environment.ContentRootPath, "..", "..", "tests", "fixtures");
    var scenario = builder.Configuration["PITHYX_SIMULATE_SCENARIO"] ?? "default";
    var fixtureDirectory = Path.Combine(fixtures, "commands", scenario);
    builder.Services.AddSingleton<ICommandRunner>(provider =>
        FixtureCommandRunner.Load(fixtureDirectory, provider.GetRequiredService<ILogger<FixtureCommandRunner>>()));
    builder.Services.AddSingleton(new SystemFiles(Path.Combine(fixtures, "proc", "mountinfo"), Path.Combine(fixtures, "proc", "swaps"), Path.Combine(fixtures, "proc", "spl", "kstat", "zfs")));
    etcDirectory = builder.Configuration["PITHYX_ETC_DIR"] ?? Path.Combine(fixtures, "etc");
}
else
{
    builder.Services.AddSingleton<ICommandRunner, ProcessCommandRunner>();
    builder.Services.AddSingleton(SystemFiles.Host);
}
builder.Services.AddSingleton(DeploymentEnvironment.Load(etcDirectory));
builder.Services.AddPithyxUpdates(mode, Path.GetDirectoryName(paths.DatabasePath) ?? ".", etcDirectory);

var app = builder.Build();

if (!BuildTimeDocument.IsGenerating)
{
    try
    {
        await app.Services.GetRequiredService<MigrationRunner>().RunAsync(Migration.Embedded(), CancellationToken.None);
        if (mode == DaemonMode.Simulated)
        {
            var shares = Path.Combine(Path.GetDirectoryName(paths.DatabasePath)!, "shares");
            await app.Services.GetRequiredService<ShareService>().SeedSimulationAsync(shares, CancellationToken.None);
        }
    }
    catch (DatabaseNewerThanDaemonException)
    {
        // Logged by the runner. Disposing flushes the log sinks, so the reason reaches the journal and the log file.
        await app.DisposeAsync();
        return DaemonExitCode.DatabaseNewerThanDaemon;
    }
    catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or IOException)
    {
        // Exit so systemd sees the failure instead of a daemon without a database.
        HostLog.DatabaseFailed(app.Logger, exception, paths.DatabasePath);
        await app.DisposeAsync();
        return DaemonExitCode.DatabaseFailed;
    }

    // The certificates exist before the first handshake; the live system's installer has none.
    if (!installer.Active)
    {
        await app.Services.GetRequiredService<BoxCertificates>().LoadAsync(CancellationToken.None);
    }
    if (!https)
    {
        HostLog.PlainHttp(app.Logger, httpPort, mode);
    }
}

app.UseMiddleware<RequestIdMiddleware>();
app.UseMiddleware<TransportRedirect>(transport);
app.UseSerilogRequestLogging(options => options.Logger = app.Services.GetRequiredService<Serilog.ILogger>());
app.UseExceptionHandler();
// Only the API answers bare status codes with problem details. Every other path belongs to the desktop,
// which serves its index.html as the SPA fallback.
app.UseWhen(context => IsApiRequest(context.Request.Path), api => api.UseStatusCodePages());
// App backends behind the proxy, then the frontends as public files with their own sandboxing headers; nothing below
// may answer for /apps/. The proxy takes WebSocket upgrades itself, so it comes before UseWebSockets.
if (!installer.Active)
{
    app.UseMiddleware<Pithyx.Daemon.Apps.Proxy.AppProxy>();
    app.UseMiddleware<AppFrontendHosting>();
}
app.UseMiddleware<CsrfMiddleware>();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30), KeepAliveTimeout = TimeSpan.FromSeconds(30) });
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi(OpenApiSetup.DocumentPath).AllowAnonymous();
if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference("/api/docs", options => options.WithOpenApiRoutePattern(OpenApiSetup.DocumentPath)).AllowAnonymous();
}

// Every API endpoint requires a session unless it opts out with AllowAnonymous.
var api = app.MapGroup("/api").RequireAuthorization();

api.MapGet("/health", () => TypedResults.Ok(new HealthResponse("ok", mode)))
    .WithName("GetHealth")
    .WithTags("System")
    .AllowAnonymous();

api.MapSetupStatusEndpoint();
// The live system serves only the installer and the desktop that shows it; the build-time document has everything.
if (installer.Active || BuildTimeDocument.IsGenerating)
{
    api.MapInstallerEndpoints();
}
if (!installer.Active)
{
    api.MapInstallIdEndpoint(Path.GetDirectoryName(paths.DatabasePath) ?? ".");
    api.MapAuthEndpoints();
    api.MapLayoutEndpoints();
    api.MapJobEndpoints();
    api.MapNotificationEndpoints();
    api.MapShareEndpoints();
    api.MapUserEndpoints();
    api.MapPermissionEndpoints();
    api.MapFileServiceEndpoints();
    api.MapFileEndpoints();
    api.MapStorageEndpoints();
    api.MapSnapshotEndpoints();
    api.MapStorageHealthEndpoints();
    api.MapLogEndpoints();
    api.MapSystemSettingsEndpoints();
    api.MapNetworkEndpoints();
    api.MapTlsEndpoints();
    api.MapUpdateEndpoints();
    api.MapPowerEndpoint();
    api.MapBackupEndpoints();
    api.MapAppEndpoints();
    Pithyx.Daemon.Apps.Catalog.StoreEndpoints.MapStoreEndpoints(api);
    api.MapAppTokenEndpoint();
    api.MapAppApi();
    api.MapAppFileUrl();
    app.MapRealtime();
    app.MapWebDav();
}
app.MapDesktop(paths.DesktopDirectory);

await app.RunAsync();
return app.Services.GetRequiredService<DaemonRestart>().Restarting ? DaemonExitCode.RestartRequested : 0;

static bool IsApiRequest(PathString path) => path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

public partial class Program;
