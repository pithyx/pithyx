using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pithyx.Daemon.Apps;
using Pithyx.Daemon.Apps.Docker;
using Pithyx.Daemon.Apps.Packages;
using Pithyx.Daemon.Apps.Proxy;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Logs;
using Pithyx.Daemon.Realtime;
using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Zfs;
using Pithyx.Daemon.Tests.Apps.Packages;
using Pithyx.Daemon.Tests.Storage.Integration;

namespace Pithyx.Daemon.Tests.Apps.Docker;

/// <summary>
/// App backends on Debian's real Docker (A14, D309, D310, D324), only on the dev VM through
/// <c>tools/dev-vm/integration.sh --docker</c> (<c>PITHYX_DOCKER_INTEGRATION=1</c>, as root). The image is imported from
/// the host's busybox and its libraries, saved into the package and loaded from there, so nothing comes from a registry.
/// The test configures Docker with the box's own files on a throwaway pool; the script puts the machine back afterwards.
/// </summary>
[Trait("Category", Category)]
public sealed class DockerIntegrationTests
{
    public const string Category = "DockerIntegration";
    private const string AppId = "com.example.dockerprobe";
    private const long Uid = 49990;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void RequireEnabled() =>
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("PITHYX_DOCKER_INTEGRATION") == "1",
            "Real Docker tests run only on the dev VM through tools/dev-vm/integration.sh --docker (PITHYX_DOCKER_INTEGRATION=1, as root).");

    [Fact]
    public async Task ABackendRunsIsReachableLogsAndARolledBackUpdateBringsTheOldOneBack()
    {
        RequireEnabled();
        await using var vdevs = FileVdevs.Create(1);
        await vdevs.CreateLayoutPoolAsync();
        var pool = vdevs.PoolName;
        // Docker's remapped root must pass through every folder above its data root, as it does through /<pool> on a box;
        // the temporary folders of the run are private.
        for (var folder = new DirectoryInfo(vdevs.AltRoot); folder is not null && folder.FullName != "/dev/shm" && folder.FullName != "/"; folder = folder.Parent)
        {
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(folder.FullName, File.GetUnixFileMode(folder.FullName) | UnixFileMode.OtherExecute);
            }
        }
        var pools = new PoolInventory();
        var disks = new DiskInventory();
        var facts = new StorageFacts(disks, pools, DeploymentEnvironment.Prod, SystemFiles.Host);
        var hub = new TopicHub(new TopicRegistry([disks.Definition(), pools.Definition()]), TimeProvider.System, Options.Create(new JsonOptions()), NullLogger<TopicHub>.Instance);
        using var state = new SystemStateService(vdevs.Commands, disks, pools, new ZfsBackend(vdevs.Commands), new DiskProbe(vdevs.Commands), facts, hub, TimeProvider.System, NullLogger<SystemStateService>.Instance);
        await state.RefreshPoolsAsync(Token);
        var storage = new AppStorage(vdevs.Commands, pools, state, new AppStorageOptions(UseZfs: true, "/"), TimeProvider.System);
        var options = new DockerOptions(Apply: true);
        using var engine = new DockerEngine(options);
        using var host = new DockerHost(vdevs.Commands, engine, options, pools, state, TimeProvider.System, NullLogger<DockerHost>.Instance);
        var backends = new AppBackends();
        var staging = Directory.CreateTempSubdirectory("pithyxci-docker-").FullName;
        // The runtime looks up a catalog's registry token for the app; this app is a sideload without a row.
        var connections = new SqliteConnectionFactory(Path.Combine(staging, "pithyx.db"));
        await new MigrationRunner(connections, TimeProvider.System, NullLogger<MigrationRunner>.Instance).RunAsync(Migration.Embedded(), Token);
        var runtime = new AppBackendRuntime(host, engine, options, vdevs.Commands, storage, backends,
            connections, pools, NullLogger<AppBackendRuntime>.Instance);
        string? imageId = null;
        try
        {
            await host.EnsureRunningAsync(pool, Token);
            Assert.True(await engine.PingAsync(Token));
            using (var config = JsonDocument.Parse(await File.ReadAllTextAsync(options.ConfigPath, Token)))
            {
                Assert.Equal(Path.Combine(vdevs.AltRoot, pool, ".pithyx", "docker"), config.RootElement.GetProperty("data-root").GetString());
            }
            Assert.Contains("pithyx-docker:40000:10000", await File.ReadAllTextAsync(options.SubuidPath, Token), StringComparison.Ordinal);
            Assert.Contains("iifname \"px-*\"", await vdevs.RunAsync("nft", ["list", "chain", "inet", "pithyx", "apps-input"]), StringComparison.Ordinal);

            imageId = await ImportImageAsync(vdevs, staging);
            var archive = Path.Combine(staging, "image.tar");
            await vdevs.RunAsync("docker", ["save", "-o", archive, imageId]);
            var imageBytes = await File.ReadAllBytesAsync(archive, Token);
            await vdevs.RunAsync("docker", ["rmi", imageId]);
            Assert.Null(await engine.ImageIdAsync(imageId, Token));

            // Version 1 writes a file into its volume as the app uid, logs a line and serves the file.
            var one = await InstallAsync(storage, runtime, pool, staging, imageId, imageBytes, "1.0.0", "one", update: false);
            Assert.Equal(imageId, await engine.ImageIdAsync(imageId, Token));
            var root = storage.RootOf(pool, AppId)!;
            var endpoint = backends.Find(AppId, "api") ?? throw new InvalidOperationException("The api endpoint is not reachable.");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            Assert.Equal("one\n", await http.GetStringAsync(new Uri(endpoint, "hello.txt"), Token));
            var owner = (await vdevs.RunAsync("stat", ["-c", "%u:%g", Path.Combine(root, "volumes", "data", "hello.txt")])).Trim();
            Assert.Equal($"{Uid}:{Uid}", owner);
            var container = Assert.Single(await engine.ContainersAsync(AppId, Token));
            Assert.Equal("web", container.Service);
            Assert.StartsWith("172.29.", container.Addresses["pithyx-com_example_dockerprobe"], StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(root, "run", AppBackendRuntime.ComposeFile)));

            // Its output reaches the journal with the app's label, which the Log Center reads as app/<id>.
            var journal = "";
            for (var attempt = 0; attempt < 20 && !journal.Contains("started one", StringComparison.Ordinal); attempt++)
            {
                await Task.Delay(500, Token);
                journal = await vdevs.RunAsync("journalctl", ["--output=json", "--no-pager", "COM_PITHYX_APP=" + AppId]);
            }
            var entry = journal.Split('\n').Select(JournalParser.Parse).OfType<PendingLogEntry>().First(line => line.Message == "started one");
            Assert.Equal("app/" + AppId, entry.Source);
            Assert.Equal(LogLevelName.Info, entry.Level);

            // Version 2 never becomes healthy: the update fails, the dataset returns to its snapshot, and version 1 runs again.
            await runtime.StopAsync(AppId, pool, Token);
            Assert.Null(backends.Find(AppId, "api"));
            Assert.Empty(await engine.ContainersAsync(AppId, Token));
            var failed = await Assert.ThrowsAsync<PithyxException>(() =>
                InstallAsync(storage, runtime, pool, staging, imageId, imageBytes, "2.0.0", "two", update: true, healthy: false));
            Assert.Equal(ErrorCodes.AppBackendStartFailed, failed.Code);
            Assert.Empty(await engine.ContainersAsync(AppId, Token));
            await runtime.StartAsync(pool, one, null, Token);
            Assert.Equal(["1.0.0"], Directory.GetFileSystemEntries(Path.Combine(root, "versions")).Select(Path.GetFileName));
            Assert.Equal("one\n", await http.GetStringAsync(new Uri(backends.Find(AppId, "api")!, "hello.txt"), Token));
            Assert.Equal("", (await vdevs.RunAsync("zfs", ["list", "-H", "-t", "snapshot", "-o", "name", "-r", ZfsPlans.AppDataset(pool, AppId)])).Trim());

            // A container that dies is seen in the Engine's events.
            using var watch = CancellationTokenSource.CreateLinkedTokenSource(Token);
            watch.CancelAfter(TimeSpan.FromSeconds(30));
            var events = engine.EventsAsync(watch.Token).GetAsyncEnumerator(watch.Token);
            var next = events.MoveNextAsync().AsTask();
            await Task.Delay(TimeSpan.FromSeconds(1), Token);
            await vdevs.RunAsync("docker", ["kill", container.Name]);
            DockerEvent? died = null;
            while (died is null && await next)
            {
                died = events.Current is { Action: "die", App: AppId } item ? item : null;
                next = died is null ? events.MoveNextAsync().AsTask() : next;
            }
            await events.DisposeAsync();
            Assert.Equal("web", died!.Service);

            // Version 3 adds a service with the host's network, the tun device and /dev/kvm, whose group exists only on the
            // host (D311): it runs in the host's user namespace as the app uid with that group. Revoked, the same file runs
            // the service on the app's network without the devices.
            await runtime.StopAsync(AppId, pool, Token);
            var three = await InstallAsync(storage, runtime, pool, staging, imageId, imageBytes, "3.0.0", "three", update: true, elevated: true);
            var granted = await ProbeAsync(http, backends);
            Assert.Contains($"uid={Uid}", granted, StringComparison.Ordinal);
            Assert.Contains("0          0 4294967295", granted, StringComparison.Ordinal);
            Assert.Contains("lo", granted, StringComparison.Ordinal);
            Assert.DoesNotContain("No such file", granted, StringComparison.Ordinal);
            var kvmGroup = (await vdevs.RunAsync("stat", ["-c", "%g", "/dev/kvm"])).Trim();
            var groups = granted.Split('\n').Single(line => line.StartsWith("uid=", StringComparison.Ordinal)).Split("groups=")[1].Split(',').Select(group => group.Split('(')[0].Trim());
            Assert.Contains(kvmGroup, groups);
            Assert.Contains("kvm opened", granted, StringComparison.Ordinal);
            await runtime.StopAsync(AppId, pool, Token);
            await runtime.StartAsync(pool, three with { Elevated = new HashSet<string>(StringComparer.Ordinal) }, null, Token);
            var revoked = await ProbeAsync(http, backends);
            Assert.DoesNotContain("0          0 4294967295", revoked, StringComparison.Ordinal);
            Assert.Contains("/dev/kvm", revoked, StringComparison.Ordinal);
            Assert.DoesNotContain("kvm opened", revoked, StringComparison.Ordinal);

            await runtime.StopAsync(AppId, pool, Token);
            await runtime.ForgetAsync(ImagesLock.HostImages(Encoding.UTF8.GetBytes(Lock(imageId))), new HashSet<string>(), lastBackend: true, Token);
            Assert.False(await engine.PingAsync(Token));
            await host.EnsureRunningAsync(pool, Token);
            Assert.Null(await engine.ImageIdAsync(imageId, Token));
            await host.StopAsync(Token);
            Assert.False(await engine.PingAsync(Token));
        }
        finally
        {
            await vdevs.RunAsync("sh", ["-c", "docker compose --project-name pithyx-com_example_dockerprobe down --timeout 1 >/dev/null 2>&1; true"]);
            if (imageId is not null)
            {
                await vdevs.RunAsync("sh", ["-c", $"docker rmi {imageId} >/dev/null 2>&1; true"]);
            }
            await vdevs.RunAsync("systemctl", ["stop", "docker.socket", "docker.service", "containerd.service"]);
            Directory.Delete(staging, recursive: true);
        }
    }

    private static string Lock(string imageId) =>
        $$"""
        { "lockVersion": 1, "images": { "localhost/pithyx-probe/web:1@sha256:{{new string('a', 64)}}": { "{{ImagesLock.HostArchitecture}}": { "manifest": "sha256:{{new string('b', 64)}}", "config": "{{imageId}}" } } } }
        """;

    /// <summary>What the elevated probe of version 3 wrote, once it is there.</summary>
    private static async Task<string> ProbeAsync(HttpClient http, AppBackends backends)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await http.GetStringAsync(new Uri(backends.Find(AppId, "api")!, "probe.txt"), Token);
            }
            catch (HttpRequestException) when (attempt < 40)
            {
                await Task.Delay(250, Token);
            }
        }
    }

    private const string ElevatedRights = "\"host.network\", \"device:/dev/net/tun\", \"device:/dev/kvm\"";

    private static async Task<BackendApp> InstallAsync(
        AppStorage storage, AppBackendRuntime runtime, string pool, string staging, string imageId, byte[] image, string version, string text, bool update, bool healthy = true,
        bool elevated = false)
    {
        var manifest = TarBuilder.Manifest(AppId, version, "", elevated
            ? TarBuilder.BackendManifest.Replace("\"lifecycle\": \"onDemand\"", "\"lifecycle\": \"onDemand\", \"elevated\": [" + ElevatedRights + "]", StringComparison.Ordinal)
            : TarBuilder.BackendManifest);
        var check = healthy ? "hello.txt" : "missing.txt";
        var probe = elevated
            ? $$"""
              probe:
                image: localhost/pithyx-probe/web:1@sha256:{{new string('a', 64)}}
                command: ["/bin/sh", "-c", "rm -f /data/probe.txt; { busybox cat /proc/self/uid_map; busybox id; busybox ls /sys/class/net; busybox ls /dev/net/tun /dev/kvm; ( exec 3<>/dev/kvm ) && echo kvm opened; } > /data/probe.tmp 2>&1; busybox mv /data/probe.tmp /data/probe.txt; exec busybox sleep 3600"]
                network_mode: host
                devices: ['/dev/net/tun', '/dev/kvm']
                volumes:
                  - data:/data

            """
            : "";
        var compose = $$"""
            services:
              web:
                image: localhost/pithyx-probe/web:1@sha256:{{new string('a', 64)}}
                command: ["/bin/sh", "-c", "echo {{text}} > /data/hello.txt && echo started {{text}} && exec busybox httpd -f -p 8080 -h /data"]
                volumes:
                  - data:/data
                healthcheck:
                  test: ["CMD", "busybox", "wget", "-q", "-O", "/dev/null", "http://127.0.0.1:8080/{{check}}"]
                  interval: 1s
                  timeout: 2s
                  retries: 5
            {{probe}}volumes:
              data:
            """;
        var bytes = TarBuilder.MinimalPackage(AppId, version, manifest)
            .File("backend/compose.yaml", compose)
            .File("backend/images.lock.json", Lock(imageId))
            .File($"backend/images/{ImagesLock.HostArchitecture}/{imageId["sha256:".Length..]}.tar", image)
            .End().Gzip();
        var package = await PackageReader.UnpackAsync(new MemoryStream(bytes), Path.Combine(staging, "package-" + version + "-" + Guid.NewGuid().ToString("N")), PackageLimits.Default, Token);
        var (checkedManifest, _) = AppPackages.Check(package);
        var app = new BackendApp(AppId, version, checkedManifest, Uid, false, false, [],
            elevated ? new HashSet<string>(["host.network", "device:/dev/net/tun", "device:/dev/kvm"], StringComparer.Ordinal) : null);
        // As AppService does it: a backend that fails goes before the dataset rolls back.
        await AppVersions.InstallAsync(storage, pool, AppId, package, version, update, async (_, token) =>
        {
            try
            {
                await runtime.StartAsync(pool, app, null, token);
            }
            catch
            {
                await runtime.StopAsync(AppId, pool, CancellationToken.None);
                throw;
            }
        }, Token);
        return app;
    }

    /// <summary>An image of the host's busybox and the libraries it loads, imported into Docker; returns its id.</summary>
    private static async Task<string> ImportImageAsync(FileVdevs vdevs, string staging)
    {
        var busybox = (await vdevs.RunAsync("sh", ["-c", "command -v busybox"])).Trim();
        var libraries = (await vdevs.RunAsync("ldd", [busybox])).Split('\n')
            .Select(line => line.Split("=>").Last().Trim().Split(' ')[0])
            .Where(path => path.StartsWith('/'))
            .ToList();
        var rootfs = Path.Combine(staging, "rootfs.tar");
        await using (var stream = File.Create(rootfs))
        await using (var writer = new TarWriter(stream, TarEntryFormat.Pax))
        {
            await writer.WriteEntryAsync(busybox, "bin/busybox", Token);
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.SymbolicLink, "bin/sh") { LinkName = "busybox" }, Token);
            foreach (var library in libraries)
            {
                var real = new FileInfo(library).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? library;
                await writer.WriteEntryAsync(real, library.TrimStart('/'), Token);
            }
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, "data") { Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute }, Token);
        }
        return (await vdevs.RunAsync("docker", ["import", rootfs])).Trim();
    }
}
