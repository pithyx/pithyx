using Pithyx.Daemon.Files;

namespace Pithyx.Daemon.Tests.Files;

public sealed class FolderSnapshotTests
{
    private static FolderSnapshot Snapshot(IEnumerable<string> names) =>
        new("1:2", DateTimeOffset.UnixEpoch, [.. names.Select(name => (name, FsKind.File))], DateTimeOffset.UnixEpoch);

    private static List<string> Expected(IEnumerable<string> names, SortOrder order)
    {
        var sorted = names.Order(Comparer<string>.Create(NaturalSort.Compare)).ToList();
        if (order == SortOrder.Desc)
        {
            sorted.Reverse();
        }
        return sorted;
    }

    [Theory]
    [InlineData(SortOrder.Asc, 101)]
    [InlineData(SortOrder.Desc, 101)]
    [InlineData(SortOrder.Asc, 5000)]
    [InlineData(SortOrder.Desc, 1)]
    public void TheFirstPageIsTheStartOfTheFullOrder(SortOrder order, int count)
    {
        var random = new Random(7);
        var names = Enumerable.Range(0, 3000).Select(_ => $"{(char)('a' + random.Next(3))}file{random.Next(10_000)}.{(random.Next(2) == 0 ? "JPG" : "jpg")}").Distinct().ToList();

        var first = Snapshot(names).ByName(null, order, count).Select(entry => entry.Name).ToList();

        Assert.Equal(Expected(names, order).Take(count), first);
    }

    [Theory]
    [InlineData(SortOrder.Asc)]
    [InlineData(SortOrder.Desc)]
    public void PagesAfterTheFirstContinueWhereItEnded(SortOrder order)
    {
        var names = Enumerable.Range(0, 1000).Select(i => $"IMG_{i}.jpg").Reverse().ToList();
        var snapshot = Snapshot(names);

        var seen = new List<string>();
        string? after = null;
        while (true)
        {
            var page = snapshot.ByName(after, order, 64).ToList();
            if (page.Count == 0)
            {
                break;
            }
            seen.AddRange(page.Select(entry => entry.Name));
            after = page[^1].Key;
        }

        Assert.Equal(Expected(names, order), seen);
    }

    [Fact]
    public void AnEmptyFolderHasAnEmptyFirstPage()
    {
        Assert.Empty(Snapshot([]).ByName(null, SortOrder.Asc, 100));
    }
}
