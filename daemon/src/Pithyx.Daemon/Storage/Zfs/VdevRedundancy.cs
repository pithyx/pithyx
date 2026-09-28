using System.Text.RegularExpressions;

namespace Pithyx.Daemon.Storage.Zfs;

/// <param name="Kind"><c>single</c>, <c>mirror</c>, <c>raidz</c> or <c>draid</c>.</param>
/// <param name="Width">Number of devices.</param>
/// <param name="FaultTolerance">How many of its devices may fail: 0 for a single disk, n-1 for an n-way mirror, P for raidzP.</param>
public sealed record VdevShape(string Kind, int Width, int FaultTolerance);

/// <param name="Warnings">
/// Stable codes: <c>lowers_redundancy</c> (the new vdev tolerates fewer failed disks than the pool does now),
/// <c>replication_mismatch</c> (a different layout that ZFS refuses without an override, without lowering the
/// redundancy), <c>irreversible</c> (the vdev can never be removed again) and <c>no_redundancy</c> (a single disk that
/// takes the whole pool with it when it fails, where the pool had no redundancy before either).
/// </param>
/// <param name="ReplicationMismatch">True when <c>zpool add</c> needs <c>--allow-replication-mismatch</c>.</param>
/// <param name="RequiresConfirmName">True when the pool name must be typed.</param>
public sealed record AddVdevAssessment(IReadOnlyList<string> Warnings, bool ReplicationMismatch, bool RequiresConfirmName);

/// <summary>What adding a data vdev does to a pool's redundancy, decided the way <c>zpool add</c> checks it.</summary>
public static partial class VdevRedundancy
{
    public static VdevShape Shape(string layout, int diskCount) => layout switch
    {
        "mirror" => new VdevShape("mirror", diskCount, diskCount - 1),
        "raidz1" or "raidz2" or "raidz3" => new VdevShape("raidz", diskCount, layout[^1] - '0'),
        _ => new VdevShape("single", diskCount, 0),
    };

    /// <summary>The shape of an existing top-level vdev; null for vdevs that hold no data (removed devices, holes).</summary>
    public static VdevShape? Shape(Vdev vdev)
    {
        ArgumentNullException.ThrowIfNull(vdev);
        return vdev.Type switch
        {
            "indirect" or "hole" => null,
            "mirror" => new VdevShape("mirror", vdev.Children.Count, vdev.Children.Count - 1),
            "raidz" => new VdevShape("raidz", vdev.Children.Count, vdev.ParityDisks ?? 1),
            "draid" => new VdevShape("draid", vdev.Children.Count, DraidParity().Match(vdev.Name) is { Success: true } match ? match.Groups[1].Value[0] - '0' : 1),
            _ => new VdevShape("single", 1, 0),
        };
    }

    /// <summary>The data vdevs of a pool with their shape.</summary>
    public static IReadOnlyList<VdevShape> DataVdevs(Pool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        return [.. pool.Vdevs.Where(vdev => vdev.Role == "data").Select(Shape).OfType<VdevShape>()];
    }

    /// <summary>
    /// Whether <c>zpool add</c> refuses the pair as mismatched replication (checked on file vdevs with OpenZFS 2.3.9):
    /// single disks only go with single disks, mirrors with mirrors of the same width, raidz with raidz of the same
    /// parity and width, and a mirror with a raidz when both tolerate the same number of failed disks.
    /// </summary>
    public static bool Mismatch(VdevShape existing, VdevShape added)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(added);
        return (existing.Kind, added.Kind) switch
        {
            ("single", "single") => false,
            ("single", _) or (_, "single") => true,
            ("mirror", "mirror") => existing.Width != added.Width,
            ("raidz", "raidz") or ("draid", "draid") => existing.FaultTolerance != added.FaultTolerance || existing.Width != added.Width,
            ("mirror", "raidz") or ("raidz", "mirror") => existing.FaultTolerance != added.FaultTolerance,
            _ => true,
        };
    }

    /// <summary>
    /// What adding a vdev of <paramref name="layout"/> with <paramref name="diskCount"/> disks means. The pool tolerates
    /// as many failed disks as its weakest data vdev; a data vdev can be removed again only while no data vdev is raidz
    /// or draid (<c>zpool remove</c>).
    /// </summary>
    public static AddVdevAssessment AssessAdd(Pool pool, string layout, int diskCount)
    {
        var existing = DataVdevs(pool);
        var added = Shape(layout, diskCount);
        var warnings = new List<string>();
        var mismatch = existing.Any(vdev => Mismatch(vdev, added));
        var lowers = existing.Count > 0 && added.FaultTolerance < existing.Min(vdev => vdev.FaultTolerance);
        if (lowers)
        {
            warnings.Add("lowers_redundancy");
        }
        else if (mismatch)
        {
            warnings.Add("replication_mismatch");
        }
        var irreversible = added.Kind == "raidz" || existing.Any(vdev => vdev.Kind is "raidz" or "draid");
        if (irreversible)
        {
            warnings.Add("irreversible");
        }
        if (!lowers && added.FaultTolerance == 0)
        {
            warnings.Add("no_redundancy");
        }
        return new AddVdevAssessment(warnings, mismatch, mismatch || irreversible);
    }

    [GeneratedRegex("^draid([1-3])")]
    private static partial Regex DraidParity();
}
