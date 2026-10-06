using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using Pithyx.Daemon.Apps.Packages;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Tests.Apps.Packages;

/// <summary>
/// The tar reader against hostile archives: every refused archive throws its code and leaves only the staging folder
/// (which the caller deletes); nothing is ever written outside it.
/// </summary>
public sealed class PackageReaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pithyx-package-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Staging => Path.Combine(_root, "staging");

    private Task<UnpackedPackage> UnpackAsync(byte[] package, PackageLimits? limits = null) =>
        PackageReader.UnpackAsync(new MemoryStream(package), Staging, limits ?? PackageLimits.Default, Token);

    private async Task<PithyxException> RefusedAsync(byte[] package, string code, string? reason = null, PackageLimits? limits = null)
    {
        var refused = await Assert.ThrowsAsync<PithyxException>(() => UnpackAsync(package, limits));
        Assert.Equal(code, refused.Code);
        if (reason is not null)
        {
            Assert.Equal(reason, refused.Parameters["reason"]);
        }
        // Nothing ever lands next to the staging folder.
        Assert.Equal([Staging], Directory.GetFileSystemEntries(_root));
        return refused;
    }

    private Task<PithyxException> InvalidAsync(byte[] package, string reason) => RefusedAsync(package, ErrorCodes.AppPackageInvalid, reason);

    private Task<PithyxException> LayoutAsync(byte[] package, string reason) => RefusedAsync(package, ErrorCodes.AppPackageLayoutInvalid, reason);

    [Fact]
    public async Task AValidPackageIsUnpackedWithFixedModesAndItsHash()
    {
        var package = TarBuilder.MinimalPackage().File("frontend/assets/style.css", "body{}", mode: "0004755").End().Gzip();

        var unpacked = await UnpackAsync(package);

        Assert.Equal(["pithyx.json", "icon.svg", "frontend/index.html", "frontend/app.js", "frontend/assets/style.css"], unpacked.Files.Select(file => file.Path));
        Assert.Equal("body{}", await File.ReadAllTextAsync(Path.Combine(Staging, "frontend", "assets", "style.css"), Token));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(package)), unpacked.Sha256);
        Assert.Equal(package.Length, unpacked.CompressedBytes);
        Assert.Equal(unpacked.Files.Sum(file => file.Size), unpacked.UnpackedBytes);
        // The setuid executable of the archive is a plain file on the box.
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                File.GetUnixFileMode(Path.Combine(Staging, "frontend", "assets", "style.css")));
            Assert.False(File.GetUnixFileMode(Path.Combine(Staging, "frontend")).HasFlag(UnixFileMode.SetGroup));
        }
    }

    [Theory]
    [InlineData(TarEntryFormat.Pax)]
    [InlineData(TarEntryFormat.Gnu)]
    [InlineData(TarEntryFormat.Ustar)]
    public async Task ArchivesOfOtherWritersWithLongNamesAreRead(TarEntryFormat format)
    {
        var deep = "frontend/" + string.Join('/', Enumerable.Repeat("folder-with-a-long-name", 6)) + "/file.js";
        var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, format, leaveOpen: true))
        {
            foreach (var (name, content) in new[] { ("./pithyx.json", TarBuilder.Manifest()), ("./icon.svg", TarBuilder.Icon), ("./frontend/index.html", "<html>"), ("./" + deep, "x") })
            {
                var entry = format switch
                {
                    TarEntryFormat.Pax => (TarEntry)new PaxTarEntry(TarEntryType.RegularFile, name),
                    TarEntryFormat.Gnu => new GnuTarEntry(TarEntryType.RegularFile, name),
                    _ => new UstarTarEntry(TarEntryType.RegularFile, name),
                };
                entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
                await writer.WriteEntryAsync(entry, Token);
            }
        }

        var unpacked = await UnpackAsync(TarBuilder.Compress(tar.ToArray()));

        Assert.Contains(deep, unpacked.Files.Select(file => file.Path));
        Assert.True(File.Exists(Path.Combine(Staging, deep)));
    }

    [Fact]
    public async Task TheArchivesOwnRootFolderIsSkipped()
    {
        var unpacked = await UnpackAsync(new TarBuilder().Folder("./").File("./pithyx.json", "{}").End().Gzip());
        Assert.Equal(["pithyx.json"], unpacked.Files.Select(file => file.Path));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("frontend/../../evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("frontend/./x.js")]
    [InlineData("././pithyx.json")]
    [InlineData("frontend\\..\\..\\evil.txt")]
    [InlineData("frontend//x.js")]
    [InlineData("frontend/café.js")]
    [InlineData("frontend/‥/x.js")]
    [InlineData("frontend/%2e%2e/x.js")]
    [InlineData("frontend/.hidden")]
    [InlineData("frontend/x\n.js")]
    public async Task UnsafeNamesAreRefused(string name)
    {
        var refused = await LayoutAsync(TarBuilder.MinimalPackage().File(name, "x").End().Gzip(), "unsafe");
        Assert.Equal(name, refused.Parameters["path"]);
    }

    [Fact]
    public async Task UnsafeFolderNamesAreRefused() =>
        await LayoutAsync(new TarBuilder().Folder("../outside/").End().Gzip(), "unsafe");

    [Fact]
    public async Task AnOverlongUtf8DotIsNoName()
    {
        // C0 AE is an overlong encoding of "."; a lax decoder would see "frontend/../x".
        var header = TarBuilder.Ustar("", '0', 1);
        header.Set(0, 100, [.. "frontend/"u8.ToArray(), 0xC0, 0xAE, 0xC0, 0xAE, .. "/x"u8.ToArray()]);
        await InvalidAsync(new TarBuilder().Entry(header, "x"u8.ToArray()).End().Gzip(), "format");
    }

    [Fact]
    public async Task ANulInsideTheNameFieldIsRefused()
    {
        // An extractor reading the whole field would write "../evil.txt"; one stopping at the NUL "pithyx.json".
        var header = TarBuilder.Ustar("", '0', 2);
        header.Set(0, 100, [.. "pithyx.json"u8.ToArray(), 0, .. "../evil.txt"u8.ToArray()]);
        await InvalidAsync(new TarBuilder().Entry(header, "{}"u8.ToArray()).End().Gzip(), "format");
    }

    [Fact]
    public async Task ANulInAPaxPathIsRefused() =>
        await InvalidAsync(new TarBuilder().Pax(("path", "pithyx.json\0../evil")).File("pithyx.json", "{}").End().Gzip(), "extended_header");

    [Theory]
    [InlineData('1')]
    [InlineData('2')]
    [InlineData('3')]
    [InlineData('4')]
    [InlineData('6')]
    [InlineData('7')]
    [InlineData('S')]
    [InlineData('V')]
    [InlineData('D')]
    [InlineData('M')]
    [InlineData('A')]
    public async Task LinksDevicesFifosAndOtherEntriesAreRefused(char type)
    {
        var refused = await InvalidAsync(new TarBuilder().Typed("frontend/thing", type, link: "/etc/passwd").End().Gzip(), "entry_type");
        Assert.Equal("frontend/thing", refused.Parameters["path"]);
    }

    [Fact]
    public async Task ASymlinkFollowedByAFileThroughItWritesNothing()
    {
        var package = TarBuilder.MinimalPackage()
            .Typed("frontend/out", '2', link: _root)
            .File("frontend/out/evil.txt", "x")
            .End().Gzip();
        await InvalidAsync(package, "entry_type");
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public async Task AHardlinkToAFileOutsideIsRefused() =>
        await InvalidAsync(new TarBuilder().Typed("pithyx.json", '1', link: "/etc/shadow").End().Gzip(), "entry_type");

    [Theory]
    [InlineData("pithyx.json")]
    [InlineData("frontend/index.html")]
    [InlineData("icon.svg")]
    public async Task ALaterEntryCannotReplaceAnEarlierOne(string name)
    {
        var refused = await LayoutAsync(TarBuilder.MinimalPackage().File(name, "replaced").End().Gzip(), "duplicate");
        Assert.Equal(name, refused.Parameters["path"]);
    }

    [Theory]
    [InlineData("PITHYX.json")]
    [InlineData("Frontend/index.html")]
    [InlineData("frontend/INDEX.html")]
    public async Task NamesDifferingOnlyInCaseAreDuplicates(string name) =>
        await LayoutAsync(TarBuilder.MinimalPackage().File(name, "x").End().Gzip(), "duplicate");

    [Fact]
    public async Task AFileInTheWayOfAFolderIsADuplicate()
    {
        await LayoutAsync(new TarBuilder().File("frontend", "x").File("frontend/index.html", "x").End().Gzip(), "duplicate");
        Directory.Delete(Staging, recursive: true);
        await LayoutAsync(new TarBuilder().File("frontend/index.html", "x").File("frontend", "x").End().Gzip(), "duplicate");
        Directory.Delete(Staging, recursive: true);
        await LayoutAsync(new TarBuilder().Folder("frontend/").File("frontend", "x").End().Gzip(), "duplicate");
        Directory.Delete(Staging, recursive: true);
        await LayoutAsync(new TarBuilder().Folder("frontend/").Folder("FRONTEND/").End().Gzip(), "duplicate");
    }

    [Fact]
    public async Task TheSameFolderTwiceIsHarmless()
    {
        var unpacked = await UnpackAsync(new TarBuilder().Folder("frontend/").Folder("frontend/").File("frontend/a.js", "x").End().Gzip());
        Assert.Single(unpacked.Files);
    }

    [Fact]
    public async Task APaxPathMustContinueTheEntrysOwnName()
    {
        // A reader ignoring pax would write pithyx.json; this one would write frontend/app.js.
        var refused = await InvalidAsync(new TarBuilder().Pax(("path", "frontend/app.js")).File("pithyx.json", "{}").End().Gzip(), "name_mismatch");
        Assert.Equal("frontend/app.js", refused.Parameters["path"]);
    }

    [Fact]
    public async Task AGnuLongNameMustContinueTheEntrysOwnName() =>
        await InvalidAsync(new TarBuilder().GnuLongName("frontend/app.js").File("pithyx.json", "{}").End().Gzip(), "name_mismatch");

    [Fact]
    public async Task ATruncatedOwnNameUnderALongNameIsAccepted()
    {
        var name = "frontend/" + new string('a', 120) + ".js";
        var unpacked = await UnpackAsync(new TarBuilder().Pax(("path", name), ("mtime", "1.5")).File(name[..100], "x").End().Gzip());
        Assert.Equal(name, unpacked.Files.Single().Path);
    }

    [Fact]
    public async Task APaxSizeOverridesTheHeader()
    {
        var header = TarBuilder.Ustar("frontend/a.js", '0', 0);
        var unpacked = await UnpackAsync(new TarBuilder().Pax(("size", "3")).Entry(header, "abc"u8.ToArray()).End().Gzip());
        Assert.Equal(3, unpacked.Files.Single().Size);
    }

    [Theory]
    [InlineData("linkpath", "/etc/passwd")]
    [InlineData("GNU.sparse.map", "0,10")]
    [InlineData("unknown.key", "x")]
    [InlineData("size", "-1")]
    [InlineData("size", "abc")]
    [InlineData("size", "99999999999999999999")]
    [InlineData("path", "")]
    public async Task PaxRecordsBeyondNameSizeAndMetadataAreRefused(string key, string value) =>
        await InvalidAsync(new TarBuilder().Pax((key, value)).File("frontend/a.js", "x").End().Gzip(), "extended_header");

    [Fact]
    public async Task DuplicatePaxKeysAreRefused() =>
        await InvalidAsync(new TarBuilder().Pax(("path", "frontend/a.js"), ("path", "frontend/b.js")).File("frontend/a.js", "x").End().Gzip(), "extended_header");

    [Fact]
    public async Task AGlobalPaxHeaderIsRefused() =>
        await InvalidAsync(new TarBuilder().PaxTyped('g', ("path", "x")).File("frontend/a.js", "x").End().Gzip(), "extended_header");

    [Fact]
    public async Task TwoExtendedHeadersForOneEntryAreRefused()
    {
        await InvalidAsync(new TarBuilder().Pax(("mtime", "1")).Pax(("mtime", "2")).File("frontend/a.js", "x").End().Gzip(), "extended_header");
        Directory.Delete(Staging, recursive: true);
        await InvalidAsync(new TarBuilder().Pax(("path", "frontend/a.js")).GnuLongName("frontend/a.js").File("frontend/a.js", "x").End().Gzip(), "extended_header");
    }

    [Fact]
    public async Task AnExtendedHeaderWithoutAnEntryIsRefused() =>
        await InvalidAsync(new TarBuilder().Pax(("path", "frontend/a.js")).End().Gzip(), "extended_header");

    [Fact]
    public async Task AMalformedPaxRecordIsRefused()
    {
        var header = TarBuilder.Ustar("PaxHeaders/x", 'x', 12);
        await InvalidAsync(new TarBuilder().Entry(header, "99 path=x.js"u8.ToArray()).File("x.js", "x").End().Gzip(), "extended_header");
    }

    [Fact]
    public async Task AGnuLongLinkIsRefused() =>
        await InvalidAsync(new TarBuilder().Typed("././@LongLink", 'K', "target\0"u8.ToArray()).File("frontend/a.js", "x").End().Gzip(), "extended_header");

    [Theory]
    [InlineData("-0000001\0")]
    [InlineData("zzzzzzzzzzz\0")]
    [InlineData("00000000009\0")]
    [InlineData("\0\0\0\0\0\0\0\0\0\0\0\0")]
    [InlineData("1 2\0")]
    public async Task SizeFieldsThatAreNotPlainOctalAreRefused(string size)
    {
        var header = TarBuilder.Ustar("frontend/a.js", '0', 1);
        header.Set(124, 12, size);
        await InvalidAsync(new TarBuilder().Entry(header, "x"u8.ToArray()).End().Gzip(), "format");
    }

    [Fact]
    public async Task BaseTwoFiftySixSizesAreRefused()
    {
        var header = TarBuilder.Ustar("frontend/a.js", '0', 1);
        header.Set(124, 12, [0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]);
        await InvalidAsync(new TarBuilder().Entry(header, "x"u8.ToArray()).End().Gzip(), "format");
    }

    [Fact]
    public async Task AWrongChecksumIsRefused() =>
        await InvalidAsync(new TarBuilder().Entry(TarBuilder.Ustar("frontend/a.js", '0', 1), "x"u8.ToArray(), checksum: "0000001\0 ").End().Gzip(), "checksum");

    [Fact]
    public async Task AnArchiveWithoutTheUstarMagicIsRefused()
    {
        var header = TarBuilder.Ustar("frontend/a.js", '0', 1);
        header.Set(257, 8, new byte[8]);
        await InvalidAsync(new TarBuilder().Entry(header, "x"u8.ToArray()).End().Gzip(), "format");
    }

    [Fact]
    public async Task AFolderWithDataIsRefused()
    {
        var header = TarBuilder.Ustar("frontend/", '5', 1);
        await InvalidAsync(new TarBuilder().Entry(header, "x"u8.ToArray()).End().Gzip(), "format");
    }

    [Fact]
    public async Task ADeclaredSizeAboveTheLimitIsRefusedBeforeReading()
    {
        // The header claims 8 GB; nothing of it follows.
        var header = TarBuilder.Ustar("frontend/big.bin", '0', 0);
        header.Set(124, 12, "77777777777\0");
        await RefusedAsync(new TarBuilder().Entry(header, []).Gzip(), ErrorCodes.AppPackageTooLarge, "unpacked");
    }

    [Fact]
    public async Task UnpackedBytesAreCountedOverAllFiles()
    {
        var limits = PackageLimits.Default with { MaxUnpackedBytes = 1024 };
        var refused = await RefusedAsync(new TarBuilder().File("frontend/a.bin", new byte[600]).File("frontend/b.bin", new byte[600]).End().Gzip(), ErrorCodes.AppPackageTooLarge, "unpacked", limits);
        Assert.Equal(0L, refused.Parameters["limit"]);
    }

    [Fact]
    public async Task AGzipBombAfterTheEndStopsAtThePadding()
    {
        // 64 MB of zeros compress to about 64 KB; the reader stops once they exceed what a tar writer pads.
        var tar = new TarBuilder().File("frontend/a.js", "x").End().Raw(new byte[64 * 1024 * 1024]).Tar();
        await InvalidAsync(TarBuilder.Compress(tar), "trailing_data");
    }

    [Fact]
    public async Task TheCompressedSizeIsLimitedWhileReading()
    {
        var limits = PackageLimits.Default with { MaxCompressedBytes = 1024 * 1024 };
        var random = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
        await RefusedAsync(new TarBuilder().File("frontend/noise.bin", random).End().Gzip(), ErrorCodes.AppPackageTooLarge, "size", limits);
    }

    [Fact]
    public async Task TheNumberOfEntriesIsLimited()
    {
        var builder = new TarBuilder();
        for (var index = 0; index < 11; index++)
        {
            builder.File($"frontend/{index}.js", "x");
        }
        var refused = await RefusedAsync(builder.End().Gzip(), ErrorCodes.AppPackageTooLarge, "files", PackageLimits.Default with { MaxEntries = 10 });
        Assert.Equal(10L, refused.Parameters["limit"]);
    }

    [Fact]
    public async Task ALargeManifestIsRefused() =>
        await RefusedAsync(new TarBuilder().File("pithyx.json", new string(' ', 65 * 1024)).End().Gzip(), ErrorCodes.AppPackageTooLarge, "manifest");

    [Fact]
    public async Task TruncatedArchivesAreRefused()
    {
        var tar = TarBuilder.MinimalPackage().End(pad: false).Tar();
        // Inside a header, inside data, and before the end marker.
        foreach (var length in new[] { 700, 1100, tar.Length - 1024, tar.Length - 512 })
        {
            if (Directory.Exists(Staging))
            {
                Directory.Delete(Staging, recursive: true);
            }
            await InvalidAsync(TarBuilder.Compress(tar[..length]), "truncated");
        }
    }

    [Fact]
    public async Task ATruncatedGzipStreamIsRefused()
    {
        var package = TarBuilder.MinimalPackage().End().Gzip();
        foreach (var length in new[] { 5, 30, package.Length / 2, package.Length - 4 })
        {
            if (Directory.Exists(Staging))
            {
                Directory.Delete(Staging, recursive: true);
            }
            var refused = await Assert.ThrowsAsync<PithyxException>(() => UnpackAsync(package[..length]));
            Assert.Equal(ErrorCodes.AppPackageInvalid, refused.Code);
            Assert.Contains(refused.Parameters["reason"], new object[] { "gzip", "truncated" });
        }
    }

    [Fact]
    public async Task APlainTarIsNotAPackage() =>
        await InvalidAsync(TarBuilder.MinimalPackage().End().Tar(), "gzip");

    [Fact]
    public async Task DataAfterTheEndMarkerIsRefused() =>
        await InvalidAsync(new TarBuilder().File("frontend/a.js", "x").End(pad: false).Raw("hidden"u8.ToArray()).Gzip(), "trailing_data");

    [Fact]
    public async Task AnotherGzipMemberWithDataIsRefused()
    {
        var package = new TarBuilder().File("frontend/a.js", "x").End().Gzip();
        await InvalidAsync([.. package, .. TarBuilder.Compress("hidden"u8.ToArray())], "trailing_data");
    }

    [Fact]
    public async Task GarbageAfterTheGzipStreamIsRefused()
    {
        var package = new TarBuilder().File("frontend/a.js", "x").End().Gzip();
        var refused = await Assert.ThrowsAsync<PithyxException>(() => UnpackAsync([.. package, .. "garbage that is not gzip"u8.ToArray()]));
        Assert.Equal(ErrorCodes.AppPackageInvalid, refused.Code);
        Assert.Contains(refused.Parameters["reason"], new object[] { "gzip", "trailing_data" });
    }

    [Fact]
    public async Task ASingleZeroBlockIsNoEnd() =>
        await InvalidAsync(new TarBuilder().File("frontend/a.js", "x").End(zeroBlocks: 1, pad: false).Gzip(), "truncated");

    [Fact]
    public async Task AZeroBlockFollowedByAnEntryIsRefused() =>
        await InvalidAsync(new TarBuilder().Raw(new byte[512]).File("frontend/a.js", "x").End().Gzip(), "format");
}
