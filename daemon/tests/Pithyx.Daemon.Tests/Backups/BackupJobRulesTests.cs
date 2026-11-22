using Pithyx.Daemon.Backups;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Tests.Backups;

/// <summary>What a backup job may hold (D415).</summary>
public sealed class BackupJobRulesTests
{
    private static readonly BackupJobSettings Valid = new("  Media  ", [new(1, "/Photos/"), new(1, "Photos"), new(2, "")], " 0 3 * * * ", true, BackupRetention.Default, ["*.tmp", " ", "*.tmp"]);

    [Fact]
    public void SettingsAreTrimmedAndSourcesDeduplicated()
    {
        var settings = BackupJobRules.Check(Valid);

        Assert.Equal("Media", settings.Name);
        Assert.Equal([new BackupSource(1, "Photos"), new BackupSource(2, "")], settings.Sources);
        Assert.Equal("0 3 * * *", settings.Schedule);
        Assert.Equal(["*.tmp"], settings.Excludes);
        Assert.Null(settings.CheckSchedule);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("sources")]
    [InlineData("schedule")]
    [InlineData("checkSchedule")]
    [InlineData("retention")]
    [InlineData("checkReadPercent")]
    [InlineData("uploadLimitKib")]
    [InlineData("overdueHours")]
    public void EachBadFieldIsNamed(string field)
    {
        var settings = field switch
        {
            "name" => Valid with { Name = "" },
            "sources" => Valid with { Sources = [new(1, "a/../b")] },
            "schedule" => Valid with { Schedule = "daily" },
            "checkSchedule" => Valid with { CheckSchedule = "61 * * * *" },
            "retention" => Valid with { Retention = new BackupRetention(-1, 0, 0, 0, 0, 0) },
            "checkReadPercent" => Valid with { CheckReadPercent = 101 },
            "uploadLimitKib" => Valid with { UploadLimitKib = -1 },
            _ => Valid with { OverdueHours = 0 },
        };

        var error = Assert.Throws<PithyxException>(() => BackupJobRules.Check(settings));

        Assert.Equal(ErrorCodes.BackupJobInvalid, error.Code);
        Assert.Equal(field, error.Parameters["field"]);
    }

    [Theory]
    [InlineData("pithyx", "pithyx")]
    [InlineData("/nas/media/", "nas/media")]
    [InlineData("a/../b", null)]
    [InlineData("a//b", null)]
    [InlineData("C:\\x", null)]
    [InlineData("a\"b", null)]
    [InlineData("", null)]
    public void RepositoriesAreRelativeFoldersWithoutTricks(string input, string? expected)
    {
        if (expected is null)
        {
            Assert.Throws<PithyxException>(() => BackupJobRules.CheckRepository(input));
        }
        else
        {
            Assert.Equal(expected, BackupJobRules.CheckRepository(input));
        }
    }

    [Theory]
    [InlineData("", "r", "r")]
    [InlineData("/", "r", "/r")]
    [InlineData("backups", "r", "backups/r")]
    [InlineData("/srv/backups", "nas/r", "/srv/backups/nas/r")]
    public void TheRepositoryLiesBelowTheTargetsPath(string targetPath, string repository, string expected) =>
        Assert.Equal(expected, BackupJobRules.RemotePath(targetPath, repository));

    [Fact]
    public void AJobIsOverdueAfterThreeIntervalsButNeverBeforeTwoDays()
    {
        var now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromDays(3), BackupJobRules.OverdueLimit("0 3 * * *", null, now, TimeZoneInfo.Utc));
        Assert.Equal(TimeSpan.FromDays(21), BackupJobRules.OverdueLimit("0 3 * * 0", null, now, TimeZoneInfo.Utc));
        Assert.Equal(TimeSpan.FromDays(2), BackupJobRules.OverdueLimit("0 * * * *", null, now, TimeZoneInfo.Utc));
        Assert.Equal(TimeSpan.FromHours(30), BackupJobRules.OverdueLimit("0 * * * *", 30, now, TimeZoneInfo.Utc));
    }
}
