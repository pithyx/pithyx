using Pithyx.Daemon.Storage;
using Pithyx.Daemon.Storage.Health;

namespace Pithyx.Daemon.Tests.Storage.Health;

/// <summary>
/// smartctl cannot run against real disks on the dev VM (they are QEMU disks without SMART), so these fixtures are
/// hand-written in the JSON shape of smartmontools 7.4 (<c>daemon/tests/fixtures/smart/handwritten-*.json</c>).
/// </summary>
public sealed class SmartParserTests
{
    internal static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "smart", $"handwritten-{name}.json"));

    private static SmartReading Parse(string name, int exitCode) => SmartParser.Parse(Fixture(name), exitCode);

    [Fact]
    public void AHealthyAtaDiskPassesWithItsCounters()
    {
        var reading = Parse("ata-healthy", 0);

        Assert.Equal((SmartOutcome.Read, "passed"), (reading.Outcome, reading.State));
        Assert.Equal((0L, 0L, 0L), (reading.ReallocatedSectors, reading.PendingSectors, reading.UncorrectableSectors));
        Assert.Equal((34, 21500L), (reading.TemperatureCelsius, reading.PowerOnHours));
        Assert.Equal("passed", reading.LastSelfTest);
        Assert.Null(reading.MediaErrors);
    }

    [Fact]
    public void ADiskThatPredictsItsFailureFails()
    {
        var reading = Parse("ata-failing", 24);

        Assert.Equal("failed", reading.State);
        Assert.Equal((1872L, 16L), (reading.ReallocatedSectors, reading.PendingSectors));
    }

    [Fact]
    public void PendingSectorsAndAFailedSelfTestAreAWarning()
    {
        var reading = Parse("ata-warning", 128);

        Assert.Equal("warning", reading.State);
        Assert.Equal((8L, 8L), (reading.PendingSectors, reading.UncorrectableSectors));
        Assert.Equal("failed", reading.LastSelfTest);
    }

    [Fact]
    public void ARunningSelfTestIsReportedAsRunning()
    {
        Assert.Equal("running", Parse("ata-running", 0).LastSelfTest);
    }

    [Fact]
    public void ASleepingDiskIsNotRead()
    {
        var reading = Parse("ata-standby", 2);

        Assert.Equal(SmartOutcome.Asleep, reading.Outcome);
        Assert.Null(reading.State);
    }

    [Fact]
    public void NvmeReportsMediaErrorsAndCriticalWarnings()
    {
        var healthy = Parse("nvme-healthy", 0);
        var critical = Parse("nvme-critical", 8);

        Assert.Equal(("passed", 0L, 0, "passed"), (healthy.State, healthy.MediaErrors, healthy.CriticalWarning, healthy.LastSelfTest));
        Assert.Equal(41, healthy.TemperatureCelsius);
        Assert.Equal(("failed", 3L, 1), (critical.State, critical.MediaErrors, critical.CriticalWarning));
    }

    [Fact]
    public void AQemuDiskIsUnsupportedBecauseItIsVirtual()
    {
        var reading = Parse("qemu-scsi", 4);

        Assert.Equal((SmartOutcome.Unsupported, "virtual"), (reading.Outcome, reading.Reason));
    }

    [Fact]
    public void ABridgeWithoutSmartIsUnsupported()
    {
        var reading = Parse("usb-no-smart", 4);

        Assert.Equal((SmartOutcome.Unsupported, "not_available"), (reading.Outcome, reading.Reason));
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("not json", 0)]
    [InlineData("{\"smartctl\":{\"exit_status\":2}}", 2)]
    public void OutputThatSaysNothingIsAFailedRead(string output, int exitCode)
    {
        Assert.Equal(SmartOutcome.Failed, SmartParser.Parse(output, exitCode).Outcome);
    }

    [Fact]
    public void ASleepingDiskKeepsItsLastValues()
    {
        var read = DateTimeOffset.Parse("2026-09-01T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var later = read.AddHours(5);
        var first = SmartInventory.Merge("wwn-1", null, Parse("ata-warning", 128), read);

        var asleep = SmartInventory.Merge("wwn-1", first, Parse("ata-standby", 2), later);

        Assert.Equal(first with { Asleep = true, CheckedAt = later }, asleep);
        Assert.Equal(read, asleep.ReadAt);
    }

    [Fact]
    public void ADiskAsleepSinceStartupIsUnknown()
    {
        var report = SmartInventory.Merge("wwn-1", null, Parse("ata-standby", 2), DateTimeOffset.UnixEpoch);

        Assert.Equal(("unknown", "asleep", true), (report.State, report.Reason, report.Asleep));
        Assert.Null(report.ReadAt);
    }

    [Fact]
    public void VirtioDisksAreNotAsked()
    {
        var virtio = new Disk("path-vda", "vda", "/dev/vda", null, null, null, "0x1af4", 1, false, null, false, false, []);

        Assert.True(SmartCommands.IsVirtio(virtio));
        Assert.False(SmartCommands.IsVirtio(virtio with { Name = "sda" }));
    }

    [Fact]
    public void EverySmartctlCommandLeavesSleepingDisksAlone()
    {
        Assert.Equal(["-n", "standby", "-j", "-a", "/dev/sda"], SmartCommands.Read("/dev/sda").Arguments);
        Assert.Equal(["-n", "standby", "-j", "-t", "short", "/dev/sda"], SmartCommands.SelfTest("/dev/sda", "short").Arguments);
        Assert.Equal(["-n", "standby", "-j", "-t", "long", "/dev/sda"], SmartCommands.SelfTest("/dev/sda", "long").Arguments);
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartCommands.SelfTest("/dev/sda", "conveyance"));
    }
}
