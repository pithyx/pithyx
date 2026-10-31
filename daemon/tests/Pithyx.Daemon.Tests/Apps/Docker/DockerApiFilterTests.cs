using System.Text;
using System.Text.Json;
using Pithyx.Daemon.Apps.Docker;
using Pithyx.Daemon.Logs;

namespace Pithyx.Daemon.Tests.Apps.Docker;

/// <summary>The decisions of the filtering Docker socket (D387): routes, label filters, and what an app may create.</summary>
public sealed class DockerApiFilterTests
{
    private static readonly DockerProxyApp _app = new("com.example.ci", "/tank/.pithyx/apps/com.example.ci", "pithyx-com_example_ci", Internet: false);

    private static readonly Dictionary<string, string> _noQuery = new(StringComparer.Ordinal);

    [Theory]
    [InlineData("GET", "/_ping", DockerRouteKind.Pass)]
    [InlineData("HEAD", "/v1.45/_ping", DockerRouteKind.Pass)]
    [InlineData("GET", "/v1.45/version", DockerRouteKind.Pass)]
    [InlineData("GET", "/v1.45/containers/json", DockerRouteKind.Filtered)]
    [InlineData("GET", "/events", DockerRouteKind.Filtered)]
    [InlineData("GET", "/volumes", DockerRouteKind.Filtered)]
    [InlineData("POST", "/v1.45/containers/create", DockerRouteKind.CreateContainer)]
    [InlineData("GET", "/containers/abc/json", DockerRouteKind.Container)]
    [InlineData("GET", "/containers/abc/logs", DockerRouteKind.Container)]
    [InlineData("POST", "/containers/abc/start", DockerRouteKind.Container)]
    [InlineData("POST", "/containers/abc/kill", DockerRouteKind.Container)]
    [InlineData("PUT", "/containers/abc/archive", DockerRouteKind.Container)]
    [InlineData("DELETE", "/containers/abc", DockerRouteKind.Container)]
    [InlineData("POST", "/containers/abc/exec", DockerRouteKind.CreateExec)]
    [InlineData("GET", "/exec/abc/json", DockerRouteKind.Exec)]
    [InlineData("POST", "/volumes/create", DockerRouteKind.CreateVolume)]
    [InlineData("DELETE", "/volumes/cache", DockerRouteKind.Volume)]
    [InlineData("GET", "/images/json", DockerRouteKind.Pass)]
    [InlineData("GET", "/images/ghcr.io/example/app:1/json", DockerRouteKind.Pass)]
    [InlineData("GET", "/info", DockerRouteKind.Deny)]
    [InlineData("POST", "/build", DockerRouteKind.Deny)]
    [InlineData("POST", "/commit", DockerRouteKind.Deny)]
    [InlineData("POST", "/containers/prune", DockerRouteKind.Deny)]
    [InlineData("POST", "/images/load", DockerRouteKind.Deny)]
    [InlineData("POST", "/images/example/push", DockerRouteKind.Deny)]
    [InlineData("DELETE", "/images/example", DockerRouteKind.Deny)]
    [InlineData("POST", "/containers/abc/update", DockerRouteKind.Deny)]
    [InlineData("POST", "/networks/create", DockerRouteKind.Deny)]
    [InlineData("POST", "/swarm/init", DockerRouteKind.Deny)]
    [InlineData("POST", "/plugins/pull", DockerRouteKind.Deny)]
    [InlineData("POST", "/session", DockerRouteKind.Deny)]
    public void RoutesEveryCallOrRefusesIt(string method, string path, DockerRouteKind kind) =>
        Assert.Equal(kind, DockerApiFilter.Route(method, path, _noQuery).Kind);

    [Fact]
    public void AttachAndExecStartKeepTheConnectionBothWays()
    {
        Assert.True(DockerApiFilter.Route("POST", "/containers/abc/attach", _noQuery).Hijack);
        Assert.True(DockerApiFilter.Route("POST", "/exec/abc/start", _noQuery).Hijack);
        Assert.False(DockerApiFilter.Route("POST", "/containers/abc/start", _noQuery).Hijack);
    }

    [Fact]
    public void OnlyPullsByNameAreImages()
    {
        Assert.Equal(DockerRouteKind.PullImage, DockerApiFilter.Route("POST", "/images/create", new Dictionary<string, string> { ["fromImage"] = "busybox" }).Kind);
        Assert.Equal(DockerRouteKind.Deny, DockerApiFilter.Route("POST", "/images/create", new Dictionary<string, string> { ["fromSrc"] = "-" }).Kind);
    }

    [Theory]
    [InlineData(null, """{"label":["com.pithyx.spawned-by=com.example.ci"]}""")]
    [InlineData("""{"status":["running"]}""", """{"status":["running"],"label":["com.pithyx.spawned-by=com.example.ci"]}""")]
    [InlineData("""{"label":["tier=db"]}""", """{"label":["tier=db","com.pithyx.spawned-by=com.example.ci"]}""")]
    [InlineData("""{"label":{"tier=db":true}}""", """{"label":{"tier=db":true,"com.pithyx.spawned-by=com.example.ci":true}}""")]
    public void ListsGetTheAppsLabel(string? filters, string expected) =>
        Assert.Equal(expected, DockerApiFilter.LabelFilter(filters, "com.example.ci"));

    [Fact]
    public void ACreatedContainerJoinsTheAppsNetworkWithTheBoxsLimits()
    {
        var volumes = new List<string>();
        var (body, reason) = DockerApiFilter.CreateContainer(
            """{"Image":"busybox","Cmd":["sleep","1"],"Labels":{"tier":"job"},"HostConfig":{"Binds":["cache:/cache","/tank/.pithyx/apps/com.example.ci/volumes/data:/data:ro"],"Memory":268435456,"Privileged":false,"CapAdd":null,"Devices":[],"PidMode":"","NetworkMode":"default"},"NetworkingConfig":{"EndpointsConfig":{"host":{}}}}""",
            _app,
            volumes);

        Assert.Null(reason);
        Assert.Equal(["cache"], volumes);
        using var document = JsonDocument.Parse(body!);
        var root = document.RootElement;
        var host = root.GetProperty("HostConfig");
        Assert.Equal("pithyx-com_example_ci", host.GetProperty("NetworkMode").GetString());
        Assert.Equal(1024, host.GetProperty("PidsLimit").GetInt32());
        Assert.Equal(500, host.GetProperty("OomScoreAdj").GetInt32());
        Assert.Equal(["no-new-privileges:true"], host.GetProperty("SecurityOpt").EnumerateArray().Select(option => option.GetString()));
        Assert.Equal("journald", host.GetProperty("LogConfig").GetProperty("Type").GetString());
        Assert.Equal("com.pithyx.spawned-by", host.GetProperty("LogConfig").GetProperty("Config").GetProperty("labels").GetString());
        Assert.Equal("com.example.ci", root.GetProperty("Labels").GetProperty("com.pithyx.spawned-by").GetString());
        Assert.Equal("job", root.GetProperty("Labels").GetProperty("tier").GetString());
        Assert.Equal(["pithyx-com_example_ci"], root.GetProperty("NetworkingConfig").GetProperty("EndpointsConfig").EnumerateObject().Select(network => network.Name));
    }

    [Theory]
    [InlineData("""{"Image":"busybox","HostConfig":{"Privileged":true}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"CapAdd":["NET_ADMIN"]}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"Devices":[{"PathOnHost":"/dev/sda"}]}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"DeviceRequests":[{"Count":-1}]}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"PidMode":"host"}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"IpcMode":"host"}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"UTSMode":"host"}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"UsernsMode":"host"}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"CgroupnsMode":"host"}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"NetworkMode":"host"}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"NetworkMode":"container:abc"}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"PortBindings":{"80/tcp":[{"HostPort":"80"}]}}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"Binds":["/etc:/host"]}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"Binds":["/tank/.pithyx/apps/com.example.ci/volumes/../../other:/x"]}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"Binds":["/tank/.pithyx/apps/com.example.other/volumes/data:/x"]}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"Binds":["cache:/cache:rshared"]}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"Mounts":[{"Type":"bind","Source":"/","Target":"/host"}]}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"Mounts":[{"Type":"volume","Source":"cache","Target":"/c","VolumeOptions":{"DriverConfig":{"Name":"local","Options":{"device":"/dev/sda"}}}}]}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"Mounts":[{"Type":"npipe","Source":"x","Target":"/x"}]}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"SecurityOpt":["seccomp=unconfined"]}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"Sysctls":{"net.ipv4.ip_forward":"1"}}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"VolumesFrom":["other"]}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"GroupAdd":["30001"]}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"CgroupParent":"/"}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"Runtime":"nvidia"}}""")]
    [InlineData("""{"Image":"busybox","HostConfig":{"RestartPolicy":{"Name":"always"}}}""")]
    [InlineData("""{"Image":"busybox","Labels":{"com.pithyx.app":"com.example.other"}}""")]
    [InlineData("""{"Image":"busybox","MacAddress":"02:42:ac:11:00:02"}""")]
    [InlineData("""{"HostConfig":{}}""")]
    [InlineData("""not json""")]
    public void WhatReachesTheBoxIsRefused(string body) =>
        Assert.NotNull(DockerApiFilter.CreateContainer(body, _app, []).Reason);

    [Fact]
    public void EmptyValuesOfUnlistedKeysPass()
    {
        var (body, reason) = DockerApiFilter.CreateContainer(
            """{"Image":"busybox","MacAddress":"","HostConfig":{"Privileged":false,"CapAdd":[],"Devices":null,"PortBindings":{},"PublishAllPorts":false,"Dns":[],"OomKillDisable":false,"CpuRealtimeRuntime":0}}""",
            _app,
            []);

        Assert.Null(reason);
        Assert.NotNull(body);
    }

    [Fact]
    public void ExecsAreNeverPrivileged()
    {
        Assert.Null(DockerApiFilter.CreateExecProblem("""{"Cmd":["sh"],"Privileged":false}"""));
        Assert.NotNull(DockerApiFilter.CreateExecProblem("""{"Cmd":["sh"],"Privileged":true}"""));
    }

    [Fact]
    public void VolumesUseTheLocalDriverWithoutOptions()
    {
        var (body, reason) = DockerApiFilter.CreateVolume("""{"Name":"cache"}""", "com.example.ci");
        Assert.Null(reason);
        Assert.Contains("\"com.pithyx.spawned-by\":\"com.example.ci\"", body, StringComparison.Ordinal);
        Assert.NotNull(DockerApiFilter.CreateVolume("""{"Name":"x","DriverOpts":{"type":"nfs","device":":/export"}}""", "com.example.ci").Reason);
        Assert.NotNull(DockerApiFilter.CreateVolume("""{"Name":"x","Driver":"rclone"}""", "com.example.ci").Reason);
    }

    [Fact]
    public void OnlyObjectsWithTheAppsLabelAreItsOwn()
    {
        using var container = JsonDocument.Parse("""{"Id":"a","Config":{"Labels":{"com.pithyx.spawned-by":"com.example.ci"}}}""");
        using var other = JsonDocument.Parse("""{"Id":"b","Config":{"Labels":{"com.pithyx.app":"com.example.ci"}}}""");
        using var volume = JsonDocument.Parse("""{"Name":"cache","Labels":{"com.pithyx.spawned-by":"com.example.ci"}}""");
        Assert.True(DockerApiFilter.Owns(container.RootElement, "com.example.ci"));
        Assert.False(DockerApiFilter.Owns(other.RootElement, "com.example.ci"));
        Assert.False(DockerApiFilter.Owns(container.RootElement, "com.example.other"));
        Assert.True(DockerApiFilter.Owns(volume.RootElement, "com.example.ci"));
    }

    [Fact]
    public void OnlyTheAppsUidAndRootMayConnect()
    {
        var plain = new DockerProxySocket(_app, 40001, Root: false, Raw: false);
        var root = plain with { Root = true };
        Assert.True(DockerSocketProxy.MayConnect(plain, 40001));
        Assert.True(DockerSocketProxy.MayConnect(plain, 0));
        Assert.False(DockerSocketProxy.MayConnect(plain, 40002));
        Assert.False(DockerSocketProxy.MayConnect(plain, DockerHost.RemapBase));
        Assert.True(DockerSocketProxy.MayConnect(root, DockerHost.RemapBase));
        Assert.False(DockerSocketProxy.MayConnect(root, 1000));
    }

    [Fact]
    public void TheForwardedHeadClosesTheConnectionUnlessItIsHijacked()
    {
        var head = HttpHead.Parse(
            Encoding.ASCII.GetBytes("POST /v1.45/containers/x/attach?stream=1 HTTP/1.1\r\nHost: docker\r\nConnection: Upgrade\r\nUpgrade: tcp\r\nContent-Length: 0"),
            [])!;
        Assert.Equal("/v1.45/containers/x/attach", head.Path);
        Assert.Equal("1", head.Query["stream"]);

        var hijacked = Encoding.ASCII.GetString(head.Forward("/v1.45/containers/full/attach?stream=1", 0, hijack: true));
        Assert.Contains("Upgrade: tcp\r\n", hijacked, StringComparison.Ordinal);
        Assert.Contains("Connection: Upgrade\r\n", hijacked, StringComparison.Ordinal);

        var plain = Encoding.ASCII.GetString(head.Forward("/v1.45/containers/full/json", 0, hijack: false));
        Assert.StartsWith("POST /v1.45/containers/full/json HTTP/1.1\r\n", plain, StringComparison.Ordinal);
        Assert.Contains("Connection: close\r\n", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("Upgrade", plain, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET /_ping")]
    [InlineData("get /_ping HTTP/1.1")]
    [InlineData("GET http://evil/ HTTP/1.1")]
    public void AMalformedHeadIsRefused(string start) =>
        Assert.Null(HttpHead.Parse(Encoding.ASCII.GetBytes(start + "\r\nHost: docker"), []));

    [Fact]
    public void ASpawnedContainersOutputLandsInTheAppsLog()
    {
        var line = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["__REALTIME_TIMESTAMP"] = "1791249140861610",
            ["_SYSTEMD_UNIT"] = "docker.service",
            ["MESSAGE"] = "job done",
            ["PRIORITY"] = "6",
            ["COM_PITHYX_SPAWNED_BY"] = "com.example.ci",
            ["CONTAINER_NAME"] = "ci-job-1",
        });

        var entry = JournalParser.Parse(line);

        Assert.NotNull(entry);
        Assert.Equal("app/com.example.ci", entry!.Source);
    }
}
