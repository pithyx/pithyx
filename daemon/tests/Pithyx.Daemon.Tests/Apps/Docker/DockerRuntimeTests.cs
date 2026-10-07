using System.Text;
using System.Text.Json;
using Pithyx.Daemon.Apps.Docker;
using Pithyx.Daemon.Tests.Apps.Packages;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Apps.Docker;

/// <summary>
/// The pure parts of the Docker runtime (A14): the Engine's answers as the A1 spike recorded them, the images lock, the
/// files the daemon writes for Docker, and where the proxy finds an endpoint.
/// </summary>
public sealed class DockerRuntimeTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(RepositoryFiles.Root, "daemon", "tests", "fixtures", "docker", name));

    [Fact]
    public void ContainersCarryTheirAppServiceStateAndAddresses()
    {
        var containers = DockerResponses.Containers(Fixture("containers.json"));

        var web = Assert.Single(containers, container => container.Name == "pithyx-org_pithyx_spike-web-1");
        Assert.Equal("org.pithyx.spike", web.App);
        Assert.Equal("web", web.Service);
        Assert.Equal("running", web.State);
        Assert.Equal("172.29.0.2", web.Addresses["pithyx-org_pithyx_spike_app"]);
        var oom = Assert.Single(containers, container => container.Name == "pithyx-spike-oom");
        Assert.Equal("exited", oom.State);
        Assert.Empty(oom.Addresses);
    }

    [Fact]
    public void EventsOfAppContainersAreReadAndEverythingElseIsSkipped()
    {
        var events = Fixture("events.jsonl").Split('\n').Select(DockerResponses.Event).OfType<DockerEvent>().ToList();

        Assert.All(events, item => Assert.Equal("org.pithyx.spike", item.App));
        Assert.Contains(events, item => item is { Action: "oom" });
        Assert.Contains(events, item => item is { Action: "die", ExitCode: 137 });
        var health = Assert.Single(events, item => item.Action == "health_status");
        Assert.Equal("healthy", health.Detail);
        Assert.Contains(events, item => item.Action == "exec_create" && item.Detail!.StartsWith("wget", StringComparison.Ordinal));
        Assert.True(events[0].Time > new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero));
        Assert.Null(DockerResponses.Event("{\"Type\":\"image\",\"Action\":\"pull\",\"Actor\":{\"ID\":\"x\",\"Attributes\":{\"com.pithyx.app\":\"a.b.c\"}}}"));
        Assert.Null(DockerResponses.Event("not json"));
    }

    [Fact]
    public void LoadedImagesAndStreamErrorsAreFound()
    {
        Assert.Equal(["sha256:" + new string('a', 64)], DockerResponses.LoadedImages("{\"stream\":\"Loaded image ID: sha256:" + new string('a', 64) + "\\n\"}\n"));
        Assert.Throws<DockerEngineException>(() => DockerResponses.LoadedImages("{\"errorDetail\":{\"message\":\"bad\"},\"error\":\"bad\"}\n"));
        Assert.Equal("manifest unknown", DockerResponses.StreamError("{\"status\":\"Pulling\"}\n{\"error\":\"manifest unknown\"}\n"));
        Assert.Null(DockerResponses.StreamError("{\"status\":\"Digest: sha256:x\"}\n"));
        Assert.Equal("No such image", DockerResponses.Message("{\"message\":\"No such image\"}"));
    }

    [Fact]
    public void ALockIsReadOnlyInItsExactShape()
    {
        var parsed = ImagesLock.Parse(Encoding.UTF8.GetBytes(TarBuilder.BackendLock()))!;
        Assert.Equal("sha256:" + new string('c', 64), parsed.Images[TarBuilder.BackendImage]["amd64"].Config);

        string[] invalid =
        [
            "{}",
            "{ \"lockVersion\": 1, \"images\": {} }",
            "{ \"lockVersion\": 1.0, \"images\": { \"a@sha256:" + new string('1', 64) + "\": { \"amd64\": { \"manifest\": \"sha256:" + new string('1', 64) + "\", \"config\": \"sha256:" + new string('1', 64) + "\" } } } }",
            TarBuilder.BackendLock().Replace("\"amd64\"", "\"386\"", StringComparison.Ordinal),
            TarBuilder.BackendLock().Replace("\"config\"", "\"id\"", StringComparison.Ordinal),
            TarBuilder.BackendLock().Replace("sha256:cccc", "sha256:CCCC", StringComparison.Ordinal),
            TarBuilder.BackendLock().Replace("\"lockVersion\": 1,", "\"lockVersion\": 1, \"extra\": true,", StringComparison.Ordinal),
            TarBuilder.BackendLock(TarBuilder.BackendImage.Split('@')[0]),
            "[]",
            "{ not json",
        ];
        Assert.All(invalid, text => Assert.Null(ImagesLock.Parse(Encoding.UTF8.GetBytes(text))));
    }

    [Theory]
    [InlineData("ghcr.io/example/web:1.0.0@sha256:aa", "ghcr.io/example/web")]
    [InlineData("postgres:17@sha256:aa", "postgres")]
    [InlineData("localhost:5000/team/web@sha256:aa", "localhost:5000/team/web")]
    [InlineData("localhost:5000/team/web:2@sha256:aa", "localhost:5000/team/web")]
    public void APullGoesToThePlatformManifestByDigest(string reference, string name) =>
        Assert.Equal($"{name}@sha256:{new string('b', 64)}", ImagesLock.PullReference(reference, new LockedImage("sha256:" + new string('b', 64), "sha256:" + new string('c', 64))));

    [Fact]
    public void DockerIsConfiguredAsD309Chose()
    {
        using var config = JsonDocument.Parse(DockerHost.DaemonJson("/tank/.pithyx/docker"));
        var root = config.RootElement;
        Assert.Equal("/tank/.pithyx/docker", root.GetProperty("data-root").GetString());
        Assert.Equal("overlay2", root.GetProperty("storage-driver").GetString());
        Assert.Equal("pithyx-docker", root.GetProperty("userns-remap").GetString());
        Assert.Equal("none", root.GetProperty("bridge").GetString());
        Assert.Equal("172.29.0.0/16", root.GetProperty("default-address-pools")[0].GetProperty("base").GetString());
        Assert.Equal(24, root.GetProperty("default-address-pools")[0].GetProperty("size").GetInt32());
        Assert.Equal("journald", root.GetProperty("log-driver").GetString());
        Assert.Equal("com.pithyx.app,com.pithyx.service", root.GetProperty("log-opts").GetProperty("labels").GetString());
        Assert.True(root.GetProperty("no-new-privileges").GetBoolean());
        Assert.Contains("RequiresMountsFor=/tank/.pithyx/docker", DockerHost.DropIn("/tank/.pithyx/docker"), StringComparison.Ordinal);
        Assert.Contains("iifname \"px-*\" ct state new drop", DockerHost.NftRules, StringComparison.Ordinal);
    }

    [Fact]
    public void SubordinateIdsOfTheRemapUserAreReplacedAndOthersKept()
    {
        var existing = "gabriel:100000:65536\npithyx-docker:1:2\nbackup:165536:65536\n";
        var written = DockerHost.SubIds(existing, DockerHost.SubuidLines);

        Assert.Equal(
            "gabriel:100000:65536\nbackup:165536:65536\npithyx-docker:1000000000:40000\npithyx-docker:40000:10000\npithyx-docker:1000050000:15536\n",
            written);
        Assert.Equal(written, DockerHost.SubIds(written, DockerHost.SubuidLines));
        Assert.Equal(
            "pithyx-docker:1000000000:30000\npithyx-docker:30000:20000\npithyx-docker:1000050000:15536\n",
            DockerHost.SubIds("", DockerHost.SubgidLines));
    }

    [Fact]
    public void EndpointsAreTheRunningServicesAddressOnTheAppNetwork()
    {
        using var manifest = JsonDocument.Parse("""
            { "backend": { "endpoints": { "api": { "service": "web", "port": 8080 }, "metrics": { "service": "web", "port": 9090 }, "db": { "service": "db", "port": 5432 } } } }
            """);
        var project = "pithyx-com_example_chat";
        DockerContainer[] containers =
        [
            new("1", "web", "com.example.chat", "web", "running", new Dictionary<string, string> { [project] = "172.29.3.2", [project + "-internet"] = "172.29.4.2" }),
            new("2", "db", "com.example.chat", "db", "exited", new Dictionary<string, string>()),
        ];

        var endpoints = AppBackendRuntime.Endpoints(manifest.RootElement, project, containers);

        Assert.Equal(new Uri("http://172.29.3.2:8080/"), endpoints["api"]);
        Assert.Equal(new Uri("http://172.29.3.2:9090/"), endpoints["metrics"]);
        Assert.False(endpoints.ContainsKey("db"));
    }

    [Fact]
    public void ComposeRunsOnTheRewrittenFileWithoutAProjectEnvironment()
    {
        var arguments = AppBackendRuntime.Compose("pithyx-com_example_chat", "/tank/.pithyx/apps/com.example.chat/run", "up", "--wait");

        Assert.Equal(
            ["compose", "--project-name", "pithyx-com_example_chat", "--project-directory", "/tank/.pithyx/apps/com.example.chat/run",
             "--file", "/tank/.pithyx/apps/com.example.chat/run/compose.json", "--env-file", "/dev/null", "--progress", "plain", "up", "--wait"],
            arguments);
        Assert.Equal("px-7", AppBackendRuntime.BridgeName(40007));
        Assert.Equal("px-i9999", AppBackendRuntime.InternetBridgeName(49999));
    }
}
