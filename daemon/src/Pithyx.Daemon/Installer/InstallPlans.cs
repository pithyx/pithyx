using Pithyx.Daemon.Commands;
using Pithyx.Daemon.Storage;

namespace Pithyx.Daemon.Installer;

/// <summary>
/// The commands that write the system disks during an installation (System layout v0.1, D295), as pure functions. Like
/// the storage plan builders this is one of the few places that may name destructive commands
/// (<c>CommandSafetyTests</c>); only <see cref="SystemInstaller"/> runs them, and only on the live system.
/// </summary>
public static class InstallPlans
{
    public const string SystemArray = "/dev/md/pithyx-system";
    public const string SwapArray = "/dev/md/pithyx-swap";

    private static readonly TimeSpan _timeout = TimeSpan.FromMinutes(2);

    /// <summary>Arrays left from an earlier attempt or an old system; nothing of the live system runs on md.</summary>
    public static CommandRequest StopArrays() => new("mdadm", ["--stop", "--scan"], _timeout);

    /// <summary>The arrays for <c>mdadm.conf</c> of the installed system.</summary>
    public static CommandRequest ScanArrays() => new("mdadm", ["--detail", "--scan"], _timeout);

    /// <summary>
    /// Wipes every disk (its old partitions first) and gives it an ESP of 512 MiB, a 32 GiB member of the system array
    /// and a 4 GiB member of the swap array; then wipes the new partitions, since old signatures at the same offsets
    /// would otherwise come back inside them.
    /// </summary>
    public static IReadOnlyList<CommandRequest> Partition(IReadOnlyList<Disk> disks)
    {
        ArgumentNullException.ThrowIfNull(disks);
        var commands = new List<CommandRequest>();
        foreach (var disk in disks)
        {
            commands.AddRange(disk.Partitions.Select(partition => new CommandRequest("wipefs", ["--all", "--force", partition.Path], _timeout)));
            commands.Add(new CommandRequest("wipefs", ["--all", "--force", disk.Path], _timeout));
            commands.Add(new CommandRequest("sgdisk", ["--zap-all", disk.Path], _timeout));
            commands.Add(new CommandRequest("sgdisk",
            [
                "--new=1:1MiB:+512MiB", "--typecode=1:EF00", "--change-name=1:pithyx-esp",
                "--new=2:0:+32GiB", "--typecode=2:FD00", "--change-name=2:pithyx-system",
                "--new=3:0:+4GiB", "--typecode=3:FD00", "--change-name=3:pithyx-swap",
                disk.Path,
            ], _timeout));
        }
        commands.Add(new CommandRequest("udevadm", ["settle", "--timeout=30"], _timeout));
        commands.Add(new CommandRequest("wipefs", ["--all", "--force", .. Partitions(disks, 1), .. Partitions(disks, 2), .. Partitions(disks, 3)], _timeout));
        return commands;
    }

    /// <summary>The system and the swap RAID1. A single disk is a RAID1 with one member, so a second disk can join later (D295).</summary>
    public static IReadOnlyList<CommandRequest> CreateArrays(IReadOnlyList<Disk> disks)
    {
        ArgumentNullException.ThrowIfNull(disks);
        return [CreateArray(SystemArray, "pithyx-system", Partitions(disks, 2)), CreateArray(SwapArray, "pithyx-swap", Partitions(disks, 3))];
    }

    /// <summary>FAT32 on every ESP, btrfs on the system array and swap on the swap array.</summary>
    public static IReadOnlyList<CommandRequest> Format(IReadOnlyList<Disk> disks)
    {
        ArgumentNullException.ThrowIfNull(disks);
        return
        [
            .. Partitions(disks, 1).Select(esp => new CommandRequest("mkfs.fat", ["-F", "32", "-n", "PITHYX-EFI", esp], _timeout)),
            new CommandRequest("mkfs.btrfs", ["--force", "--label", "pithyx-system", SystemArray], _timeout),
            new CommandRequest("mkswap", ["--label", "pithyx-swap", SwapArray], _timeout),
        ];
    }

    /// <summary>Partition <paramref name="number"/> of every disk, in disk order.</summary>
    public static List<string> Partitions(IReadOnlyList<Disk> disks, int number) =>
        [.. disks.Select(disk => InstallerFacts.PartitionPath(disk.Path, number))];

    private static CommandRequest CreateArray(string device, string name, List<string> members)
    {
        List<string> arguments = ["--create", device, "--run", "--level=1", "--metadata=1.2", "--homehost=any", $"--name={name}", $"--raid-devices={members.Count}"];
        if (members.Count == 1)
        {
            arguments.Add("--force");
        }
        return new CommandRequest("mdadm", [.. arguments, .. members], _timeout);
    }
}
