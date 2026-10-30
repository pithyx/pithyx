using System.Globalization;
using System.Text.Json;

namespace Pithyx.Daemon.Storage;

/// <summary>
/// Parses <c>lsblk -J -b -O</c>. Only whole disks are returned; optical drives, loop, RAM and network block devices
/// (<c>nbd</c>, which the kernel module creates by the dozen, empty until a client connects) are not.
/// </summary>
public static class LsblkParser
{
    public static IReadOnlyList<Disk> Parse(string json) => ParseWithDevices(json).Select(entry => entry.Disk).ToList();

    /// <summary>The disks, each with its device tree for the disk guard.</summary>
    public static IReadOnlyList<(Disk Disk, BlockDevice Device)> ParseWithDevices(string json)
    {
        using var document = JsonDocument.Parse(json);
        var disks = new List<(Disk, BlockDevice)>();
        foreach (var device in document.RootElement.GetProperty("blockdevices").EnumerateArray())
        {
            var name = Text(device, "name") ?? "";
            if (Text(device, "type") != "disk" || name.StartsWith("zram", StringComparison.Ordinal) || name.StartsWith("ram", StringComparison.Ordinal)
                || name.StartsWith("nbd", StringComparison.Ordinal))
            {
                continue;
            }

            var path = Text(device, "path") ?? $"/dev/{name}";
            var serial = Text(device, "serial");
            var wwn = Text(device, "wwn");
            var partitions = device.TryGetProperty("children", out var children)
                ? children.EnumerateArray().Where(child => Text(child, "type") == "part").Select(ParsePartition).ToList()
                : [];

            disks.Add((new Disk(
                wwn is not null ? $"wwn-{wwn}" : serial is not null ? $"serial-{serial}" : $"path-{path}",
                name,
                path,
                serial,
                wwn,
                Text(device, "model"),
                Text(device, "vendor"),
                Number(device, "size"),
                Flag(device, "rota"),
                Text(device, "tran"),
                Flag(device, "rm"),
                Flag(device, "ro"),
                partitions), ParseDevice(device)));
        }
        return disks;
    }

    private static BlockDevice ParseDevice(JsonElement device)
    {
        var name = Text(device, "name") ?? "";
        return new BlockDevice(
            name,
            Text(device, "path") ?? $"/dev/{name}",
            Text(device, "id-link"),
            Text(device, "maj:min") ?? "",
            Text(device, "type") ?? "",
            Text(device, "fstype"),
            MountPoints(device),
            device.TryGetProperty("children", out var children) ? [.. children.EnumerateArray().Select(ParseDevice)] : []);
    }

    private static List<string> MountPoints(JsonElement device) =>
        device.TryGetProperty("mountpoints", out var points) && points.ValueKind == JsonValueKind.Array
            ? [.. points.EnumerateArray().Select(p => p.GetString()).OfType<string>()]
            : [];

    private static Partition ParsePartition(JsonElement partition)
    {
        var name = Text(partition, "name") ?? "";
        return new Partition(name, Text(partition, "path") ?? $"/dev/{name}", Number(partition, "size"), Text(partition, "fstype"), Text(partition, "label"), MountPoints(partition));
    }

    // lsblk pads some strings (vendor, model) with spaces and uses null for missing values.
    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 0 } text
            ? text
            : null;

    // Older util-linux versions print numbers as strings.
    private static long Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) switch
        {
            false => 0,
            true when value.ValueKind == JsonValueKind.Number => value.GetInt64(),
            true when value.ValueKind == JsonValueKind.String => long.Parse(value.GetString()!, CultureInfo.InvariantCulture),
            _ => 0,
        };

    // Booleans are true/false in current util-linux and "0"/"1" in older versions.
    private static bool Flag(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => value.GetString() == "1",
            JsonValueKind.Number => value.GetInt32() == 1,
            _ => false,
        };
}
