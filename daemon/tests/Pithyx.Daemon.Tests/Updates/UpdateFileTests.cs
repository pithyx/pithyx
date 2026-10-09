using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NSec.Cryptography;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Tests.Support;
using Pithyx.Daemon.Updates;

namespace Pithyx.Daemon.Tests.Updates;

public sealed class UpdateFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"pithyx-update-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static UpdateExpectation Expect(string installed = "0.1.0", string channel = "beta", bool allowUnsigned = false) =>
        new(UpdateCorpusTests.Keyring(Path.Combine(RepositoryFiles.Root, "tools", "release", "fixtures", "keys.json")), channel, allowUnsigned, installed, "amd64");

    [Fact]
    public async Task ASignedFileIsUnpackedAndChecked()
    {
        var file = TestUpdate.Build();

        var verified = await UpdateFile.ReadAsync(new MemoryStream(file), _directory, Expect(), CancellationToken.None);

        Assert.Equal("0.2.0-beta.3", verified.Manifest.Version);
        Assert.True(verified.TrustedSignature);
        Assert.Equal(file.Length, verified.Bytes);
        Assert.Equal(TestUpdate.Contents["debs/pithyx_0.2.0~beta.3_amd64.deb"], File.ReadAllBytes(Path.Combine(_directory, "debs", "pithyx_0.2.0~beta.3_amd64.deb")));
        Assert.True(File.Exists(Path.Combine(_directory, "manifest.json")));
    }

    [Fact]
    public async Task AChangedPackageIsRefusedAndNothingStays()
    {
        var file = TestUpdate.Build(tamper: "debs/pithyx-kernel_0.2.0~beta.3_amd64.deb");

        var error = await Assert.ThrowsAsync<PithyxException>(() => UpdateFile.ReadAsync(new MemoryStream(file), _directory, Expect(), CancellationToken.None));

        Assert.Equal(ErrorCodes.UpdateFileHashMismatch, error.Code);
        Assert.Equal("debs/pithyx-kernel_0.2.0~beta.3_amd64.deb", error.Parameters["path"]);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task AnEntryTheManifestDoesNotNameIsRefused()
    {
        var file = TestUpdate.Build(extra: "debs/evil_1.0_amd64.deb");

        var error = await Assert.ThrowsAsync<PithyxException>(() => UpdateFile.ReadAsync(new MemoryStream(file), _directory, Expect(), CancellationToken.None));

        Assert.Equal(ErrorCodes.UpdateFileUnexpectedEntry, error.Code);
    }

    [Fact]
    public async Task AMissingEntryIsRefused()
    {
        var file = TestUpdate.Build(omit: "debs/Release");

        var error = await Assert.ThrowsAsync<PithyxException>(() => UpdateFile.ReadAsync(new MemoryStream(file), _directory, Expect(), CancellationToken.None));

        Assert.Equal(ErrorCodes.UpdateFileMissingEntry, error.Code);
        Assert.Equal("debs/Release", error.Parameters["path"]);
    }

    [Fact]
    public async Task TheSignatureIsCheckedBeforeAnyPackageIsWritten()
    {
        var file = TestUpdate.Build(signed: false);

        var error = await Assert.ThrowsAsync<PithyxException>(() => UpdateFile.ReadAsync(new MemoryStream(file), _directory, Expect(), CancellationToken.None));

        Assert.Equal(ErrorCodes.UpdateFileSignatureInvalid, error.Code);
        Assert.Equal("missing", error.Parameters["reason"]);
    }

    [Fact]
    public async Task ADevelopmentBoxTakesAnUnsignedFile()
    {
        var file = TestUpdate.Build(signed: false);

        var verified = await UpdateFile.ReadAsync(new MemoryStream(file), _directory, Expect(allowUnsigned: true), CancellationToken.None);

        Assert.False(verified.TrustedSignature);
    }

    [Fact]
    public async Task AStableBoxRefusesABetaKey()
    {
        var error = await Assert.ThrowsAsync<PithyxException>(() =>
            UpdateFile.ReadAsync(new MemoryStream(TestUpdate.Build()), _directory, Expect(channel: "stable"), CancellationToken.None));

        Assert.Equal(ErrorCodes.UpdateFileSignatureInvalid, error.Code);
        Assert.Equal("untrusted", error.Parameters["reason"]);
    }

    [Theory]
    [InlineData("0.2.0-beta.3", ErrorCodes.UpdateFileOlder)]
    [InlineData("0.3.0", ErrorCodes.UpdateFileOlder)]
    [InlineData("0.0.9", ErrorCodes.UpdateFileMinimum)]
    public async Task TheVersionMustFitTheBox(string installed, string code)
    {
        var error = await Assert.ThrowsAsync<PithyxException>(() =>
            UpdateFile.ReadAsync(new MemoryStream(TestUpdate.Build()), _directory, Expect(installed), CancellationToken.None));

        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task TheUpdateFileOfTheCorpusReadsAsTheReleaseToolsWroteIt()
    {
        await using var file = File.OpenRead(Path.Combine(RepositoryFiles.Root, "tools", "release", "fixtures", "pithyx-0.2.0-beta.3-amd64.pithyxupdate"));

        var verified = await UpdateFile.ReadAsync(file, _directory, Expect(installed: "0.1.0-dev"), CancellationToken.None);

        Assert.Equal(("0.2.0-beta.3", "0.0.1", 4), (verified.Manifest.Version, verified.Manifest.MinimumVersion, verified.Manifest.Files.Count));
        Assert.True(verified.TrustedSignature);
    }

    [Fact]
    public async Task NotATarIsInvalid()
    {
        var error = await Assert.ThrowsAsync<PithyxException>(() =>
            UpdateFile.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 2048))), _directory, Expect(), CancellationToken.None));

        Assert.Equal(ErrorCodes.UpdateFileInvalid, error.Code);
    }
}

/// <summary>Small update files signed with the corpus key <c>test-beta</c>, as tools/release/build-update.sh lays them out.</summary>
internal static class TestUpdate
{
    public static readonly IReadOnlyDictionary<string, byte[]> Contents = new Dictionary<string, byte[]>
    {
        ["debs/Packages"] = Encoding.UTF8.GetBytes("Package: pithyx\n"),
        ["debs/Release"] = Encoding.UTF8.GetBytes("Origin: Pithyx\n"),
        ["debs/pithyx-kernel_0.2.0~beta.3_amd64.deb"] = Encoding.UTF8.GetBytes("kernel package"),
        ["debs/pithyx_0.2.0~beta.3_amd64.deb"] = Encoding.UTF8.GetBytes("pithyx package"),
    };

    public static byte[] Manifest(string version = "0.2.0-beta.3", string minimum = "0.1.0") =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            updateVersion = 1,
            version,
            channel = "beta",
            minimumVersion = minimum,
            architecture = "amd64",
            commit = "c2e871a0f3b1",
            publishedAt = "2026-10-09T21:00:00Z",
            kernel = new { release = "6.12.111+deb13-amd64", zfs = "2.3.9-0+deb13u1" },
            files = Contents.OrderBy(file => file.Key, StringComparer.Ordinal).Select(file => new
            {
                path = file.Key,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(file.Value)),
                size = file.Value.Length,
            }),
            packages = new[]
            {
                new { name = "pithyx", version = "0.2.0~beta.3", path = "debs/pithyx_0.2.0~beta.3_amd64.deb" },
                new { name = "pithyx-kernel", version = "0.2.0~beta.3", path = "debs/pithyx-kernel_0.2.0~beta.3_amd64.deb" },
            },
            changelog = "### Added\n\n- Updates.",
        }));

    public static byte[] Sign(byte[] bytes)
    {
        var seed = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryFiles.Root, "tools", "release", "fixtures", "keys.json")))
            .RootElement.GetProperty("keys").EnumerateArray().Single(key => key.GetProperty("keyId").GetString() == "test-beta").GetProperty("seed").GetString()!;
        using var key = Key.Import(SignatureAlgorithm.Ed25519, Convert.FromHexString(seed), KeyBlobFormat.RawPrivateKey);
        var signature = Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(key, bytes));
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { signatureVersion = 1, signatures = new[] { new { keyId = "test-beta", signature } } }));
    }

    public static byte[] Build(bool signed = true, string? tamper = null, string? extra = null, string? omit = null, string minimum = "0.1.0")
    {
        var manifest = Manifest(minimum: minimum);
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, TarEntryFormat.Gnu, leaveOpen: true))
        {
            Add(writer, UpdateFile.ManifestFile, manifest);
            if (signed)
            {
                Add(writer, UpdateFile.SignatureFile, Sign(manifest));
            }
            writer.WriteEntry(new GnuTarEntry(TarEntryType.Directory, "debs/"));
            foreach (var (path, content) in Contents.OrderBy(file => file.Key, StringComparer.Ordinal))
            {
                if (path == omit)
                {
                    continue;
                }
                Add(writer, path, path == tamper ? [.. content.Reverse()] : content);
            }
            if (extra is not null)
            {
                Add(writer, extra, [1, 2, 3]);
            }
        }
        return output.ToArray();
    }

    private static void Add(TarWriter writer, string name, byte[] content) =>
        writer.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(content) });
}
