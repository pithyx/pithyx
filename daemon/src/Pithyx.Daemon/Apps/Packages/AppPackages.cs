using System.Security.Cryptography;
using System.Text.Json;
using Pithyx.Daemon.Apps.Compose;
using Pithyx.Daemon.Apps.Docker;
using Pithyx.Daemon.Apps.Manifest;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Hosting;

namespace Pithyx.Daemon.Apps.Packages;

/// <summary>Where a package comes from when it is not a sideload: a catalog's verified copy and what its index says.</summary>
/// <param name="Catalog">The catalog's id.</param>
/// <param name="AllowReservedId">The official catalog may ship <c>org.pithyx.*</c>.</param>
/// <param name="AppId">The app id the index lists the package under.</param>
/// <param name="Version">The version it lists it under.</param>
/// <param name="PackageSha256">The SHA-256 of the package file in the index.</param>
/// <param name="ManifestSha256">The SHA-256 of its <c>pithyx.json</c> in the index.</param>
/// <param name="Images">The index's images (reference to architecture to manifest and config digest), null for no backend.</param>
public sealed record PackageOrigin(
    string Catalog,
    bool AllowReservedId,
    string AppId,
    string Version,
    string PackageSha256,
    string ManifestSha256,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, LockedImage>>? Images);

/// <summary>An uploaded package that passed every check, waiting for its installation.</summary>
/// <param name="Id">The package id the install names.</param>
/// <param name="UserId">The administrator who uploaded it; only they can install it.</param>
/// <param name="ExpiresAt">When it is discarded.</param>
/// <param name="Package">Its files in the staging folder.</param>
/// <param name="Manifest">The checked manifest.</param>
/// <param name="Icon">The checked icon.</param>
/// <param name="Origin">The catalog it comes from; null for a sideload.</param>
public sealed record CheckedPackage(string Id, long UserId, DateTimeOffset ExpiresAt, UnpackedPackage Package, JsonElement Manifest, byte[] Icon, PackageOrigin? Origin = null)
{
    public string AppId => Manifest.GetProperty("id").GetString()!;

    public string Version => Manifest.GetProperty("version").GetString()!;

    public IReadOnlyList<string> Permissions => [.. Manifest.GetProperty("permissions").EnumerateArray().Select(permission => permission.GetString()!)];
}

/// <summary>
/// Uploaded packages between their check and their installation: unpacked by <see cref="PackageReader"/> into the
/// rebuildable cache (<c>/var/cache/pithyx/app-packages</c>), checked (manifest, icon, layout; sideloads never under
/// <c>org.pithyx.*</c>), and kept for <see cref="Lifetime"/> for the administrator who uploaded them. At most
/// <see cref="MaxPending"/> wait at once; a new upload discards the oldest. A restart discards them all.
/// </summary>
public sealed class AppPackages
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public const int MaxPending = 3;

    private readonly Lock _lock = new();
    private readonly List<CheckedPackage> _pending = [];
    private readonly string _directory;
    private readonly TimeProvider _time;

    public AppPackages(DaemonPaths paths, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _directory = Path.Combine(paths.CacheDirectory, "app-packages");
        _time = time;
        // Nothing survives a restart; leftovers of an earlier run go.
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    public PackageLimits Limits { get; init; } = PackageLimits.Default;

    /// <summary>
    /// Unpacks and checks an upload, or a package of a catalog's copy (<paramref name="origin"/>), which must also be
    /// exactly what the catalog's index names; throws the first problem and keeps nothing then.
    /// </summary>
    public async Task<CheckedPackage> UploadAsync(long userId, Stream body, CancellationToken cancellationToken, PackageOrigin? origin = null)
    {
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var staging = Path.Combine(_directory, id);
        try
        {
            var unpacked = await PackageReader.UnpackAsync(body, staging, Limits, cancellationToken);
            if (origin is not null && unpacked.Sha256 != origin.PackageSha256)
            {
                throw HashMismatch($"apps/{origin.AppId}/{origin.Version}/{origin.AppId}-{origin.Version}.pithyxapp");
            }
            var (manifest, icon) = Check(unpacked, origin);
            var package = new CheckedPackage(id, userId, _time.GetUtcNow() + Lifetime, unpacked, manifest, icon, origin);
            List<CheckedPackage> evicted;
            lock (_lock)
            {
                evicted = Expired();
                _pending.Add(package);
                while (_pending.Count > MaxPending)
                {
                    evicted.Add(_pending[0]);
                    _pending.RemoveAt(0);
                }
            }
            evicted.ForEach(Delete);
            return package;
        }
        catch
        {
            DeleteFolder(staging);
            throw;
        }
    }

    /// <summary>A waiting package of the user; <see cref="ErrorCodes.AppPackageNotFound"/> when it is gone or someone else's.</summary>
    public CheckedPackage Get(long userId, string packageId)
    {
        List<CheckedPackage> expired;
        CheckedPackage? found;
        lock (_lock)
        {
            expired = Expired();
            found = _pending.Find(package => package.Id == packageId && package.UserId == userId);
        }
        expired.ForEach(Delete);
        return found ?? throw new PithyxException(ErrorCodes.AppPackageNotFound);
    }

    /// <summary>Takes a waiting package out for its installation; the caller deletes it with <see cref="Delete"/> afterwards.</summary>
    public CheckedPackage Take(long userId, string packageId)
    {
        var package = Get(userId, packageId);
        lock (_lock)
        {
            if (!_pending.Remove(package))
            {
                throw new PithyxException(ErrorCodes.AppPackageNotFound);
            }
        }
        return package;
    }

    public void Discard(long userId, string packageId) => Delete(Take(userId, packageId));

    public void Delete(CheckedPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        DeleteFolder(package.Package.Directory);
    }

    /// <summary>
    /// The checks of a package: the manifest (never <c>org.pithyx.*</c> but from the official catalog), the icon, the
    /// layout, and for a backend its Compose file and images lock. A catalog's package must also be the app, version,
    /// manifest and images its index names. The first problem is thrown.
    /// </summary>
    public static (JsonElement Manifest, byte[] Icon) Check(UnpackedPackage package, PackageOrigin? origin = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        var paths = package.Files.Select(file => file.Path).ToList();
        if (!paths.Contains(PackageLayout.ManifestFile))
        {
            throw Layout(PackageLayout.ManifestFile);
        }
        var manifestBytes = File.ReadAllBytes(Path.Combine(package.Directory, PackageLayout.ManifestFile));
        var validation = ManifestValidator.Validate(manifestBytes, allowReservedId: origin?.AllowReservedId == true);
        if (!validation.IsValid)
        {
            throw validation.Issues[0].ToException();
        }
        var manifest = validation.Manifest!.Value;
        if (origin is not null
            && (manifest.GetProperty("id").GetString() != origin.AppId
                || manifest.GetProperty("version").GetString() != origin.Version
                || Convert.ToHexStringLower(SHA256.HashData(manifestBytes)) != origin.ManifestSha256))
        {
            throw HashMismatch($"apps/{origin.AppId}/{origin.Version}/{PackageLayout.ManifestFile}");
        }
        var compose = manifest.TryGetProperty("backend", out var backend) ? backend.GetProperty("compose").GetString() : null;
        var layout = PackageLayout.Check(paths, manifest.GetProperty("entry").GetString()!, compose);
        if (layout.Count > 0)
        {
            throw layout[0].ToException();
        }
        if (compose is not null)
        {
            CheckBackend(package, manifest, compose, paths);
        }
        if (origin is not null && !SameImages(package, compose, origin))
        {
            throw HashMismatch($"apps/{origin.AppId}/{origin.Version}/{ImagesLock.FileName}");
        }
        var icon = File.ReadAllBytes(Path.Combine(package.Directory, PackageLayout.IconFile));
        if (IconValidator.Validate(icon) is [var problem, ..])
        {
            throw problem.ToException();
        }
        return (manifest, icon);
    }

    /// <summary>
    /// The Compose file against the allowlist and the manifest (D310), and the images lock (D315): it lists exactly the
    /// file's images, each with an image for this box's architecture.
    /// </summary>
    private static void CheckBackend(UnpackedPackage package, JsonElement manifest, string compose, List<string> paths)
    {
        var validation = ComposeValidator.Validate(File.ReadAllBytes(Path.Combine(package.Directory, compose)), manifest);
        if (!validation.IsValid)
        {
            throw validation.Issues[0].ToException();
        }
        if (!paths.Contains(ImagesLock.FileName))
        {
            throw ImagesLock.Invalid("missing");
        }
        var lockFile = ImagesLock.Parse(File.ReadAllBytes(Path.Combine(package.Directory, ImagesLock.FileName))) ?? throw ImagesLock.Invalid("invalid");
        var images = ((ComposeMap)validation.Document!.Get("services")!).Entries
            .Select(service => ((ComposeScalar)((ComposeMap)service.Value).Get("image")!).Value)
            .ToHashSet(StringComparer.Ordinal);
        if (!images.SetEquals(lockFile.Images.Keys))
        {
            throw ImagesLock.Invalid("stale");
        }
        var architecture = ImagesLock.HostArchitecture ?? "";
        if (lockFile.Images.Values.Any(platforms => !platforms.ContainsKey(architecture)))
        {
            throw ImagesLock.Invalid("platform");
        }
    }

    /// <summary>Whether the package's images lock names exactly the images of the catalog's index.</summary>
    private static bool SameImages(UnpackedPackage package, string? compose, PackageOrigin origin)
    {
        if (compose is null || origin.Images is null)
        {
            return compose is null && origin.Images is null;
        }
        var lockFile = ImagesLock.Parse(File.ReadAllBytes(Path.Combine(package.Directory, ImagesLock.FileName)));
        if (lockFile is null || lockFile.Images.Count != origin.Images.Count)
        {
            return false;
        }
        foreach (var (reference, platforms) in lockFile.Images)
        {
            if (!origin.Images.TryGetValue(reference, out var indexed) || indexed.Count != platforms.Count)
            {
                return false;
            }
            foreach (var (architecture, platform) in platforms)
            {
                if (!indexed.TryGetValue(architecture, out var expected) || expected.Manifest != platform.Manifest || expected.Config != platform.Config)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static PithyxException HashMismatch(string path) =>
        new(ErrorCodes.StorePackageHashMismatch, new Dictionary<string, object?> { ["path"] = path });

    // Callers hold _lock.
    private List<CheckedPackage> Expired()
    {
        var now = _time.GetUtcNow();
        var expired = _pending.Where(package => package.ExpiresAt <= now).ToList();
        _pending.RemoveAll(expired.Contains);
        return expired;
    }

    private static void DeleteFolder(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static PithyxException Layout(string path) =>
        new(ErrorCodes.AppPackageLayoutInvalid, new Dictionary<string, object?> { ["path"] = path, ["reason"] = "missing" });
}
