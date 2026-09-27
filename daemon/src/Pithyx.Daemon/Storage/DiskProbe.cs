using System.Text.Json;
using Pithyx.Daemon.Commands;

namespace Pithyx.Daemon.Storage;

/// <summary>Signatures found on a whole disk: partition tables, file systems, RAID superblocks, ZFS labels.</summary>
/// <param name="Types">Signature types, sorted, for example <c>gpt</c>, <c>PMBR</c>, <c>zfs_member</c> or <c>linux_raid_member</c>.</param>
public sealed record DiskSignatures(IReadOnlyList<string> Types);

/// <summary>
/// Looks for signatures on a disk with <c>wipefs --no-act</c> and <c>blkid -p</c>. Both only read. They find what lsblk
/// misses: a partition table without partitions, an mdadm superblock or ZFS labels at the end of a disk. They run when a
/// disk appears or changes and right before a plan, never for a read request, and never on members of an imported pool,
/// so a sleeping disk is not woken.
/// </summary>
public sealed class DiskProbe(ICommandRunner commands)
{
    public static CommandRequest Wipefs(string path) => new("wipefs", ["--no-act", "--json", path]);

    public static CommandRequest Blkid(string path) => new("blkid", ["-p", "-o", "export", path]);

    public async Task<DiskSignatures> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var wipefs = await commands.RunAsync(Wipefs(path), cancellationToken);
        var blkid = await commands.RunAsync(Blkid(path), cancellationToken);
        // blkid exits 2 when it finds nothing; any other failure means the disk could not be read.
        if (wipefs.ExitCode != 0 || blkid.ExitCode is not (0 or 2))
        {
            throw new IOException($"Probing {path} failed: wipefs {wipefs.ExitCode} {wipefs.StandardError.Trim()}, blkid {blkid.ExitCode} {blkid.StandardError.Trim()}");
        }
        return new DiskSignatures([.. ParseWipefs(wipefs.StandardOutput).Concat(ParseBlkid(blkid.StandardOutput)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }

    /// <summary>Signature types from <c>wipefs --no-act --json</c>, which prints nothing for a blank disk.</summary>
    public static IEnumerable<string> ParseWipefs(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("signatures", out var signatures)
            ? [.. signatures.EnumerateArray().Select(s => s.TryGetProperty("type", out var type) ? type.GetString() : null).OfType<string>()]
            : [];
    }

    /// <summary>Signature types from <c>blkid -p -o export</c>: the content type and the partition table type.</summary>
    public static IEnumerable<string> ParseBlkid(string export) =>
        export.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0] is "TYPE" or "PTTYPE" && pair[1].Length > 0)
            .Select(pair => pair[1]);
}
