using Vyxis.Daemon.Commands;

namespace Vyxis.Daemon.Tests.Commands;

public sealed class FixtureCommandRunnerTests
{
    private static readonly string _defaultScenario = Path.Combine(AppContext.BaseDirectory, "fixtures", "commands", "default");

    [Fact]
    public async Task ReturnsRecordedOutput()
    {
        var runner = FixtureCommandRunner.Load(_defaultScenario);

        var result = await runner.RunAsync(new CommandRequest("uname", ["-srm"]), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("Linux ", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThrowsForUnrecordedArguments()
    {
        var runner = FixtureCommandRunner.Load(_defaultScenario);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(new CommandRequest("uname", ["-a"]), TestContext.Current.CancellationToken));

        Assert.Contains("uname -a", exception.Message, StringComparison.Ordinal);
    }
}
