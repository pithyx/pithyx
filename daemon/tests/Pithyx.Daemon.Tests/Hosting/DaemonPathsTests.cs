using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting.Internal;
using Pithyx.Daemon.Hosting;

namespace Pithyx.Daemon.Tests.Hosting;

public sealed class DaemonPathsTests : IDisposable
{
    private readonly string _etc = Directory.CreateTempSubdirectory("pithyx-etc-").FullName;

    public void Dispose() => Directory.Delete(_etc, recursive: true);

    [Theory]
    [InlineData("Development", DaemonMode.Real)]
    [InlineData("Development", DaemonMode.Simulated)]
    [InlineData("Production", DaemonMode.Simulated)]
    public void ACheckoutDaemonWithoutItsOwnDatabaseIsASecondInstallationOnAPithyxMachine(string environmentName, DaemonMode mode)
    {
        File.WriteAllText(Path.Combine(_etc, "environment"), "dev\n");

        Assert.True(DaemonPaths.IsSecondInstallation(Configuration(), Environment(environmentName), mode, _etc));
    }

    [Fact]
    public void AnInstalledDaemonIsNeverASecondInstallation()
    {
        File.WriteAllText(Path.Combine(_etc, "environment"), "dev\n");

        Assert.False(DaemonPaths.IsSecondInstallation(Configuration(), Environment("Production"), DaemonMode.Real, _etc));
    }

    [Fact]
    public void ADaemonWithItsOwnDatabaseMayRunNextToTheInstallation()
    {
        File.WriteAllText(Path.Combine(_etc, "environment"), "dev\n");
        var configuration = Configuration(("PITHYX_DB", Path.Combine(_etc, "pithyx.db")));

        Assert.False(DaemonPaths.IsSecondInstallation(configuration, Environment("Development"), DaemonMode.Simulated, _etc));
    }

    [Fact]
    public void AMachineWithoutPithyxKeepsTheDevelopmentDirectory() =>
        Assert.False(DaemonPaths.IsSecondInstallation(Configuration(), Environment("Development"), DaemonMode.Simulated, _etc));

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(value => KeyValuePair.Create(value.Key, (string?)value.Value))).Build();

    private static HostingEnvironment Environment(string name) => new HostingEnvironment { EnvironmentName = name };
}
