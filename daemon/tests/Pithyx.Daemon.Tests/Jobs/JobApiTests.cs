using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Tests.Auth;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;
using static Pithyx.Daemon.Tests.Jobs.JobTestSupport;

namespace Pithyx.Daemon.Tests.Jobs;

public sealed class JobApiTests : IAsyncLifetime
{
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _admin;
    private HttpClient _bob = null!;
    private HttpClient _carol = null!;
    private JobService _jobs = null!;
    private long _adminId;
    private long _bobId;

    public JobApiTests() => _admin = _factory.CreateApiClient();

    public async ValueTask InitializeAsync()
    {
        await _admin.SetupAdminAsync();
        _jobs = _factory.Services.GetRequiredService<JobService>();
        _adminId = await _factory.ScalarAsync<long>("SELECT id FROM users WHERE username = 'admin'");
        _bobId = await _factory.InsertUserAsync("bob");
        _bob = _factory.ClientWithCookie(await _factory.SessionCookieAsync(_bobId));
        _carol = _factory.ClientWithCookie(await _factory.SessionCookieAsync(await _factory.InsertUserAsync("carol")));
    }

    public ValueTask DisposeAsync()
    {
        _admin.Dispose();
        _bob.Dispose();
        _carol.Dispose();
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }

    private Task<Job> StartAsync(long userId, Func<JobContext, Task>? work = null) =>
        _jobs.StartAsync(userId, "test.work", new { path = "/tank/a" }, work ?? (_ => Task.CompletedTask), Token);

    [Fact]
    public async Task TheOwnerReadsAndListsTheirJobsNewestFirst()
    {
        var older = await StartAsync(_bobId);
        var newer = await StartAsync(_bobId);
        await _jobs.WaitForStateAsync(newer.Id, JobState.Succeeded);
        await StartAsync(_adminId);

        using var get = await _bob.GetAsync($"/api/jobs/{newer.Id}", Token);
        using var list = await _bob.GetAsync("/api/jobs", Token);
        var job = await get.JsonAsync();
        var jobs = await list.JsonAsync();

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("succeeded", job.GetProperty("state").GetString());
        Assert.Equal("test.work", job.GetProperty("kind").GetString());
        Assert.Equal("/tank/a", job.GetProperty("parameters").GetProperty("path").GetString());
        Assert.Equal(32, job.GetProperty("requestId").GetString()!.Length);
        Assert.Equal([newer.Id, older.Id], jobs.EnumerateArray().Select(item => item.GetProperty("id").GetInt64()));
    }

    [Fact]
    public async Task AnotherUserCannotSeeOrCancelAJob()
    {
        var job = await StartAsync(_bobId, Gated(new TaskCompletionSource()));
        await _jobs.WaitForStateAsync(job.Id, JobState.Running);

        using var get = await _carol.GetAsync($"/api/jobs/{job.Id}", Token);
        using var cancel = await _carol.PostAsync($"/api/jobs/{job.Id}/cancel", null, Token);
        using var list = await _carol.GetAsync("/api/jobs", Token);

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(ErrorCodes.JobsJobNotFound, await get.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
        Assert.Equal(ErrorCodes.JobsJobNotFound, await cancel.ErrorCodeAsync());
        Assert.Equal(0, (await list.JsonAsync()).GetArrayLength());
        Assert.Equal(JobState.Running, (await _jobs.GetAsync(Principal(_bobId), job.Id, Token)).State);
    }

    [Fact]
    public async Task AnAdministratorCanReadAndCancelEveryJobButListsOnlyTheirOwn()
    {
        var job = await StartAsync(_bobId, Gated(new TaskCompletionSource()));
        await _jobs.WaitForStateAsync(job.Id, JobState.Running);

        using var get = await _admin.GetAsync($"/api/jobs/{job.Id}", Token);
        using var list = await _admin.GetAsync("/api/jobs", Token);
        using var cancel = await _admin.PostAsync($"/api/jobs/{job.Id}/cancel", null, Token);
        var cancelled = await _jobs.WaitForStateAsync(job.Id, JobState.Cancelled);

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(job.Id, (await get.JsonAsync()).GetProperty("id").GetInt64());
        Assert.Equal(0, (await list.JsonAsync()).GetArrayLength());
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        Assert.NotNull(cancelled.FinishedAt);
    }

    [Fact]
    public async Task TheOwnerCancelsAJobAndCancellingAgainChangesNothing()
    {
        var job = await StartAsync(_bobId, Gated(new TaskCompletionSource()));
        await _jobs.WaitForStateAsync(job.Id, JobState.Running);

        using var first = await _bob.PostAsync($"/api/jobs/{job.Id}/cancel", null, Token);
        await _jobs.WaitForStateAsync(job.Id, JobState.Cancelled);
        using var second = await _bob.PostAsync($"/api/jobs/{job.Id}/cancel", null, Token);
        using var get = await _bob.GetAsync($"/api/jobs/{job.Id}", Token);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal("cancelled", (await get.JsonAsync()).GetProperty("state").GetString());
    }

    [Fact]
    public async Task AFailedJobShowsItsErrorCodeAndParams()
    {
        var job = await StartAsync(_bobId, _ => throw new PithyxException(ErrorCodes.DesktopNoteTooLong, new Dictionary<string, object?> { ["maxLength"] = 4000 }));
        await _jobs.WaitForStateAsync(job.Id, JobState.Failed);

        using var get = await _bob.GetAsync($"/api/jobs/{job.Id}", Token);
        var body = await get.JsonAsync();

        Assert.Equal("failed", body.GetProperty("state").GetString());
        Assert.Equal(ErrorCodes.DesktopNoteTooLong, body.GetProperty("errorCode").GetString());
        Assert.Equal(4000, body.GetProperty("errorParams").GetProperty("maxLength").GetInt32());
    }

    [Theory]
    [InlineData("GET", "/api/jobs/999")]
    [InlineData("POST", "/api/jobs/999/cancel")]
    public async Task AnUnknownJobIsNotFound(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await _admin.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ErrorCodes.JobsJobNotFound, await response.ErrorCodeAsync());
    }
}
