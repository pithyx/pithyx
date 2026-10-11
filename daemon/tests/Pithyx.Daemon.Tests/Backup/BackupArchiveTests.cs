using System.IO.Compression;
using System.Security.Cryptography;
using Pithyx.Daemon.Backup;

namespace Pithyx.Daemon.Tests.Backup;

public sealed class BackupArchiveTests
{
    private static readonly byte[] _key = RandomNumberGenerator.GetBytes(BackupArchive.KeyBytes);
    private static readonly byte[] _salt = RandomNumberGenerator.GetBytes(BackupArchive.SaltBytes);

    private static byte[] Sealed(byte[] plaintext) => BackupArchive.Seal(plaintext, _key, _salt, 65536, 3);

    [Fact]
    public void OpensWhatItSealedWithTheSameKey()
    {
        var packed = BackupArchive.Pack(new Dictionary<string, byte[]> { ["manifest.json"] = "{}"u8.ToArray(), ["certificates/tls/ftps.pfx"] = [1, 2, 3] });
        var archive = Sealed(packed);

        var envelope = BackupArchive.Parse(archive);
        Assert.NotNull(envelope);
        Assert.Equal((65536, 3), (envelope.MemoryKib, envelope.Passes));
        Assert.Equal(_salt, envelope.Salt);
        var entries = BackupArchive.Unpack(BackupArchive.Open(envelope, _key)!);
        Assert.Equal(["certificates/tls/ftps.pfx", "manifest.json"], entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal([1, 2, 3], entries["certificates/tls/ftps.pfx"]);
    }

    [Fact]
    public void AWrongKeyOrAChangedByteOpensNothing()
    {
        var archive = Sealed([1, 2, 3, 4]);

        Assert.Null(BackupArchive.Open(BackupArchive.Parse(archive)!, RandomNumberGenerator.GetBytes(BackupArchive.KeyBytes)));
        var changedContent = (byte[])archive.Clone();
        changedContent[^20] ^= 1;
        Assert.Null(BackupArchive.Open(BackupArchive.Parse(changedContent)!, _key));
        // The header is authenticated: lower key settings fail like a wrong password.
        var changedHeader = (byte[])archive.Clone();
        changedHeader[13] = 2;
        Assert.Null(BackupArchive.Open(BackupArchive.Parse(changedHeader)!, _key));
    }

    [Fact]
    public void RefusesFilesThatAreNoBackupOrAskForAbsurdKeySettings()
    {
        Assert.Null(BackupArchive.Parse("not a backup at all, just some text"u8.ToArray()));
        Assert.Null(BackupArchive.Parse([]));
        var weak = BackupArchive.Seal([1], _key, _salt, 1024, 3);
        Assert.Null(BackupArchive.Parse(weak));
        var expensive = BackupArchive.Seal([1], _key, _salt, 4 * 1024 * 1024, 3);
        Assert.Null(BackupArchive.Parse(expensive));
        var otherFormat = Sealed([1]);
        otherFormat[8] = 2;
        Assert.Null(BackupArchive.Parse(otherFormat));
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("/etc/shadow")]
    [InlineData("certificates/../x")]
    [InlineData("a b")]
    public void RefusesEntriesThatLeaveTheirFolder(string name)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write([1]);
        }

        Assert.Throws<InvalidDataException>(() => BackupArchive.Unpack(buffer.ToArray()));
    }

    [Fact]
    public void RefusesAZipThatUnpacksToMoreThanTheLimit()
    {
        var packed = BackupArchive.Pack(new Dictionary<string, byte[]> { ["pithyx.db"] = new byte[1024 * 1024], ["users/shadow"] = new byte[10] });

        Assert.True(packed.Length < 10 * 1024);
        Assert.Throws<InvalidDataException>(() => BackupArchive.Unpack(packed, maxContentBytes: 1024 * 1024));
        Assert.Equal(2, BackupArchive.Unpack(packed, maxContentBytes: (1024 * 1024) + 10).Count);
    }

    [Fact]
    public void NamesTheFileAfterTheHostAndTheTime()
    {
        var at = new DateTimeOffset(2026, 10, 3, 18, 5, 0, TimeSpan.FromHours(2));

        Assert.Equal("pithyx-config-devbox-20261003-1605.pithyxbackup", ConfigBackupService.FileName("DEVBOX", at));
        Assert.Equal("pithyx-config-my-nas-local-20261003-1605.pithyxbackup", ConfigBackupService.FileName("my nas.local", at));
        Assert.Equal("pithyx-config-box-20261003-1605.pithyxbackup", ConfigBackupService.FileName("", at));
    }

    [Theory]
    [InlineData("ssh_host_ed25519_key", true)]
    [InlineData("ssh_host_rsa_key.pub", true)]
    [InlineData("ssh_host_rsa_key-cert.pub", false)]
    [InlineData("sshd_config", false)]
    [InlineData("ssh_host_../key", false)]
    public void TakesOnlySshHostKeys(string name, bool hostKey) => Assert.Equal(hostKey, ConfigBackupService.IsSshHostKey(name));
}
