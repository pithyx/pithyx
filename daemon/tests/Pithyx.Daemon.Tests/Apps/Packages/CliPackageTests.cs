using System.Security.Cryptography;
using Pithyx.Daemon.Apps.Packages;
using Pithyx.Daemon.Tests.Apps.Manifest;

namespace Pithyx.Daemon.Tests.Apps.Packages;

/// <summary>
/// A package written by <c>pithyx pack</c> (<c>sdk/schema/fixtures/package</c>; the Vitest suite of <c>@pithyx/cli</c>
/// checks that packing the project next to it gives the same archive): the box unpacks it, accepts it, and its
/// <c>CHECKSUMS</c> match the files.
/// </summary>
public sealed class CliPackageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pithyx-cli-package-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APackageOfTheCliIsAccepted()
    {
        await using var file = File.OpenRead(Corpus.Directory("package", "com.example.package-fixture-1.0.0.pithyxapp"));
        var unpacked = await PackageReader.UnpackAsync(file, Path.Combine(_root, "staging"), PackageLimits.Default, Token);

        var (manifest, _) = AppPackages.Check(unpacked);

        Assert.Equal("com.example.package-fixture", manifest.GetProperty("id").GetString());
        var paths = unpacked.Files.Select(entry => entry.Path).ToList();
        Assert.Equal(["pithyx.json", "icon.svg"], paths.Take(2));
        Assert.Equal([.. paths.Skip(2).Order(StringComparer.Ordinal)], paths.Skip(2));
        Assert.Contains("frontend/assets/" + new string('a', 120) + ".txt", paths);
        Assert.Contains("frontend/assets/folder-with-a-rather-long-name-number-one/folder-with-a-rather-long-name-number-two/prefixed.txt", paths);

        var listed = (await File.ReadAllLinesAsync(Path.Combine(unpacked.Directory, "CHECKSUMS"), Token))
            .Select(line => line.Split("  ", 2))
            .ToDictionary(parts => parts[1], parts => parts[0]);
        Assert.Equal(paths.Where(path => path != "CHECKSUMS").Order(StringComparer.Ordinal), listed.Keys);
        foreach (var (path, digest) in listed)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(unpacked.Directory, path), Token);
            Assert.Equal(digest, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
    }
}
