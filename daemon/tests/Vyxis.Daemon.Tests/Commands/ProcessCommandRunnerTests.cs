using Microsoft.Extensions.Logging.Abstractions;
using Vyxis.Daemon.Commands;

namespace Vyxis.Daemon.Tests.Commands;

public sealed class ProcessCommandRunnerTests
{
    private readonly ProcessCommandRunner _runner = new(NullLogger<ProcessCommandRunner>.Instance);

    [Fact]
    public async Task AlwaysUsesCUtf8Locale()
    {
        var result = await _runner.RunAsync(
            new CommandRequest("sh", ["-c", "printf %s \"$LC_ALL\""]),
            TestContext.Current.CancellationToken);

        Assert.Equal("C.UTF-8", result.StandardOutput);
    }

    [Fact]
    public async Task PassesExitCodeAndStandardError()
    {
        var result = await _runner.RunAsync(
            new CommandRequest("sh", ["-c", "echo failed >&2; exit 3"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("failed\n", result.StandardError);
    }

    [Fact]
    public async Task KillsCommandAfterTimeout()
    {
        var started = DateTime.UtcNow;

        await Assert.ThrowsAsync<TimeoutException>(
            () => _runner.RunAsync(new CommandRequest("sleep", ["30"], TimeSpan.FromMilliseconds(200)), TestContext.Current.CancellationToken));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }
}
