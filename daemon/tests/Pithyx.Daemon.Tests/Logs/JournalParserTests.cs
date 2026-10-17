using Pithyx.Daemon.Logs;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Logs;

public sealed class JournalParserTests
{
    private static string[] Lines(string name) =>
        File.ReadAllLines(Path.Combine(RepositoryFiles.Root, "daemon", "tests", "fixtures", "journal", name));

    [Fact]
    public void KernelLinesAreSystemKernelWithTheirPriority()
    {
        var entries = Lines("kernel.jsonl").Select(JournalParser.Parse).ToList();

        Assert.All(entries, entry => Assert.Equal("system/kernel", entry!.Source));
        Assert.All(entries, entry => Assert.Equal(LogLevelName.Info, entry!.Level));
        Assert.Equal("Future hung task reports are suppressed, see sysctl kernel.hung_task_warnings", entries[2]!.Message);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 19, 57, 7, TimeSpan.Zero).AddTicks(314200), entries[2]!.Time);
        Assert.StartsWith("s=22c85822d57d4884af9c01b16ba7e05e;i=3598;", entries[2]!.JournalCursor, StringComparison.Ordinal);
        Assert.Equal(" </TASK>".TrimEnd(), entries[1]!.Message);
    }

    [Fact]
    public void ErrorPrioritiesAreErrors() =>
        Assert.All(Lines("errors.jsonl").Select(JournalParser.Parse), entry => Assert.Equal(LogLevelName.Error, entry!.Level));

    [Fact]
    public void ServicesAreNamedByTheirUnitWithoutTheSuffix()
    {
        var entry = JournalParser.Parse(Lines("timesyncd.jsonl")[0])!;

        Assert.Equal("system/systemd-timesyncd", entry.Source);
        Assert.Equal("systemd-timesyncd.service", entry.Data!["unit"]);
        Assert.StartsWith("Contacted time server", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMessageStoredAsBytesIsDecodedAndLinesWithoutAMessageAreSkipped()
    {
        var lines = Lines("handwritten-bytes.jsonl");

        var entry = JournalParser.Parse(lines[0])!;
        Assert.Equal("system/smbd", entry.Source);
        Assert.Equal(LogLevelName.Warn, entry.Level);
        Assert.Equal("share\u001b[31m missing", entry.Message);
        Assert.Null(JournalParser.Parse(lines[1]));
        Assert.Null(JournalParser.Parse(lines[2]));
        Assert.Null(JournalParser.Parse(""));
    }

    [Theory]
    [InlineData("0", LogLevelName.Error)]
    [InlineData("3", LogLevelName.Error)]
    [InlineData("4", LogLevelName.Warn)]
    [InlineData("5", LogLevelName.Info)]
    [InlineData("6", LogLevelName.Info)]
    [InlineData("7", LogLevelName.Debug)]
    [InlineData(null, LogLevelName.Info)]
    public void PrioritiesMapToLevels(string? priority, LogLevelName level) => Assert.Equal(level, JournalParser.LevelOf(priority));

    [Fact]
    public void FollowingStartsWithTheBootOrAfterTheCursor()
    {
        var first = JournalParser.FollowArguments(null);
        var next = JournalParser.FollowArguments("s=1;i=2");

        Assert.Equal(["--follow", "--output=json", "--no-pager", "--boot", "_TRANSPORT=kernel", "+"], first.Take(6));
        Assert.Contains("--after-cursor=s=1;i=2", next);
        Assert.DoesNotContain("--boot", next);
        Assert.Contains("_SYSTEMD_UNIT=smbd.service", next);
        Assert.DoesNotContain(next, argument => argument.Contains("pithyx", StringComparison.Ordinal));
    }

    [Fact]
    public void AppContainerLinesGoToTheAppWithStdoutAsInfoAndStderrAsWarn()
    {
        var entries = Lines("docker-app.jsonl").Select(JournalParser.Parse).ToList();

        Assert.All(entries, entry =>
        {
            Assert.Equal("app/org.pithyx.spike", entry!.Source);
            Assert.Equal("web", entry.Data!["service"]);
            Assert.Equal("pithyx-org_pithyx_spike-web-1", entry.Data!["container"]);
            Assert.False(entry.Data.ContainsKey("unit"));
        });
        Assert.All(entries.Where(entry => entry!.Message == "to-stderr"), entry => Assert.Equal(LogLevelName.Warn, entry!.Level));
        Assert.All(entries.Where(entry => entry!.Message == "to-stdout"), entry => Assert.Equal(LogLevelName.Info, entry!.Level));
    }

    [Theory]
    [InlineData("6", "{\"level\":\"debug\",\"msg\":\"x\"}", LogLevelName.Debug)]
    [InlineData("6", "{\"level\":\"ERROR\",\"msg\":\"x\"}", LogLevelName.Error)]
    [InlineData("3", "{\"level\":\"info\"}", LogLevelName.Info)]
    [InlineData("3", "{\"level\":\"warning\"}", LogLevelName.Warn)]
    [InlineData("3", "{\"level\":30}", LogLevelName.Warn)]
    [InlineData("6", "{\"level\":\"verbose\"}", LogLevelName.Info)]
    [InlineData("6", "{not json", LogLevelName.Info)]
    [InlineData("3", "plain", LogLevelName.Warn)]
    public void AJsonLevelWinsOverTheStream(string priority, string message, LogLevelName expected) =>
        Assert.Equal(expected, JournalParser.AppLevelOf(priority, message));

    [Fact]
    public void AJsonLineShowsItsMessageWithTheOtherFieldsAsDataAndItsTraceAsRequest()
    {
        var line = JournalParser.AppLineOf("6", """{"level":"info","message":"Clip saved","file":"a.md","bytes":336,"tags":["x"],"traceparent":"00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"}""");
        Assert.Equal((LogLevelName.Info, "Clip saved", "4bf92f3577b34da6a3ce929d0e0e4736"), (line.Level, line.Message, line.RequestId));
        Assert.Equal(new Dictionary<string, string> { ["file"] = "a.md", ["bytes"] = "336", ["tags"] = "[\"x\"]", ["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01" }, line.Fields);

        var pino = JournalParser.AppLineOf("6", """{"level":"warn","msg":"slow","traceparent":"garbage"}""");
        Assert.Equal((LogLevelName.Warn, "slow", (string?)null), (pino.Level, pino.Message, pino.RequestId));

        const string NoMessage = """{"level":"error","code":7}""";
        var whole = JournalParser.AppLineOf("6", NoMessage);
        Assert.Equal((LogLevelName.Error, NoMessage), (whole.Level, whole.Message));
        Assert.Empty(whole.Fields);

        var many = JournalParser.AppLineOf("6", "{\"message\":\"m\"," + string.Join(',', Enumerable.Range(0, 40).Select(i => $"\"f{i}\":\"{new string('v', 1100)}\"")) + "}");
        Assert.Equal(32, many.Fields.Count);
        Assert.All(many.Fields.Values, value => Assert.Equal(1024, value.Length));
    }

    [Fact]
    public void OnlyDockerLinesWithAValidAppLabelAreAppLines()
    {
        var line = Lines("docker-app.jsonl")[0];
        Assert.Equal("system/docker", JournalParser.Parse(line.Replace("\"org.pithyx.spike\"", "\"../etc\"", StringComparison.Ordinal))!.Source);
        Assert.Equal("system/smbd", JournalParser.Parse(line.Replace("\"docker.service\"", "\"smbd.service\"", StringComparison.Ordinal))!.Source);
    }
}
