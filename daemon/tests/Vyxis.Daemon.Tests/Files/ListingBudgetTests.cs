using System.Diagnostics;
using System.Net;
using Vyxis.Daemon.Tests.Support;
using static Vyxis.Daemon.Tests.Auth.AuthApi;

namespace Vyxis.Daemon.Tests.Files;

/// <summary>
/// The performance budget of step 6: the first page of a folder with 100,000 entries in under 200 ms, measured through
/// the API on a folder the daemon has not listed before (the kernel's caches are warm, as after any recent access).
/// </summary>
[Collection(TimingBudgets.Name)]
public sealed class ListingBudgetTests(ITestOutputHelper output) : IDisposable
{
    private const int Entries = 100_000;
    private static readonly TimeSpan _firstPageBudget = TimeSpan.FromMilliseconds(200);

    private readonly DaemonFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    // A new entry changes the folder, so the next listing reads it again.
    private static void Touch(string folder) =>
        File.OpenHandle(Path.Combine(folder, Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.Write).Dispose();

    [Fact]
    public async Task TheFirstPageOfAHugeFolderArrivesWithinBudget()
    {
#if DEBUG
        // Debug builds run the daemon without JIT optimizations; CI measures the Release build.
        Assert.Skip("Timing budgets are measured in Release builds (dotnet test -c Release), as in CI.");
#endif
        var root = Directory.CreateDirectory(Path.Combine(_factory.DataDirectory, "pool", "big")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(root, "photos")).FullName;
        var warmupFolder = Directory.CreateDirectory(Path.Combine(root, "warmup")).FullName;
        for (var i = 0; i < 5000; i++)
        {
            File.OpenHandle(Path.Combine(warmupFolder, $"w{i}"), FileMode.CreateNew, FileAccess.Write).Dispose();
        }
        for (var i = 0; i < Entries; i++)
        {
            File.OpenHandle(Path.Combine(folder, $"IMG_{i}.jpg"), FileMode.CreateNew, FileAccess.Write).Dispose();
        }

        using var client = _factory.CreateApiClient();
        await client.SetupAdminAsync();
        using var created = await client.PostAsync("/api/shares", new { name = "Big", path = root });
        var share = (await created.JsonAsync()).GetProperty("id").GetInt64();
        // A fresh process runs unoptimized code at first; a daemon that has been up has long compiled these paths.
        // Listing another folder a few times (each a new snapshot) gets them there.
        for (var round = 0; round < 20; round++)
        {
            Touch(warmupFolder);
            using var warmup = await client.GetAsync($"/api/files/{share}/list?path=warmup", Token);
            Assert.Equal(HttpStatusCode.OK, warmup.StatusCode);
        }
        await Task.Delay(500, Token);

        var watch = Stopwatch.StartNew();
        using var first = await client.GetAsync($"/api/files/{share}/list?path=photos", Token);
        var page = await first.JsonAsync();
        watch.Stop();
        var cold = watch.Elapsed;

        watch.Restart();
        var cursor = Uri.EscapeDataString(page.GetProperty("nextCursor").GetString()!);
        using var second = await client.GetAsync($"/api/files/{share}/list?path=photos&cursor={cursor}", Token);
        await second.JsonAsync();
        var warm = watch.Elapsed;

        watch.Restart();
        using var bySize = await client.GetAsync($"/api/files/{share}/list?path=photos&sort=size", Token);
        await bySize.JsonAsync();
        var sizeOrder = watch.Elapsed;

        output.WriteLine($"first page {cold.TotalMilliseconds:F0} ms, next page {warm.TotalMilliseconds:F0} ms, size order {sizeOrder.TotalMilliseconds:F0} ms");
        Assert.Equal(Entries, page.GetProperty("total").GetInt32());
        Assert.Equal("IMG_0.jpg", page.GetProperty("entries")[0].GetProperty("name").GetString());
        Assert.Equal("IMG_99.jpg", page.GetProperty("entries")[99].GetProperty("name").GetString());
        Assert.True(cold < _firstPageBudget, $"First page took {cold.TotalMilliseconds:F0} ms (budget {_firstPageBudget.TotalMilliseconds} ms)");
    }
}

/// <summary>Timing budgets run alone, never next to other test classes that share the CPU.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingBudgets
{
    public const string Name = "Budgets";
}
