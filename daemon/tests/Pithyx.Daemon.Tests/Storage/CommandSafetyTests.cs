using System.Text.RegularExpressions;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Storage;

/// <summary>
/// Pools are imported only by the import plans, never all at once, never destroyed ones, and changed only through a
/// command plan. The daemon's source may name destructive storage commands only in the plan builders and in the
/// installer's plans, which run only on the live system.
/// </summary>
public sealed partial class CommandSafetyTests
{
    private const string ImportPlans = "Storage/Zfs/ImportPlans.cs";
    private static readonly string[] _planBuilders = ["Storage/Zfs/ZfsPlans.cs", "Storage/Zfs/ZfsDatasetPlans.cs", "Storage/Zfs/SnapshotPlans.cs", ImportPlans, "Installer/InstallPlans.cs"];

    private static IEnumerable<(string File, string Text)> DaemonSources()
    {
        var root = Path.Combine(RepositoryFiles.Root, "daemon", "src", "Pithyx.Daemon");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(file => (Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText(file)));
    }

    [Fact]
    public void OnlyTheImportPlansImportAPool()
    {
        foreach (var (file, text) in DaemonSources().Where(source => source.File != ImportPlans))
        {
            Assert.False(ImportArgument().IsMatch(text), $"{file} names zpool import");
        }
    }

    [Fact]
    public void ImportsNeverTakeEveryPoolOrDestroyedOnesAndForceOnlyWithATypedName()
    {
        var text = DaemonSources().Single(source => source.File == ImportPlans).Text;

        Assert.DoesNotContain("\"-a\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"-D\"", text, StringComparison.Ordinal);
        // -f appears once, in the helper that needs the TypedName proof.
        Assert.Single(ForceFlag().Matches(text));
        Assert.Contains("private static string[] Force(TypedName? force) => force is null ? [] : [\"-f\"];", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyPlanBuildersNameDestructiveCommands()
    {
        foreach (var (file, text) in DaemonSources().Where(source => !_planBuilders.Contains(source.File)))
        {
            Assert.False(DestructiveArgument().IsMatch(text), $"{file} names a destructive storage command outside a plan builder");
        }
    }

    [Fact]
    public void WipefsRunsOutsidePlansOnlyWithNoAct()
    {
        foreach (var (file, text) in DaemonSources().Where(source => !_planBuilders.Contains(source.File)))
        {
            foreach (Match call in WipefsCall().Matches(text))
            {
                Assert.Contains("\"--no-act\"", call.Value, StringComparison.Ordinal);
            }
        }
    }

    // A string argument "import", or a command line that starts with zpool import.
    [GeneratedRegex("\"import\"|\"zpool import")]
    private static partial Regex ImportArgument();

    [GeneratedRegex("\"-f\"")]
    private static partial Regex ForceFlag();

    // A zpool or zfs command that changes a pool, or one with -f (only zpool events -f, which follows, is allowed);
    // and programs that write disks. Both new CommandRequest(...) and a target-typed new(...) count.
    [GeneratedRegex("(?:CommandRequest|new)\\(\\s*\"(zpool|zfs)\",\\s*\\[\\s*\"(labelclear|destroy|create|add|attach|detach|replace|split|remove|offline|online|clear|export|upgrade|rollback|set|snapshot|rename|promote)\"|CommandRequest\\(\\s*\"(zpool|zfs)\",\\s*\\[\\s*\"(?!events\")[^;]*\"-f\"|CommandRequest\\(\\s*\"(mkfs[.a-z0-9]*|sgdisk|sfdisk|parted|dd|mdadm|blkdiscard)\"")]
    private static partial Regex DestructiveArgument();

    [GeneratedRegex("\"wipefs\"[^;]*;")]
    private static partial Regex WipefsCall();
}
