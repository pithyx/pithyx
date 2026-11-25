using Pithyx.Daemon.Backups;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Backups;

/// <summary>rsync's command line and its output for server backups (D420), with lines rsync 3.5 printed.</summary>
public sealed class BackupRsyncTests
{
    [Fact]
    public void APullKeepsThePathsBelowTheDestinationAndRewritesOnlyChangedBlocks()
    {
        var arguments = BackupRsync.PullArguments("ssh -F /dev/null", ["/srv/www", "/etc"], "/proc/1/fd/9/", delete: true, BackupPullOwners.Server, ["*.tmp"], 500);

        Assert.Equal(
            [
                "-i", "PATH=/usr/bin:/bin", "LC_ALL=C.UTF-8", "HOME=/nonexistent",
                "rsync", "-e", "ssh -F /dev/null",
                "-rltHR", "--numeric-ids", "--inplace", "--no-whole-file", "--no-motd", "--itemize-changes", "--stats", "--info=progress2",
                "-pgoAX", "--delete", "--bwlimit=500", "--exclude=*.tmp",
                "--", "pithyx-target:/srv/www", "pithyx-target:/etc", "/proc/1/fd/9/",
            ],
            arguments);
        var boxOwners = BackupRsync.PullArguments("ssh", ["/"], "/d/", delete: false, BackupPullOwners.Box, [], 0);
        Assert.DoesNotContain("-pgoAX", boxOwners);
        Assert.DoesNotContain("--delete", boxOwners);
        Assert.DoesNotContain(boxOwners, argument => argument.StartsWith("--bwlimit", StringComparison.Ordinal));
    }

    [Fact]
    public void ItemizedLinesProgressAndStatisticsMakeTheReport()
    {
        var report = new RsyncReport();
        // The runner splits stdout at line feeds and carriage returns, as rsync's progress needs.
        foreach (var line in Fixture("rsync-pull.stdout").Split(['\n', '\r']))
        {
            BackupRsync.ReadLine(line, report);
        }
        BackupRsync.ReadErrors(Fixture("rsync-pull.stderr"), report);

        Assert.Equal(11, report.Files);
        Assert.Equal(300_020, report.Bytes);
        Assert.Equal((1, 1, 1), (report.Added, report.Changed, report.Removed));
        Assert.Equal(
            [
                new BackupVersionChange("srv/www/sub/old.txt", BackupChangeKind.Removed),
                new BackupVersionChange("srv/www/index.html", BackupChangeKind.Modified),
                new BackupVersionChange("srv/www/new.txt", BackupChangeKind.Added),
            ],
            report.Changes);
        Assert.Equal("srv/www/new.txt", report.Current);
        Assert.Equal(11, report.Progress!.ItemsTotal);
        Assert.Equal(11, report.Progress.ItemsDone);
        Assert.Equal(1, report.UnreadableCount);
        Assert.Equal([("etc/app/secret", "permission")], report.Unreadable);
    }

    [Fact]
    public void AProgressLineFollowedByAnItemizedLineCountsBoth()
    {
        var report = new RsyncReport();

        BackupRsync.ReadLine("        300,004  50%  286.10MB/s    0:00:00 (xfr#3, ir-chk=1004/2000)>f+++++++++ srv/a.txt", report);

        Assert.Equal(new Pithyx.Daemon.Jobs.JobProgress(996, 2000, 300_004, 600_008, null), report.Progress);
        Assert.Equal(1, report.Added);
        BackupRsync.ReadLine(">f..T...... srv/folder/", report);
        BackupRsync.ReadLine("cd+++++++++ srv/folder/", report);
        BackupRsync.ReadLine("cL+++++++++ srv/link -> a.txt", report);
        BackupRsync.ReadLine(".f...p..... srv/b.txt", report);
        Assert.Equal((1, 1), (report.Added, report.Changed));
    }

    [Fact]
    public void ErrorsComeFromSshFirstThenAMissingRsyncThenTheServer()
    {
        Assert.Null(BackupRsync.Failure(0, "", "/srv"));
        Assert.Null(BackupRsync.Failure(BackupRsync.Partial, "rsync: [sender] send_files failed to open \"/srv/x\": Permission denied (13)", "/srv"));
        Assert.Null(BackupRsync.Failure(BackupRsync.Vanished, "file has vanished: \"/srv/tmp/x\"", "/srv"));
        Assert.Equal(ErrorCodes.BackupTargetAuthFailed, BackupRsync.Failure(255, "backup@web01: Permission denied (publickey).\r\nrsync: connection unexpectedly closed (0 bytes received so far) [Receiver]", "/srv")!.Code);
        Assert.Equal(ErrorCodes.BackupServerRsyncMissing, BackupRsync.Failure(12, "sh: 1: rrsync: not found\nrsync: connection unexpectedly closed (0 bytes received so far) [Receiver]", "/srv")!.Code);
        Assert.Equal(ErrorCodes.BackupServerRsyncMissing, BackupRsync.Failure(12, "bash: line 1: rsync: command not found", "/srv")!.Code);
        var refused = BackupRsync.Failure(12, "rrsync error: post-realpath open failed\nrsync: connection unexpectedly closed", "/srv/nothing")!;
        Assert.Equal(ErrorCodes.BackupServerRefused, refused.Code);
        Assert.Equal("/srv/nothing", refused.Parameters["path"]);
        Assert.Equal(ErrorCodes.BackupServerRefused, BackupRsync.Failure(12, "rsync error: sending to read-only server is not allowed", "/")!.Code);
    }

    [Fact]
    public void TheVersionsAreTheDatasetsPullSnapshots()
    {
        var listing = "tank/pithyx/shares/servers/web01@pithyx-pull-20261124T100000Z\t1795519200\t4096\t0\n"
            + "tank/pithyx/shares/servers/web01@pithyx-daily-20261124T000000Z\t1795478400\t0\t0\n"
            + "tank/pithyx/shares/servers/web01@by-hand\t1795478400\t0\t0\n"
            + "tank/pithyx/shares/servers/web01@pithyx-pull-20261124T110000Z\t1795522800\t-\t1\n";

        var snapshots = BackupRsync.ParseSnapshots(listing, "tank/pithyx/shares/servers/web01");

        Assert.Equal(
            [
                ("pithyx-pull-20261124T100000Z", DateTimeOffset.FromUnixTimeSeconds(1795519200), 4096L, 0),
                ("pithyx-pull-20261124T110000Z", DateTimeOffset.FromUnixTimeSeconds(1795522800), (long?)null, 1),
            ],
            snapshots);
    }

    [Theory]
    [InlineData(0, "", null, null)]
    [InlineData(24, "file has vanished: \"/run/pithyx/backup-stage/view/./Media/a\"\n", null, null)]
    [InlineData(23, "rsync: [sender] send_files failed to open \"/run/pithyx/backup-stage/view/./Media/a\": Permission denied (13)\n", null, null)]
    [InlineData(23, "rsync: [generator] recv_generator: mkdir \"/m/Media\" failed: Permission denied (13)\n", ErrorCodes.BackupTargetNotWritable, "write")]
    [InlineData(12, "rsync: write failed on \"/m/a\": Disk quota exceeded (122)\n", ErrorCodes.BackupTargetNotWritable, "full")]
    [InlineData(2, "protocol version mismatch -- is your shell clean?\n", ErrorCodes.BackupTargetRsyncMissing, null)]
    [InlineData(127, "sh: 1: rsync: not found\n", ErrorCodes.BackupTargetRsyncMissing, null)]
    [InlineData(255, "u1@host: Permission denied (publickey).\n", ErrorCodes.BackupTargetAuthFailed, null)]
    [InlineData(255, "kex_exchange_identification: read: Connection reset by peer\n", ErrorCodes.BackupTargetUnreachable, null)]
    [InlineData(20, "rsync error: received SIGINT, SIGTERM, or SIGHUP (code 20)\n", ErrorCodes.BackupRunFailed, null)]
    public void AMirrorsFailuresNameTheTargetsProblem(int exitCode, string standardError, string? code, string? reason)
    {
        var failure = BackupRsync.MirrorFailure(exitCode, standardError, "backups/m");

        Assert.Equal(code, failure?.Code);
        if (reason is not null)
        {
            Assert.Equal(reason, failure!.Parameters["reason"]);
        }
    }

    [Fact]
    public void AMirrorSendsEachSourceBelowItsNameAndNamesUnreadableFilesSo()
    {
        var arguments = BackupRsync.MirrorArguments("ssh", ["Media/Photos", "homes/anna"], "backups/m", delete: false, ["*.tmp"], 0);

        Assert.Equal(["--exclude=*.tmp", "--", "/run/pithyx/backup-stage/view/./Media/Photos", "/run/pithyx/backup-stage/view/./homes/anna", "pithyx-target:backups/m/"], arguments.TakeLast(5));
        Assert.DoesNotContain("--delete", arguments);
        Assert.Equal("Media/Photos/a.jpg", BackupRsync.MirrorItem("run/pithyx/backup-stage/view/./Media/Photos/a.jpg"));
        Assert.Equal("homes/anna/x", BackupRsync.MirrorItem("/run/pithyx/backup-stage/view/homes/anna/x"));
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(RepositoryFiles.Root, "daemon", "tests", "fixtures", "backups", name));
}
