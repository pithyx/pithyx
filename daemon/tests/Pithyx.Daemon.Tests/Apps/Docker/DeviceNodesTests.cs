using Pithyx.Daemon.Apps.Docker;

namespace Pithyx.Daemon.Tests.Apps.Docker;

/// <summary>What a device right stands for at the start (D387): a class's present nodes with their groups, or its one node.</summary>
public sealed class DeviceNodesTests
{
    /// <summary>A small <c>/dev</c>: two graphics nodes and a stray file, two serial adapters by id, tun, and a link to a disk.</summary>
    private static readonly DeviceFs _fs = new(
        path => path switch
        {
            "/dev/dri/card0" => (44u, false),
            "/dev/dri/renderD128" => (105u, false),
            "/dev/ttyUSB0" or "/dev/ttyACM0" => (20u, false),
            "/dev/net/tun" => (0u, true),
            "/dev/sda" => (6u, false),
            _ => null,
        },
        path => path switch
        {
            "/dev/dri" => ["/dev/dri/by-path", "/dev/dri/card0", "/dev/dri/renderD128", "/dev/dri/notes"],
            "/dev/serial" => ["/dev/serial/by-id", "/dev/serial/by-path"],
            "/dev/serial/by-id" => ["/dev/serial/by-id/usb-zigbee", "/dev/serial/by-id/usb-evil"],
            "/dev/serial/by-path" => ["/dev/serial/by-path/pci-acm"],
            _ => [],
        },
        path => path switch
        {
            "/dev/serial/by-id/usb-zigbee" => "/dev/ttyUSB0",
            "/dev/serial/by-id/usb-evil" => "/dev/sda",
            "/dev/serial/by-path/pci-acm" => "/dev/ttyACM0",
            "/dev/zigbee" => "/dev/ttyUSB0",
            "/dev/disk-link" => "/dev/sda",
            _ => null,
        });

    [Fact]
    public void TheGraphicsClassIsEveryCardAndRenderNode() =>
        Assert.Equal(
            [new DeviceNode("/dev/dri/card0", 44), new DeviceNode("/dev/dri/renderD128", 105)],
            DeviceNodes.Present("/dev/dri", _fs));

    [Fact]
    public void TheSerialClassIsTheAdaptersItsLinksLeadTo() =>
        Assert.Equal(
            [new DeviceNode("/dev/ttyACM0", 20), new DeviceNode("/dev/ttyUSB0", 20)],
            DeviceNodes.Present("/dev/serial", _fs));

    [Fact]
    public void TunNeedsNoGroup() =>
        Assert.Equal([new DeviceNode("/dev/net/tun", null)], DeviceNodes.Present("/dev/net/tun", _fs));

    [Fact]
    public void ASingleNodeFollowsItsLinkButNeverToABlockedNode()
    {
        Assert.Equal([new DeviceNode("/dev/ttyUSB0", 20)], DeviceNodes.Present("/dev/zigbee", _fs));
        Assert.Empty(DeviceNodes.Present("/dev/disk-link", _fs));
        Assert.Empty(DeviceNodes.Present("/dev/ttyUSB7", _fs));
    }

    [Fact]
    public void RequiredAndOptionalDevicesAreToldApart()
    {
        var granted = new[] { "device:/dev/dri", "device:/dev/ttyUSB7", "device:/dev/kvm", "host.network" };
        var optional = new HashSet<string>(["/dev/kvm"], StringComparer.Ordinal);

        Assert.Equal(["/dev/ttyUSB7"], DeviceNodes.MissingRequired(granted, optional, _fs));
        Assert.Equal(["/dev/kvm"], DeviceNodes.MissingOptional(granted, optional, _fs));
    }

    [Fact]
    public void InSimulationEveryDeviceIsThere() =>
        Assert.Equal([new DeviceNode("/dev/dri", null)], DeviceNodes.Present("/dev/dri", DeviceFs.Simulation));
}
