using Pithyx.Daemon.SystemSettings;

namespace Pithyx.Daemon.Tests.SystemSettings;

/// <summary>Fixtures are real files from the Debian 13 dev VM, plus a handwritten interfaces file with static addresses.</summary>
public sealed class HostParsersTests
{
    private static string Fixture(params string[] path) => File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "fixtures", .. path]));

    [Fact]
    public void TheOperatingSystemIsThePrettyName() =>
        Assert.Equal("Debian GNU/Linux 13 (trixie)", HostParsers.ParseOsRelease(Fixture("etc", "os-release")));

    [Fact]
    public void WithoutAPrettyNameTheNameAndVersionAreUsed() =>
        Assert.Equal("Pithyx Linux 1", HostParsers.ParseOsRelease("NAME=\"Pithyx Linux\"\nVERSION=\"1\"\n"));

    [Fact]
    public void TheProcessorIsTheFirstModelName() =>
        Assert.Equal("Intel(R) Xeon(R) CPU E5-2650 v2 @ 2.60GHz", HostParsers.ParseCpuModel(Fixture("proc", "cpuinfo")));

    [Fact]
    public void Arm64BoardsNameTheirModel() =>
        Assert.Equal("Raspberry Pi 5 Model B Rev 1.0", HostParsers.ParseCpuModel("processor\t: 0\nBogoMIPS\t: 108.00\n\nModel\t\t: Raspberry Pi 5 Model B Rev 1.0\n"));

    [Fact]
    public void TheDevVmGetsItsAddressByDhcp()
    {
        var methods = HostParsers.ParseInterfacesFile(Fixture("etc", "network", "interfaces"));

        Assert.Equal("dhcp", methods["ens18"]);
        Assert.Equal("loopback", methods["lo"]);
    }

    [Fact]
    public void IPv4WinsOverIPv6AndAnIPv6OnlyInterfaceKeepsItsMethod()
    {
        var methods = HostParsers.ParseInterfacesFile(Fixture("etc", "network", "interfaces-handwritten-static"));

        Assert.Equal("static", methods["enp1s0"]);
        Assert.Equal("dhcp", methods["enp2s0"]);
        Assert.Equal("manual", methods["wlan0"]);
    }

    [Fact]
    public void TheTimeServersSurviveARoundTripThroughTheDropIn()
    {
        var dropIn = HostParsers.RenderTimesyncdDropIn(["0.pool.ntp.org", "192.168.1.1", "2001:db8::1"]);

        Assert.StartsWith("# Managed by pithyx.", dropIn, StringComparison.Ordinal);
        Assert.Equal(["0.pool.ntp.org", "192.168.1.1", "2001:db8::1"], HostParsers.ParseTimesyncdServers(dropIn));
        Assert.Empty(HostParsers.ParseTimesyncdServers(""));
    }

    [Theory]
    [InlineData("/usr/share/zoneinfo/Europe/Berlin", "Europe/Berlin")]
    [InlineData("../usr/share/zoneinfo/America/Argentina/Buenos_Aires", "America/Argentina/Buenos_Aires")]
    [InlineData("/etc/somewhere", null)]
    public void TheTimeZoneIsTheNameAfterZoneinfo(string target, string? zone) => Assert.Equal(zone, HostParsers.TimeZoneOfLink(target));

    [Theory]
    [InlineData("pool.ntp.org", true)]
    [InlineData("ntp1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("not a host!", false)]
    [InlineData("-bad.example", false)]
    [InlineData("", false)]
    public void TimeServersAreHostNamesOrAddresses(string value, bool valid) => Assert.Equal(valid, HostParsers.IsTimeServer(value));

    [Theory]
    [InlineData("0.1.0-dev+2f3f866b", "0.1.0-dev", "2f3f866b")]
    [InlineData("0.1.0", "0.1.0", null)]
    public void TheBuildSplitsVersionAndCommit(string informational, string version, string? commit) =>
        Assert.Equal(new BuildInfo(version, commit), BuildInfo.FromInformationalVersion(informational));
}
