using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Hosting;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;
using static Pithyx.Daemon.Tests.Jobs.JobTestSupport;

namespace Pithyx.Daemon.Tests.Jobs;

public sealed class JobServiceTests : IAsyncLifetime
{
    private readonly DaemonFactory _factory = new();
    private JobService _jobs = null!;
    private long _userId;

    public async ValueTask InitializeAsync()
    {
        _jobs = _factory.Services.GetRequiredService<JobService>();
        _userId = await _factory.InsertUserAsync("ada");
    }

    public ValueTask DisposeAsync()
    {
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }

    private Task<Job> StartAsync(Func<JobContext, Task> work, object? parameters = null) =>
        _jobs.StartAsync(_userId, "test.work", parameters, work, Token);

    private Task<string> RowStateAsync(long id) => _factory.ScalarAsync<string>("SELECT state FROM jobs WHERE id = @id", new { id });

    [Fact]
    public async Task AJobIsQueuedThenRunsAndSucceedsWithItsResult()
    {
        var running = new TaskCompletionSource<Job>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = await StartAsync(
            async context =>
            {
                running.SetResult(await _jobs.GetAsync(Principal(_userId), context.JobId, context.Cancellation));
                context.Report(new JobProgress(3, 3, 300, 300, "/tank/c.txt"));
                context.SetResult(new { copied = 3 });
            },
            new { source = "/tank/a", target = "/tank/b" });

        var succeeded = await _jobs.WaitForStateAsync(queued.Id, JobState.Succeeded);
        var whileRunning = await running.Task;

        Assert.Equal(JobState.Queued, queued.State);
        Assert.Equal("/tank/a", queued.Parameters!.Value.GetProperty("source").GetString());
        Assert.Equal(JobState.Running, whileRunning.State);
        Assert.NotNull(whileRunning.StartedAt);
        Assert.Null(whileRunning.FinishedAt);
        Assert.Equal(3, succeeded.Result!.Value.GetProperty("copied").GetInt32());
        Assert.Equal(new JobProgress(3, 3, 300, 300, "/tank/c.txt"), succeeded.Progress);
        Assert.Null(succeeded.ErrorCode);
        Assert.NotNull(succeeded.FinishedAt);
        Assert.Equal("succeeded", await RowStateAsync(queued.Id));
        Assert.Equal("{\"copied\":3}", await _factory.ScalarAsync<string>("SELECT result FROM jobs WHERE id = @id", new { id = queued.Id }));
        Assert.Equal(300, await _factory.ScalarAsync<long>("SELECT bytes_done FROM jobs WHERE id = @id", new { id = queued.Id }));
    }

    [Fact]
    public async Task APithyxExceptionFailsTheJobWithItsCodeAndParams()
    {
        var job = await StartAsync(_ => throw new PithyxException(ErrorCodes.DesktopLayoutTooLarge, new Dictionary<string, object?> { ["maxWindows"] = 50 }));

        var failed = await _jobs.WaitForStateAsync(job.Id, JobState.Failed);

        Assert.Equal(ErrorCodes.DesktopLayoutTooLarge, failed.ErrorCode);
        Assert.Equal(50, ((JsonElement)failed.ErrorParams!["maxWindows"]!).GetInt32());
        Assert.Equal("{\"maxWindows\":50}", await _factory.ScalarAsync<string>("SELECT error_params FROM jobs WHERE id = @id", new { id = job.Id }));
        Assert.Equal(ErrorCodes.DesktopLayoutTooLarge, await _factory.ScalarAsync<string>("SELECT error_code FROM jobs WHERE id = @id", new { id = job.Id }));
    }

    [Fact]
    public async Task AnUnexpectedExceptionFailsTheJobWithTheGenericCodeAndIsLoggedWithTheRequestId()
    {
        var job = await StartAsync(_ => throw new InvalidOperationException("disk on fire"));

        var failed = await _jobs.WaitForStateAsync(job.Id, JobState.Failed);

        Assert.Equal(ErrorCodes.SystemInternalUnexpected, failed.ErrorCode);
        Assert.Empty(failed.ErrorParams!);
        var log = Path.Combine(_factory.DataDirectory, "logs", "daemon.json");
        string? entry = null;
        await WaitUntilAsync(
            () =>
            {
                using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                entry = reader.ReadToEnd().Split('\n').FirstOrDefault(line => line.Contains("failed unexpectedly", StringComparison.Ordinal));
                return Task.FromResult(entry is not null);
            },
            "the failure in the log");
        using var logged = JsonDocument.Parse(entry!);
        Assert.Contains("disk on fire", logged.RootElement.GetProperty("@x").GetString(), StringComparison.Ordinal);
        Assert.Equal(job.RequestId, logged.RootElement.GetProperty("RequestId").GetString());
    }

    [Fact]
    public async Task CancellingAQueuedJobEndsItRightAwayAndItsWorkNeverRuns()
    {
        var gate = new TaskCompletionSource();
        var first = await StartAsync(Gated(gate));
        var second = await StartAsync(Gated(gate));
        var ran = false;
        var third = await StartAsync(_ =>
        {
            ran = true;
            return Task.CompletedTask;
        });
        await _jobs.WaitForStateAsync(second.Id, JobState.Running);

        await _jobs.CancelAsync(Principal(_userId), third.Id, Token);
        var cancelled = await _jobs.GetAsync(Principal(_userId), third.Id, Token);
        gate.SetResult();
        await _jobs.WaitForStateAsync(first.Id, JobState.Succeeded);
        await _jobs.WaitForStateAsync(second.Id, JobState.Succeeded);

        Assert.Equal(JobState.Cancelled, cancelled.State);
        Assert.Null(cancelled.StartedAt);
        Assert.NotNull(cancelled.FinishedAt);
        Assert.Equal("cancelled", await RowStateAsync(third.Id));
        Assert.False(ran);
    }

    [Fact]
    public async Task CancellingARunningJobCancelsItsWork()
    {
        var job = await StartAsync(Gated(new TaskCompletionSource()));
        await _jobs.WaitForStateAsync(job.Id, JobState.Running);

        await _jobs.CancelAsync(Principal(_userId), job.Id, Token);
        var cancelled = await _jobs.WaitForStateAsync(job.Id, JobState.Cancelled);

        Assert.NotNull(cancelled.StartedAt);
        Assert.NotNull(cancelled.FinishedAt);
        Assert.Null(cancelled.ErrorCode);
        Assert.Equal("cancelled", await RowStateAsync(job.Id));
    }

    [Fact]
    public async Task CancellingAFinishedJobChangesNothing()
    {
        var job = await StartAsync(_ => Task.CompletedTask);
        var succeeded = await _jobs.WaitForStateAsync(job.Id, JobState.Succeeded);

        await _jobs.CancelAsync(Principal(_userId), job.Id, Token);
        await _jobs.CancelAsync(Principal(_userId), job.Id, Token);

        Assert.Equal(succeeded, await _jobs.GetAsync(Principal(_userId), job.Id, Token), JobComparer.Instance);
        Assert.Equal("succeeded", await RowStateAsync(job.Id));
    }

    [Fact]
    public async Task AtMostTwoJobsRunAtOnceAndQueuedJobsStartInOrder()
    {
        var gates = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource()).ToArray();
        var started = gates.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var active = 0;
        var mostActive = 0;
        Func<JobContext, Task> Work(int index) => async context =>
        {
            InterlockedMax(ref mostActive, Interlocked.Increment(ref active));
            started[index].SetResult();
            try
            {
                await gates[index].Task.WaitAsync(context.Cancellation);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        };
        var jobs = new List<Job>();
        for (var index = 0; index < gates.Length; index++)
        {
            jobs.Add(await StartAsync(Work(index)));
        }

        await Task.WhenAll(started[0].Task, started[1].Task).WaitAsync(Token);
        var thirdWhileTwoRun = (await _jobs.GetAsync(Principal(_userId), jobs[2].Id, Token)).State;
        gates[1].SetResult();
        await started[2].Task.WaitAsync(Token);
        var fourthWhileThirdRuns = (await _jobs.GetAsync(Principal(_userId), jobs[3].Id, Token)).State;
        gates[0].SetResult();
        await started[3].Task.WaitAsync(Token);
        gates[2].SetResult();
        gates[3].SetResult();
        await _jobs.WaitForStateAsync(jobs[3].Id, JobState.Succeeded);

        Assert.Equal(JobState.Queued, thirdWhileTwoRun);
        Assert.Equal(JobState.Queued, fourthWhileThirdRuns);
        Assert.Equal(JobRunner.MaxConcurrentJobs, mostActive);
    }

    [Fact]
    public async Task TheWorkRunsInAnActivityWithTheRequestIdOfTheStartingRequest()
    {
        var seen = new TaskCompletionSource<(string TraceId, string? Source, string RequestId)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Job job;
        using (var request = new Activity("test.request").Start())
        {
            job = await StartAsync(_ =>
            {
                seen.SetResult((Activity.Current?.TraceId.ToHexString() ?? "", Activity.Current?.Source.Name, Tracing.CurrentRequestId));
                return Task.CompletedTask;
            });
            Assert.Equal(request.TraceId.ToHexString(), job.RequestId);
        }

        var (traceId, source, requestId) = await seen.Task;

        Assert.Equal(job.RequestId, traceId);
        Assert.Equal(job.RequestId, requestId);
        Assert.Equal(Tracing.SourceName, source);
        Assert.Equal(job.RequestId, await _factory.ScalarAsync<string>("SELECT request_id FROM jobs WHERE id = @id", new { id = job.Id }));
    }

    [Fact]
    public async Task ProgressIsWrittenToTheDatabaseAtMostOncePerSecond()
    {
        var contextSource = new TaskCompletionSource<JobContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource();
        var job = await StartAsync(context =>
        {
            contextSource.SetResult(context);
            return gate.Task.WaitAsync(context.Cancellation);
        });
        var context = await contextSource.Task;
        await WaitUntilAsync(async () => await RowStateAsync(job.Id) == "running", "the running state in the row");
        Task<long> StoredItemsAsync() => _factory.ScalarAsync<long>("SELECT items_done FROM jobs WHERE id = @id", new { id = job.Id });

        for (var item = 1; item <= 50; item++)
        {
            context.Report(new JobProgress(item, 50, item * 10, 500, $"file{item}"));
        }
        var live = await _jobs.GetAsync(Principal(_userId), job.Id, Token);
        await Task.Delay(200, Token);
        var storedWithinTheFirstSecond = await StoredItemsAsync();
        _factory.Time.Advance(JobService.PersistInterval);
        await WaitUntilAsync(async () => await StoredItemsAsync() == 50, "the progress after one second");

        context.Report(new JobProgress(51, 51, 510, 510, "file51"));
        _factory.Time.Advance(JobService.PersistInterval - TimeSpan.FromMilliseconds(1));
        await Task.Delay(200, Token);
        var storedJustBeforeTheNextSecond = await StoredItemsAsync();
        _factory.Time.Advance(TimeSpan.FromMilliseconds(1));
        await WaitUntilAsync(async () => await StoredItemsAsync() == 51, "the progress after the next second");
        gate.SetResult();
        await _jobs.WaitForStateAsync(job.Id, JobState.Succeeded);

        Assert.Equal(50, live.Progress.ItemsDone);
        Assert.Equal(0, storedWithinTheFirstSecond);
        Assert.Equal(50, storedJustBeforeTheNextSecond);
        Assert.Equal("file51", await _factory.ScalarAsync<string>("SELECT current_item FROM jobs WHERE id = @id", new { id = job.Id }));
    }

    [Fact]
    public async Task FinishedJobsAreDeletedSevenDaysAfterTheyFinished()
    {
        var old = await StartAsync(_ => Task.CompletedTask);
        await _jobs.WaitForStateAsync(old.Id, JobState.Succeeded);
        _factory.Time.Advance(TimeSpan.FromDays(6));
        var recent = await StartAsync(_ => Task.CompletedTask);
        await _jobs.WaitForStateAsync(recent.Id, JobState.Succeeded);
        var running = await StartAsync(Gated(new TaskCompletionSource()));
        await _jobs.WaitForStateAsync(running.Id, JobState.Running);

        _factory.Time.Advance(TimeSpan.FromDays(1) + JobCleanupService.Interval);
        await WaitUntilAsync(async () => await _factory.ScalarAsync<long>("SELECT count(*) FROM jobs WHERE id = @id", new { id = old.Id }) == 0, "the old job to be deleted");

        Assert.Equal(1, await _factory.ScalarAsync<long>("SELECT count(*) FROM jobs WHERE id = @id", new { id = recent.Id }));
        Assert.Equal(1, await _factory.ScalarAsync<long>("SELECT count(*) FROM jobs WHERE id = @id", new { id = running.Id }));
    }

    [Fact]
    public async Task JobsLeftQueuedOrRunningAreInterruptedAfterARestartAndNeverResumed()
    {
        var runs = 0;
        Func<JobContext, Task> work = context =>
        {
            Interlocked.Increment(ref runs);
            return Task.Delay(Timeout.Infinite, context.Cancellation);
        };
        string dataDirectory;
        long done, crashed, queued;
        long[] running;
        using (var first = new DaemonFactory { KeepDataDirectory = true })
        {
            dataDirectory = first.DataDirectory;
            var jobs = first.Services.GetRequiredService<JobService>();
            var userId = await first.InsertUserAsync("bob");
            done = (await jobs.StartAsync(userId, "test.work", null, _ => Task.CompletedTask, Token)).Id;
            await jobs.WaitForStateAsync(done, JobState.Succeeded);
            running = [(await jobs.StartAsync(userId, "test.work", null, work, Token)).Id, (await jobs.StartAsync(userId, "test.work", null, work, Token)).Id];
            queued = (await jobs.StartAsync(userId, "test.work", null, work, Token)).Id;
            await jobs.WaitForStateAsync(running[1], JobState.Running);
            // What a crash leaves behind: a running row that nothing marked on the way down.
            crashed = await first.ScalarAsync<long>(
                "INSERT INTO jobs (user_id, kind, state, request_id, created_at, started_at) VALUES (@userId, 'test.work', 'running', @requestId, @now, @now) RETURNING id",
                new { userId, requestId = ActivityTraceId.CreateRandom().ToHexString(), now = "2026-01-01T00:00:00.0000000Z" });
        }

        using var second = new DaemonFactory { DataDirectory = dataDirectory };
        var restarted = second.Services.GetRequiredService<JobService>();
        var admin = Principal(0, admin: true);

        foreach (var id in (long[])[.. running, queued, crashed])
        {
            var job = await restarted.GetAsync(admin, id, Token);
            Assert.Equal(JobState.Interrupted, job.State);
            Assert.NotNull(job.FinishedAt);
        }
        Assert.Equal(JobState.Succeeded, (await restarted.GetAsync(admin, done, Token)).State);
        await Task.Delay(200, Token);
        Assert.Equal(2, runs);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
            {
                return;
            }
            current = previous;
        }
    }

    // Records compare their JSON elements by reference; the JSON text says whether a job is unchanged.
    private sealed class JobComparer : IEqualityComparer<Job>
    {
        public static JobComparer Instance { get; } = new();

        public bool Equals(Job? x, Job? y) => JsonSerializer.Serialize(x) == JsonSerializer.Serialize(y);

        public int GetHashCode(Job obj) => JsonSerializer.Serialize(obj).GetHashCode(StringComparison.Ordinal);
    }
}
