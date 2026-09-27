using System.Text.RegularExpressions;
using Pithyx.Daemon.Tests.Support;

namespace Pithyx.Daemon.Tests.Storage;

/// <summary>
/// Pools are never imported, never forced and never changed outside a command plan. The daemon's source may name
/// destructive storage commands only in the plan builders.
/// </summary>
public sealed partial class CommandSafetyTests
{
    private static readonly string[] _planBuilders = ["Storage/Zfs/ZfsPlans.cs"];

    private static IEnumerable<(string File, string Text)> DaemonSources()
    {
        var root = Path.Combine(RepositoryFiles.Root, "daemon", "src", "Pithyx.Daemon");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(file => (Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText(file)));
    }

    [Fact]
    public void NoSourceImportsAPool()
    {
        foreach (var (file, text) in DaemonSources())
        {
            Assert.False(ImportArgument().IsMatch(text), $"{file} names zpool import");
        }
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

    [GeneratedRegex("\"import\"|zpool import")]
    private static partial Regex ImportArgument();

    // A zpool or zfs command that changes a pool, or one with -f (only zpool events -f, which follows, is allowed);
    // and programs that write disks.
    [GeneratedRegex("CommandRequest\\(\\s*\"(zpool|zfs)\",\\s*\\[\\s*\"(labelclear|destroy|create|add|attach|detach|replace|split|remove|offline|online|clear|export|upgrade|rollback|set|snapshot|rename|promote)\"|CommandRequest\\(\\s*\"(zpool|zfs)\",\\s*\\[\\s*\"(?!events\")[^;]*\"-f\"|CommandRequest\\(\\s*\"(mkfs[.a-z0-9]*|sgdisk|sfdisk|parted|dd|mdadm|blkdiscard)\"")]
    private static partial Regex DestructiveArgument();

    [GeneratedRegex("\"wipefs\"[^;]*;")]
    private static partial Regex WipefsCall();
}
