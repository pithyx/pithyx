using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Vyxis.Daemon.Commands;
using Vyxis.Daemon.Tests.Support;

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

    [Fact]
    public async Task MasksSensitiveArgumentsInLogs()
    {
        var logger = new ListLogger<ProcessCommandRunner>();
        var runner = new ProcessCommandRunner(logger);

        await runner.RunAsync(
            new CommandRequest("true", ["user", "hunter2"], SensitiveArgumentIndexes: new HashSet<int> { 1 }),
            TestContext.Current.CancellationToken);

        var message = Assert.Single(logger.Messages);
        Assert.Contains("user", message, StringComparison.Ordinal);
        Assert.Contains("***", message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PassesTraceparentToTheCommand()
    {
        using var activity = new Activity("test").SetIdFormat(ActivityIdFormat.W3C).Start();

        var result = await _runner.RunAsync(
            new CommandRequest("sh", ["-c", "printf %s \"$TRACEPARENT\""]),
            TestContext.Current.CancellationToken);

        Assert.Equal(activity.Id, result.StandardOutput);
    }
}
