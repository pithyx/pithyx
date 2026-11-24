using Pithyx.Daemon.Backups;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Backups;

/// <summary>restic's command lines and its JSON output (D415), with lines restic 0.18 printed.</summary>
public sealed class ResticTests
{
    private static readonly string[] SshOptions = ["-F", "/dev/null", "-i", "/tmp/k/key", "-o", "HostName=u1.example.de"];

    [Fact]
    public void EveryCallOpensTheRepositoryOverTheTargetsSsh()
    {
        var global = ResticCommands.Global("backups/media", "/tmp/k/repository", SshOptions, 500, 0, "/tank/.pithyx/backup-cache/3");

        Assert.Equal(
            [
                "-r", "sftp:pithyx-target:backups/media",
                "--password-file", "/tmp/k/repository",
                "-o", "sftp.command=ssh -F /dev/null -i /tmp/k/key -o HostName=u1.example.de pithyx-target -s sftp",
                "--json",
                "--limit-upload", "500",
                "--cache-dir", "/tank/.pithyx/backup-cache/3",
            ],
            global);
        Assert.Contains("--no-cache", ResticCommands.Global("/srv/r", "p", SshOptions, 0, 0, null));
        var env = ResticCommands.Env(["snapshots"]);
        Assert.Equal(["-i", "PATH=/usr/bin:/bin", "LC_ALL=C.UTF-8", "HOME=/nonexistent", "RESTIC_PROGRESS_FPS=0.5", "restic", "snapshots"], env);
    }

    [Fact]
    public void AnSshOptionWithASpaceIsRefusedSinceResticSplitsTheCommandAtSpaces()
    {
        Assert.Throws<ArgumentException>(() => ResticCommands.SftpCommand(["-o", "User=a b"]));
        Assert.Throws<ArgumentException>(() => ResticCommands.SftpCommand(["-o", "User=\"a\""]));
    }

    [Fact]
    public void ABackupMountsTheSnapshotsParentsFirstAndStoresTheLivePaths()
    {
        var backup = ResticCommands.Backup(["-r", "x"], ["/tank/shares/media", "/tank/homes/anna/Photos"], ["*.tmp", "cache/"], "box");
        var command = ResticCommands.InNamespace(
            [new SnapshotMount("tank/homes/anna@pithyx-backup-20261122T200000Z", "/tank/homes/anna"), new SnapshotMount("tank/shares/media@pithyx-backup-20261122T200000Z", "/tank/shares/media")],
            ["env", .. ResticCommands.Env(backup)]);

        Assert.Equal(["-r", "x", "backup", "--group-by", "", "--host", "box", "--exclude", "*.tmp", "--exclude", "cache/", "--", "/tank/shares/media", "/tank/homes/anna/Photos"], backup);
        Assert.Equal(["--mount", "--propagation", "private", "--", "sh", "-c", ResticCommands.MountScript, "sh", "/run/pithyx/backup-stage"], command.Take(9));
        Assert.Equal(["tank/homes/anna@pithyx-backup-20261122T200000Z", "/tank/homes/anna", "tank/shares/media@pithyx-backup-20261122T200000Z", "/tank/shares/media", "--", "env"], command.Skip(9).Take(6));
        Assert.Equal("restic", command[command.ToList().IndexOf("restic")]);
    }

    [Fact]
    public void RetentionForgetsOverTheWholeRepositoryAndKeepingAllRunsNoForget()
    {
        Assert.Null(ResticCommands.Forget([], new BackupRetention(0, 0, 0, 0, 0, 0)));
        Assert.Equal(
            ["forget", "--prune", "--group-by", "", "--keep-tag", "pithyx-protected", "--keep-last", "2", "--keep-daily", "7", "--keep-weekly", "4", "--keep-monthly", "12"],
            ResticCommands.Forget([], new BackupRetention(2, 0, 7, 4, 12, 0)));
        Assert.Equal(["check", "--with-cache", "--read-data-subset=5%"], ResticCommands.Check([], 5));
        Assert.Equal(["check", "--with-cache"], ResticCommands.Check([], 0));
    }

    [Fact]
    public void ABackupsLinesGiveProgressFiguresAndUnreadableFiles()
    {
        var report = new ResticReport();
        var progress = 0;
        foreach (var line in Fixture("restic-backup-warning.stdout").Split('\n'))
        {
            progress += ResticOutput.Read(line, report) ? 1 : 0;
        }
        foreach (var line in Fixture("restic-backup-warning.stderr").Split('\n'))
        {
            ResticOutput.Read(line, report);
        }

        Assert.True(progress >= 1);
        Assert.Equal(34, report.Progress!.ItemsTotal);
        Assert.Equal(90_600_002, report.Progress.BytesTotal);
        Assert.Equal(33, report.Files);
        Assert.Equal(90_600_000, report.Bytes);
        Assert.True(report.AddedBytes > 90_000_000);
        Assert.Equal(64, report.Snapshot!.Length);
        Assert.Equal(1, report.Warnings);
        Assert.Equal([new ResticUnreadable("/tank/shares/media/src/sub/locked", "open /tank/shares/media/src/sub/locked: permission denied")], report.WarningItems);
        Assert.Equal("permission", report.WarningItems[0].Reason);
        Assert.False(ResticOutput.Read("Fatal: something", report));
        Assert.False(ResticOutput.Read("{not json", report));
    }

    [Fact]
    public void ACheckSummaryCountsItsErrors()
    {
        var report = new ResticReport();

        ResticOutput.Read("""{"message_type":"summary","num_errors":2,"broken_packs":["ab"],"suggest_repair_index":true,"suggest_prune":false}""", report);

        Assert.Equal(2, report.CheckErrors);
        Assert.Null(report.Snapshot);
    }

    [Theory]
    [InlineData(0, "", null)]
    [InlineData(3, "", null)]
    [InlineData(10, "Fatal: repository does not exist", ErrorCodes.BackupRepositoryNotFound)]
    [InlineData(11, "Fatal: unable to create lock", ErrorCodes.BackupRepositoryLocked)]
    [InlineData(12, "Fatal: wrong password or no key found", ErrorCodes.BackupRepositoryWrongKey)]
    [InlineData(70, "", ErrorCodes.BackupSourceUnavailable)]
    [InlineData(1, "Fatal: create repository at sftp:pithyx-target:r failed: config file already exists", ErrorCodes.BackupRepositoryExists)]
    [InlineData(1, "Fatal: unexpected", ErrorCodes.BackupRunFailed)]
    public void ExitCodesMapToErrors(int exitCode, string standardError, string? code)
    {
        Assert.Equal(code, ResticOutput.Failure(exitCode, standardError, "r")?.Code);
    }

    [Theory]
    [InlineData("sftp-hostkey.stderr", ErrorCodes.BackupTargetHostKeyChanged)]
    [InlineData("sftp-auth.stderr", ErrorCodes.BackupTargetAuthFailed)]
    [InlineData("sftp-refused.stderr", ErrorCodes.BackupTargetUnreachable)]
    [InlineData("sftp-dns.stderr", ErrorCodes.BackupTargetUnreachable)]
    public void SshsMessagesOnTheWayThroughResticNameTheConnectionFailure(string fixture, string code)
    {
        var standardError = "subprocess ssh: " + Fixture(fixture) + "Fatal: unable to open repository at sftp:pithyx-target:r: unable to start the sftp session, error: EOF\n";

        Assert.Equal(code, ResticOutput.Failure(1, standardError, "r")?.Code);
    }

    [Fact]
    public void AMountServesEveryVersionUnderItsFullId()
    {
        Assert.Equal(["-r", "x", "mount", "--path-template", "ids/%I", "--", "/run/pithyx/backup-browse/3"], ResticCommands.Mount(["-r", "x"], "/run/pithyx/backup-browse/3"));
    }

    [Fact]
    public void ARestoreIncludesEachStoredPathLiterally()
    {
        var restore = ResticCommands.Restore(["-r", "x"], "9f9e9f2c", "/proc/1/fd/7/r", ["/tank/shares/docs/a*b?[1].txt", "/tank/shares/docs/c\\d"]);

        Assert.Equal(
            ["-r", "x", "restore", "--target", "/proc/1/fd/7/r", "--include", "/tank/shares/docs/a\\*b\\?\\[1].txt", "--include", "/tank/shares/docs/c\\\\d", "--", "9f9e9f2c"],
            restore);
    }

    [Fact]
    public void ARestoresStatusLinesAreItsProgressAndItsErrorsAreCounted()
    {
        var progress = Fixture("restic-restore.stdout").Split('\n').Select(ResticOutput.RestoreProgress).ToList();
        Assert.Equal(new JobProgress(3, 7, 2693387, 30000015, null), progress[0]);
        Assert.Equal(25441617, progress[1]!.BytesDone);
        Assert.Null(progress[2]);
        Assert.Null(ResticOutput.RestoreProgress("Fatal: no snapshot"));

        var report = new ResticReport();
        foreach (var line in Fixture("restic-restore-errors.stderr").Split('\n'))
        {
            ResticOutput.Read(line, report);
        }
        Assert.Equal(2, report.Warnings);
        Assert.Equal("/tank/shares/docs/a/x.txt", report.WarningItems[1].Item);
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(RepositoryFiles.Root, "daemon", "tests", "fixtures", "backups", name));
}
