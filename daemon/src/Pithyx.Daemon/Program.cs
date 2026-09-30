using System.Text.Json;
using System.Text.Json.Serialization;
using Pithyx.Daemon;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Files;
using Pithyx.Daemon.Health;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Layouts;
using Pithyx.Daemon.Monitoring;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Shares;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Health;
using Pithyx.Daemon.Storage.Snapshots;
using Pithyx.Daemon.Storage.Zfs;
using Scalar.AspNetCore;
using Serilog;

const int HttpPort = 7484;

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

builder.AddPithyxLogging(paths.LogDirectory);
// The port is fixed (CLAUDE.md, Network). Only a development build takes PITHYX_DEV_PORT, so a second daemon (tests,
// another worktree) can run next to the dev unit on the same machine.
var httpPort = builder.Environment.IsDevelopment() && int.TryParse(builder.Configuration["PITHYX_DEV_PORT"], out var devPort)
    ? devPort
    : HttpPort;
builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(httpPort));
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
builder.Services.AddPithyxJobs();
builder.Services.AddPithyxShares();
builder.Services.AddPithyxFiles();
builder.Services.AddSingleton<TopicRegistry>();
builder.Services.AddSingleton<TopicHub>();
builder.Services.AddPithyxMonitoring();
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
    builder.Services.AddPithyxStorageHealthMonitor();
    if (mode == DaemonMode.Real)
    {
        builder.Services.AddHostedService<Pithyx.Daemon.Files.Thumbnails.ThumbnailCacheProvisioner>();
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
    builder.Services.AddSingleton<ICommandRunner>(FixtureCommandRunner.Load(Path.Combine(fixtures, "commands", scenario)));
    builder.Services.AddSingleton(new SystemFiles(Path.Combine(fixtures, "proc", "mountinfo"), Path.Combine(fixtures, "proc", "swaps"), Path.Combine(fixtures, "proc", "spl", "kstat", "zfs")));
    etcDirectory = builder.Configuration["PITHYX_ETC_DIR"] ?? Path.Combine(fixtures, "etc");
}
else
{
    builder.Services.AddSingleton<ICommandRunner, ProcessCommandRunner>();
    builder.Services.AddSingleton(SystemFiles.Host);
}
builder.Services.AddSingleton(DeploymentEnvironment.Load(etcDirectory));

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

    // Plain HTTP is the development transport. HTTPS on 443 arrives with the TLS work in M5.
    HostLog.PlainHttp(app.Logger, HttpPort, mode);
}

app.UseMiddleware<RequestIdMiddleware>();
app.UseSerilogRequestLogging(options => options.Logger = app.Services.GetRequiredService<Serilog.ILogger>());
app.UseExceptionHandler();
// Only the API answers bare status codes with problem details. Every other path belongs to the desktop,
// which serves its index.html as the SPA fallback.
app.UseWhen(context => IsApiRequest(context.Request.Path), api => api.UseStatusCodePages());
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

api.MapAuthEndpoints();
api.MapLayoutEndpoints();
api.MapJobEndpoints();
api.MapShareEndpoints();
api.MapFileEndpoints();
api.MapStorageEndpoints();
api.MapSnapshotEndpoints();
api.MapStorageHealthEndpoints();
app.MapRealtime();
app.MapDesktop(paths.DesktopDirectory);

await app.RunAsync();
return 0;

static bool IsApiRequest(PathString path) => path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

public partial class Program;
