using Pithyx.Daemon.Backups;
using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Backups;

/// <summary>Host keys and connection tests of backup targets, on output of OpenSSH 10.0 recorded on a dev VM.</summary>
public sealed class BackupSshTests
{
    private const string LocalEd25519 = "SHA256:+k2DS16k//dZzjNhStd/auE6WNdHV8IwySL7UEOyg0o";

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(RepositoryFiles.Root, "daemon", "tests", "fixtures", "backups", name));

    private static CommandResult Sftp(string name, int exitCode) => new(exitCode, Fixture($"sftp-{name}.stdout"), Fixture($"sftp-{name}.stderr"), TimeSpan.Zero);

    [Fact]
    public void AScanOffersEveryKeyTypeOnceWithEd25519First()
    {
        var keys = SshHostKeys.ParseScan(Fixture("keyscan-localhost.stdout"));

        Assert.Equal(["ssh-ed25519", "ecdsa-sha2-nistp256", "ssh-rsa"], keys.Select(key => key.Type));
        // ssh printed this fingerprint for the same key when it refused a wrong known_hosts line.
        Assert.Equal(LocalEd25519, keys[0].Fingerprint);
        Assert.StartsWith("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5", keys[0].Key, StringComparison.Ordinal);
        Assert.Empty(SshHostKeys.ParseScan(""));
    }

    [Theory]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl")]
    [InlineData("github.com ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl")]
    [InlineData("[u1.example.de]:23 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl a comment")]
    public void APastedKeyIsReadWithOrWithoutHostAndComment(string line)
    {
        var key = SshHostKeys.Parse(line);

        Assert.NotNull(key);
        Assert.Equal("ssh-ed25519", key.Type);
        Assert.Equal("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl", key.Key);
        // GitHub publishes this fingerprint for the key.
        Assert.Equal("SHA256:+DiY3wvvV6TuJJhbpZisF/zLDA0zPMSvHdkr4UvCOqU", key.Fingerprint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ssh-ed25519")]
    [InlineData("ssh-ed25519 not-base64!")]
    [InlineData("ssh-dss AAAAB3NzaC1kc3MAAACBAP")]
    // An Ed25519 blob under the name of another type.
    [InlineData("ssh-rsa AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl")]
    public void AnythingElseIsNoHostKey(string line) => Assert.Null(SshHostKeys.Parse(line));

    [Fact]
    public void AProbeMakesEachFolderOfThePathThenWritesAndRemovesItsFile()
    {
        var script = BackupSsh.ProbeScript("backups/box one", "/tmp/k/empty", ".pithyx-test-x");

        Assert.Equal(
            "-mkdir \"backups\"\n-mkdir \"backups/box one\"\ncd \"backups/box one\"\nput \"/tmp/k/empty\" \".pithyx-test-x\"\nrm \".pithyx-test-x\"\n",
            script);
        Assert.Equal("put \"/e\" \"t\"\nrm \"t\"\n", BackupSsh.ProbeScript("", "/e", "t"));
        Assert.StartsWith("-mkdir \"/srv\"\n-mkdir \"/srv/backup\"\ncd \"/srv/backup\"\n", BackupSsh.ProbeScript("/srv/backup", "/e", "t"), StringComparison.Ordinal);
    }

    [Fact]
    public void ASuccessfulProbeHasNoError() => Assert.Null(BackupSsh.ProbeOutcome(Sftp("ok", 0), "backups/box one"));

    [Theory]
    [InlineData("hostkey", 255, ErrorCodes.BackupTargetHostKeyChanged, null)]
    [InlineData("unknownkey", 255, ErrorCodes.BackupTargetHostKeyChanged, null)]
    [InlineData("auth", 255, ErrorCodes.BackupTargetAuthFailed, null)]
    [InlineData("refused", 255, ErrorCodes.BackupTargetUnreachable, "refused")]
    [InlineData("dns", 255, ErrorCodes.BackupTargetUnreachable, "dns")]
    [InlineData("timeout", 255, ErrorCodes.BackupTargetUnreachable, "timeout")]
    [InlineData("nopath", 1, ErrorCodes.BackupTargetNotWritable, "path")]
    [InlineData("missing", 1, ErrorCodes.BackupTargetNotWritable, "path")]
    [InlineData("readonly", 1, ErrorCodes.BackupTargetNotWritable, "write")]
    public void AFailedProbeSaysWhy(string fixture, int exitCode, string code, string? reason)
    {
        var error = BackupSsh.ProbeOutcome(Sftp(fixture, exitCode), "backups");

        Assert.NotNull(error);
        Assert.Equal(code, error.Code);
        Assert.Equal(reason, error.Parameters.GetValueOrDefault("reason") as string);
    }

    [Fact]
    public void AChangedHostKeyNamesTheKeyTheServerSent()
    {
        var error = BackupSsh.ProbeOutcome(Sftp("hostkey", 255), "");

        Assert.Equal(LocalEd25519, Assert.IsType<string>(error!.Parameters["fingerprint"]));
    }

    [Fact]
    public void EveryConnectionPinsTheKeysAndNamesTheServerOnlyThroughOptions()
    {
        using var folder = new KeyFolder();
        var options = BackupSsh.Options(folder, new SshEndpoint("u1.example.de", 23, "u1", "ssh-ed25519 AAAA", "PRIVATE"));

        Assert.Contains("StrictHostKeyChecking=yes", options);
        Assert.Contains("GlobalKnownHostsFile=/dev/null", options);
        Assert.Contains("IdentitiesOnly=yes", options);
        Assert.Contains("HostName=u1.example.de", options);
        Assert.Contains("Port=23", options);
        Assert.Contains("User=u1", options);
        Assert.Equal("pithyx-target ssh-ed25519 AAAA\n", File.ReadAllText(Path.Combine(folder.Path, "known_hosts")));
        Assert.Equal("PRIVATE\n", File.ReadAllText(Path.Combine(folder.Path, "key")));
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(folder.Path, "key")));
        }
    }
}
