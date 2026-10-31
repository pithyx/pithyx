using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Tests.Support;
using Pithyx.Daemon.Ups;

namespace Pithyx.Daemon.Tests.Ups;

/// <summary>Reading a NUT server's answers and deciding when the box shuts down (D377).</summary>
public sealed class UpsRulesTests
{
    private static readonly UpsSettings _settings = UpsSettings.Default with { Mode = UpsMode.Network, Host = "nut.lan" };

    private static string[] Fixture(string name) =>
        File.ReadAllLines(Path.Combine(RepositoryFiles.Root, "daemon", "tests", "fixtures", "nut", name));

    [Fact]
    public void AUpsOnLinePowerReportsItsBatteryLoadAndModel()
    {
        var reading = NutProtocol.Read(NutProtocol.ParseVars(Fixture("list-var-online.txt")));

        Assert.Equal(new UpsReading(false, false, false, 100, 1800, 23, "Example Back-UPS 900"), reading);
    }

    [Fact]
    public void AUpsOnBatteryRoundsItsValuesAndUnescapesQuotes()
    {
        var reading = NutProtocol.Read(NutProtocol.ParseVars(Fixture("list-var-on-battery.txt")));

        Assert.Equal(new UpsReading(true, false, false, 41, 612, 38, "Example \"Power\" Rack 1500"), reading);
    }

    [Theory]
    [InlineData("ERR UNKNOWN-UPS", "unknown_ups")]
    [InlineData("ERR DRIVER-NOT-CONNECTED", "driver_not_connected")]
    [InlineData("ERR DATA-STALE", "data_stale")]
    [InlineData("ERR ACCESS-DENIED", "access_denied")]
    [InlineData("ERR INVALID-PASSWORD", "access_denied")]
    [InlineData("ERR UNKNOWN-COMMAND", "protocol")]
    [InlineData("OK", null)]
    public void ErrorsBecomeTheProblemTheSettingsShow(string line, string? problem) =>
        Assert.Equal(problem, NutProtocol.ProblemOf(line));

    [Theory]
    [InlineData("OL", 100, 1800, null)]
    [InlineData("OB", 50, 1200, null)]
    [InlineData("OB", 20, 1200, "charge")]
    [InlineData("OB", 50, 300, "runtime")]
    [InlineData("OB LB", 80, 1200, "low_battery")]
    [InlineData("OL FSD", 100, 1800, "forced")]
    [InlineData("OL LB", 10, 60, null)]
    public void TheBoxShutsDownOnBatteryAtTheLimitsOrWhenTheServerForcesIt(string status, int charge, int runtime, string? reason)
    {
        var reading = NutProtocol.Read(new Dictionary<string, string>
        {
            ["ups.status"] = status,
            ["battery.charge"] = charge.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["battery.runtime"] = runtime.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

        Assert.Equal(reason, UpsRules.ShutdownReason(reading, _settings));
    }

    [Fact]
    public void AUpsWithoutBatteryValuesShutsDownOnlyWhenItSaysTheBatteryIsLow()
    {
        Assert.Null(UpsRules.ShutdownReason(new UpsReading(true, false, false, null, null, null, null), _settings));
        Assert.Equal("low_battery", UpsRules.ShutdownReason(new UpsReading(true, true, false, null, null, null, null), _settings));
    }

    [Theory]
    [InlineData("network", "", 3493, "ups", "", "", 20, 5, "host")]
    [InlineData("network", "nut lan", 3493, "ups", "", "", 20, 5, "host")]
    [InlineData("network", "nut.lan", 0, "ups", "", "", 20, 5, "port")]
    [InlineData("network", "nut.lan", 3493, "my ups", "", "", 20, 5, "name")]
    [InlineData("network", "nut.lan", 3493, "ups", "mon user", "", 20, 5, "username")]
    [InlineData("network", "nut.lan", 3493, "ups", "mon", "a\"b", 20, 5, "password")]
    [InlineData("usb", "", 3493, "ups", "", "", 4, 5, "batteryLow")]
    [InlineData("usb", "", 3493, "ups", "", "", 20, 61, "runtimeLow")]
    public void BadSettingsNameTheirField(string mode, string host, int port, string name, string username, string password, int batteryLow, int runtimeLow, string field)
    {
        var request = new UpsSettingsRequest(Enum.Parse<UpsMode>(mode, ignoreCase: true), host, port, name, username, password, batteryLow, runtimeLow);

        var error = Assert.Throws<PithyxException>(() => UpsRules.Apply(UpsSettings.Default, request));

        Assert.Equal(ErrorCodes.SystemUpsInvalid, error.Code);
        Assert.Equal(field, error.Parameters["field"]);
    }

    [Fact]
    public void MissingFieldsKeepTheirValuesAndAnEmptyPasswordRemovesIt()
    {
        var saved = UpsRules.Apply(UpsSettings.Default, new UpsSettingsRequest(UpsMode.Network, " fd00::10 ", Name: "rack", Username: "monuser", Password: "secret"));

        Assert.Equal(_settings with { Host = "fd00::10", Name = "rack", Username = "monuser", Password = "secret" }, saved);
        Assert.Equal("secret", UpsRules.Apply(saved, new UpsSettingsRequest(UpsMode.Usb)).Password);
        Assert.Equal("", UpsRules.Apply(saved, new UpsSettingsRequest(UpsMode.Network, Password: "")).Password);
    }
}
