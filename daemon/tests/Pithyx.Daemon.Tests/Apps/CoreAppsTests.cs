using Pithyx.Daemon.Apps;
using Pithyx.Daemon.Apps.Manifest;

namespace Pithyx.Daemon.Tests.Apps;

public sealed class CoreAppsTests
{
    [Fact]
    public void EveryCoreAppHasAnIdOfD302InTheReservedNamespace()
    {
        Assert.All(CoreApps.All, id =>
        {
            Assert.True(AppId.IsValid(id), id);
            Assert.True(AppId.IsReserved(id), id);
        });
        Assert.Equal(CoreApps.All.Count, CoreApps.All.Distinct(StringComparer.Ordinal).Count());
        Assert.Subset(CoreApps.All.ToHashSet(), CoreApps.Granted.Concat(CoreApps.Admin).ToHashSet());
    }

    [Fact]
    public void TheShortIdsOfBeforeTheRenameNameTheSameApps()
    {
        Assert.Equal(CoreApps.Files, CoreApps.LegacyIds["files"]);
        Assert.Equal(CoreApps.Logs, CoreApps.LegacyIds["logs"]);
        Assert.Equal(CoreApps.Demo, CoreApps.LegacyIds["demo"]);
        Assert.Equal(CoreApps.All.Count, CoreApps.LegacyIds.Count);
        Assert.False(CoreApps.LegacyIds.ContainsKey("disks"));
    }
}
