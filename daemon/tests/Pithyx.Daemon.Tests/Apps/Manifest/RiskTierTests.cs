using Pithyx.Daemon.Apps.Catalog;
using Pithyx.Daemon.Apps.Manifest;

namespace Pithyx.Daemon.Tests.Apps.Manifest;

/// <summary>
/// The risk tiers of elevated rights (D388) on the shared corpus <c>sdk/schema/fixtures/tiers</c>, and what a critical
/// right does to the update policy.
/// </summary>
public sealed class RiskTierTests
{
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var entry in Corpus.Json("tiers", "cases.json").GetProperty("cases").EnumerateObject())
        {
            data.Add(entry.Name, entry.Value.GetString()!);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EachRightHasTheTierOfTheCorpus(string right, string tier) =>
        Assert.Equal(Enum.Parse<RiskTier>(tier, ignoreCase: true), ElevatedRights.TierOf(right));

    [Theory]
    [InlineData(UpdatePolicy.Automatic, 0, false, false, true)]
    [InlineData(UpdatePolicy.Automatic, 0, false, true, false)]
    [InlineData(UpdatePolicy.Automatic, 1, false, false, false)]
    [InlineData(UpdatePolicy.Automatic, 0, true, false, false)]
    [InlineData(UpdatePolicy.Notify, 0, false, false, false)]
    [InlineData(UpdatePolicy.None, 0, false, false, false)]
    public void AnAppWithACriticalRightNeverUpdatesByItself(UpdatePolicy policy, int addedPermissions, bool elevated, bool critical, bool automatic) =>
        Assert.Equal(automatic, StoreService.UpdatesItself(policy, addedPermissions, elevated, critical));
}
