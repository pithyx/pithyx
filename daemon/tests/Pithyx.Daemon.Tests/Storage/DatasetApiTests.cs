using System.Net;
using System.Net.Http.Json;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Storage;

/// <summary>The dataset and volume actions of <c>POST /api/storage/plans</c> in simulation mode, whose pools have only their roots.</summary>
public sealed class DatasetApiTests : IAsyncLifetime
{
    private readonly DaemonFactory _factory = new();
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync()
    {
        _admin = _factory.CreateApiClient();
        await _admin.SetupAdminAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<(HttpStatusCode Status, string Code)> RefusedAsync(object request)
    {
        using var response = await _admin.PostAsJsonAsync("/api/storage/plans", request, Token);
        return (response.StatusCode, await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task TheNewFieldsReachThePlanner()
    {
        Assert.Equal(
            (HttpStatusCode.Forbidden, "storage.dataset.protected"),
            await RefusedAsync(new { action = "dataset.destroy", pool = "pithyxtestm", dataset = "pithyxtestm", confirmName = "pithyxtestm" }));
        Assert.Equal(
            (HttpStatusCode.Forbidden, "storage.dataset.protected"),
            await RefusedAsync(new { action = "dataset.set", pool = "pithyxtestm", dataset = "pithyxtestm", quotaBytes = 1_073_741_824L, compression = "zstd" }));
        Assert.Equal(
            (HttpStatusCode.NotFound, "storage.dataset.not_found"),
            await RefusedAsync(new { action = "dataset.rename", pool = "pithyxtestm", dataset = "pithyxtestm/media", newName = "movies" }));
        Assert.Equal(
            (HttpStatusCode.Conflict, "storage.pool.not_prepared"),
            await RefusedAsync(new { action = "volume.create", pool = "pithyxtestm", name = "vm1", sizeBytes = 1_073_741_824L, sparse = true }));
    }
}
