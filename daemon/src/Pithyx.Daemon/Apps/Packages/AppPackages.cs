using System.Security.Cryptography;
using System.Text.Json;
using Pithyx.Daemon.Apps.Compose;
using Pithyx.Daemon.Apps.Docker;
using Pithyx.Daemon.Apps.Manifest;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Hosting;

namespace Pithyx.Daemon.Apps.Packages;

/// <summary>An uploaded package that passed every check, waiting for its installation.</summary>
/// <param name="Id">The package id the install names.</param>
/// <param name="UserId">The administrator who uploaded it; only they can install it.</param>
/// <param name="ExpiresAt">When it is discarded.</param>
/// <param name="Package">Its files in the staging folder.</param>
/// <param name="Manifest">The checked manifest.</param>
/// <param name="Icon">The checked icon.</param>
public sealed record CheckedPackage(string Id, long UserId, DateTimeOffset ExpiresAt, UnpackedPackage Package, JsonElement Manifest, byte[] Icon)
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

    /// <summary>Unpacks and checks an upload; throws the first problem and keeps nothing then.</summary>
    public async Task<CheckedPackage> UploadAsync(long userId, Stream body, CancellationToken cancellationToken)
    {
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var staging = Path.Combine(_directory, id);
        try
        {
            var unpacked = await PackageReader.UnpackAsync(body, staging, Limits, cancellationToken);
            var (manifest, icon) = Check(unpacked);
            var package = new CheckedPackage(id, userId, _time.GetUtcNow() + Lifetime, unpacked, manifest, icon);
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
    /// The checks of a sideload: the manifest (never <c>org.pithyx.*</c>), the icon, the layout, and for a backend its
    /// Compose file and images lock. The first problem is thrown.
    /// </summary>
    public static (JsonElement Manifest, byte[] Icon) Check(UnpackedPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var paths = package.Files.Select(file => file.Path).ToList();
        if (!paths.Contains(PackageLayout.ManifestFile))
        {
            throw Layout(PackageLayout.ManifestFile);
        }
        var validation = ManifestValidator.Validate(File.ReadAllBytes(Path.Combine(package.Directory, PackageLayout.ManifestFile)), allowReservedId: false);
        if (!validation.IsValid)
        {
            throw validation.Issues[0].ToException();
        }
        var manifest = validation.Manifest!.Value;
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
