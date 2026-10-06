using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.Jobs;
using Pithyx.Daemon.Notifications;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;
using static Pithyx.Daemon.Tests.Jobs.JobTestSupport;

namespace Pithyx.Daemon.Tests.Notifications;

public sealed class NotificationServiceTests : IAsyncLifetime
{
    private readonly DaemonFactory _factory = new();
    private NotificationService _notifications = null!;
    private long _bobId;
    private long _carolId;

    public async ValueTask InitializeAsync()
    {
        _notifications = _factory.Services.GetRequiredService<NotificationService>();
        _bobId = await _factory.InsertUserAsync("bob");
        _carolId = await _factory.InsertUserAsync("carol");
    }

    public ValueTask DisposeAsync()
    {
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }

    private static NotificationDraft Draft(string code = "test.message", NotificationTone tone = NotificationTone.Info) =>
        new(code, new Dictionary<string, object?> { ["count"] = 3, ["name"] = "tank" }, tone);

    private Task<Notification> CreateAsync(long userId, string code = "test.message") => _notifications.CreateAsync(userId, Draft(code), Token);

    private async Task<IReadOnlyList<Notification>> ListAsync(long userId) => (await _notifications.ListAsync(Principal(userId), Token)).Items;

    [Fact]
    public async Task ANotificationIsStoredWithItsCodeAndParamsAndListedNewestFirstForItsUserOnly()
    {
        var first = await CreateAsync(_bobId, "test.first");
        var second = await _notifications.CreateAsync(_bobId, Draft("test.second", NotificationTone.Critical) with { App = "org.pithyx.demo" }, Token);
        await CreateAsync(_carolId);

        var bob = await ListAsync(_bobId);

        Assert.Equal([second.Id, first.Id], bob.Select(entry => entry.Id));
        Assert.Equal("test.second", bob[0].Code);
        Assert.Equal(NotificationTone.Critical, bob[0].Tone);
        Assert.Equal("org.pithyx.demo", bob[0].App);
        Assert.Equal(3, bob[1].Params["count"].GetInt32());
        Assert.Equal("tank", bob[1].Params["name"].GetString());
        Assert.Null(bob[1].App);
        Assert.Null(bob[1].ReadAt);
        Assert.Equal(_factory.Time.GetUtcNow(), bob[1].CreatedAt);
        Assert.Single(await ListAsync(_carolId));
        Assert.Equal("{\"count\":3,\"name\":\"tank\"}", await _factory.ScalarAsync<string>("SELECT params FROM notifications WHERE id = @id", new { id = first.Id }));
    }

    [Fact]
    public async Task EveryAdministratorGetsTheSameNotification()
    {
        var adminId = await _factory.InsertUserAsync("ada", admin: true);
        var otherAdminId = await _factory.InsertUserAsync("grace", admin: true);

        await _notifications.CreateForAdministratorsAsync(Draft("storage.alert.poolDegraded", NotificationTone.Critical), Token);

        Assert.Equal("storage.alert.poolDegraded", Assert.Single(await ListAsync(adminId)).Code);
        Assert.Equal("storage.alert.poolDegraded", Assert.Single(await ListAsync(otherAdminId)).Code);
        Assert.Empty(await ListAsync(_bobId));
    }

    [Fact]
    public async Task AUserKeepsAtMostTwoHundredAndTheOldestIsDropped()
    {
        var first = await CreateAsync(_bobId);
        for (var i = 1; i < NotificationService.MaxPerUser; i++)
        {
            await CreateAsync(_bobId);
        }
        await CreateAsync(_carolId);

        var newest = await CreateAsync(_bobId);

        var bob = await ListAsync(_bobId);
        Assert.Equal(NotificationService.MaxPerUser, bob.Count);
        Assert.Equal(newest.Id, bob[0].Id);
        Assert.DoesNotContain(bob, entry => entry.Id == first.Id);
        Assert.Equal(NotificationService.MaxPerUser, await _factory.ScalarAsync<long>("SELECT count(*) FROM notifications WHERE user_id = @_bobId", new { _bobId }));
        Assert.Single(await ListAsync(_carolId));
    }

    [Fact]
    public async Task MarkingReadChangesTheGivenOnesOrAllAndOnlyTheUsersOwn()
    {
        var first = await CreateAsync(_bobId);
        var second = await CreateAsync(_bobId);
        var third = await CreateAsync(_bobId);
        var carols = await CreateAsync(_carolId);

        await _notifications.MarkReadAsync(Principal(_bobId), [first.Id, carols.Id], Token);
        var afterSome = await ListAsync(_bobId);
        _factory.Time.Advance(TimeSpan.FromMinutes(5));
        await _notifications.MarkReadAsync(Principal(_bobId), null, Token);
        var afterAll = await ListAsync(_bobId);

        Assert.Equal([third.Id, second.Id], afterSome.Where(entry => entry.ReadAt is null).Select(entry => entry.Id));
        Assert.All(afterAll, entry => Assert.NotNull(entry.ReadAt));
        // Marking all again leaves the first read time alone.
        Assert.Equal(afterSome.Single(entry => entry.Id == first.Id).ReadAt, afterAll.Single(entry => entry.Id == first.Id).ReadAt);
        Assert.Null(Assert.Single(await ListAsync(_carolId)).ReadAt);
        Assert.Equal(0, await _factory.ScalarAsync<long>("SELECT count(*) FROM notifications WHERE user_id = @_bobId AND read_at IS NULL", new { _bobId }));
    }

    [Fact]
    public async Task DeletingOneOrAllTouchesOnlyTheUsersOwnAndAnotherUsersIdAnswersLikeAMissingOne()
    {
        var first = await CreateAsync(_bobId);
        var second = await CreateAsync(_bobId);
        var carols = await CreateAsync(_carolId);

        await _notifications.DeleteAsync(Principal(_bobId), first.Id, Token);
        var afterOne = await ListAsync(_bobId);
        var foreign = await Assert.ThrowsAsync<PithyxException>(() => _notifications.DeleteAsync(Principal(_bobId), carols.Id, Token));
        await _notifications.ClearAsync(Principal(_bobId), Token);

        Assert.Equal([second.Id], afterOne.Select(entry => entry.Id));
        Assert.Equal(ErrorCodes.NotificationsNotificationNotFound, foreign.Code);
        Assert.Empty(await ListAsync(_bobId));
        Assert.Equal(0, await _factory.ScalarAsync<long>("SELECT count(*) FROM notifications WHERE user_id = @_bobId", new { _bobId }));
        Assert.Single(await ListAsync(_carolId));
    }

    [Fact]
    public async Task NotificationsOlderThanThirtyDaysAreDeletedByTheHourlyCleanup()
    {
        var old = await CreateAsync(_bobId);
        _factory.Time.Advance(TimeSpan.FromDays(29));
        var recent = await CreateAsync(_bobId);

        _factory.Time.Advance(TimeSpan.FromDays(1) + NotificationCleanupService.Interval);
        await WaitUntilAsync(async () => await _factory.ScalarAsync<long>("SELECT count(*) FROM notifications WHERE id = @id", new { id = old.Id }) == 0, "the old notification to be deleted");

        Assert.Equal([recent.Id], (await ListAsync(_bobId)).Select(entry => entry.Id));
    }

    [Fact]
    public async Task NotificationsSurviveARestart()
    {
        string dataDirectory;
        long id;
        using (var first = new DaemonFactory { KeepDataDirectory = true })
        {
            dataDirectory = first.DataDirectory;
            var userId = await first.InsertUserAsync("dora");
            id = (await first.Services.GetRequiredService<NotificationService>().CreateAsync(userId, Draft(), Token)).Id;
        }
        using var second = new DaemonFactory { DataDirectory = dataDirectory };

        var list = await second.Services.GetRequiredService<NotificationService>().ListAsync(Principal(await second.ScalarAsync<long>("SELECT id FROM users WHERE username = 'dora'")), Token);

        Assert.Equal(id, Assert.Single(list.Items).Id);
    }

    [Fact]
    public async Task AJobThatEndsNotifiesItsOwnerOnceWithItsKind()
    {
        var jobs = _factory.Services.GetRequiredService<JobService>();
        var succeeded = await jobs.StartAsync(_bobId, "files.copy", null, _ => Task.CompletedTask, Token);
        await jobs.WaitForStateAsync(succeeded.Id, JobState.Succeeded);
        var failed = await jobs.StartAsync(_bobId, "files.move", null, _ => throw new PithyxException(ErrorCodes.FsVolumeFull), Token);
        await jobs.WaitForStateAsync(failed.Id, JobState.Failed);
        var cancelled = await jobs.StartAsync(_bobId, "files.delete", null, Gated(new TaskCompletionSource()), Token);
        await jobs.WaitForStateAsync(cancelled.Id, JobState.Running);
        await jobs.CancelAsync(Principal(_bobId), cancelled.Id, Token);
        await jobs.WaitForStateAsync(cancelled.Id, JobState.Cancelled);
        await WaitUntilAsync(async () => (await ListAsync(_bobId)).Count == 3, "three notifications");

        // A job reads as finished just before its notification exists, so the order of the three may vary.
        var list = (await ListAsync(_bobId)).OrderBy(entry => entry.Params["jobId"].GetInt64()).ToList();

        Assert.Equal(
            [
                (JobService.JobSucceededCode, NotificationTone.Success, "files.copy", succeeded.Id),
                (JobService.JobFailedCode, NotificationTone.Warning, "files.move", failed.Id),
                (JobService.JobCancelledCode, NotificationTone.Info, "files.delete", cancelled.Id),
            ],
            list.Select(entry => (entry.Code, entry.Tone, entry.Params["kind"].GetString(), entry.Params["jobId"].GetInt64())));
        Assert.Equal(ErrorCodes.FsVolumeFull, list[1].Params["errorCode"].GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Object, list[1].Params["errorParams"].ValueKind);
        Assert.False(list[0].Params.ContainsKey("errorCode"));
        Assert.Empty(await ListAsync(_carolId));
    }
}
