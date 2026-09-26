using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Vyxis.Daemon.Commands;
using Vyxis.Daemon.Realtime;
using Vyxis.Daemon.Storage;
using Vyxis.Daemon.Tests.Support;

namespace Vyxis.Daemon.Tests.Storage;

public sealed class SystemStateServiceTests : IAsyncDisposable
{
    private static readonly string _allDisks = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "commands", "default", "lsblk.stdout"));

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly ScriptedCommandRunner _commands = new();
    private readonly DiskInventory _inventory = new();
    private readonly TopicHub _hub;
    private readonly SystemStateService _service;
    private readonly TopicHub.Connection _subscriber;

    public SystemStateServiceTests()
    {
        _hub = new TopicHub(new TopicRegistry([_inventory.Definition()]), _time, Options.Create(new JsonOptions()), NullLogger<TopicHub>.Instance);
        _service = new SystemStateService(_commands, _inventory, _hub, _time, NullLogger<SystemStateService>.Instance);
        _subscriber = _hub.Connect(new ClaimsPrincipal(new ClaimsIdentity([], "test")), "s");
        SetDisks(_allDisks);
    }

    public async ValueTask DisposeAsync()
    {
        await _service.StopAsync(CancellationToken.None);
        _service.Dispose();
    }

    private void SetDisks(string lsblkJson) => _commands.Results["lsblk"] = new CommandResult(0, lsblkJson, "", TimeSpan.Zero);

    private static string Without(string lsblkJson, string name)
    {
        var document = JsonSerializer.Deserialize<Dictionary<string, List<JsonElement>>>(lsblkJson)!;
        document["blockdevices"].RemoveAll(device => device.GetProperty("name").GetString() == name);
        return JsonSerializer.Serialize(document);
    }

    private async Task<JsonElement> NextEventAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var payload = await _subscriber.Outbox.Reader.ReadAsync(timeout.Token);
            var message = JsonDocument.Parse(payload).RootElement.Clone();
            if (message.GetProperty("type").GetString() == "event")
            {
                return message;
            }
        }
    }

    private async Task AdvanceUntilAsync(Task task)
    {
        for (var i = 0; i < 100 && !task.IsCompleted; i++)
        {
            _time.Advance(SystemStateService.Debounce);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ReadsTheDisksBeforeTheDaemonAcceptsRequests()
    {
        await _service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, _inventory.Current.Disks.Count);
    }

    [Fact]
    public async Task UdevEventLeadsToAnEventWithTheRemovedDisk()
    {
        await _service.StartAsync(TestContext.Current.CancellationToken);
        await _hub.SubscribeAsync(_subscriber, "c1", DiskInventory.Topic, TestContext.Current.CancellationToken);

        SetDisks(Without(_allDisks, "sdg"));
        _commands.Push("UDEV  [12698.860548] remove   /devices/pci0000:00/.../block/sdg (block)");
        var next = NextEventAsync();
        await AdvanceUntilAsync(next);
        var message = await next;

        Assert.Equal(DiskInventory.Topic, message.GetProperty("topic").GetString());
        Assert.Equal(["serial-VYXTEST06"], message.GetProperty("data").GetProperty("removals").EnumerateArray().Select(r => r.GetString()));
        Assert.Empty(message.GetProperty("data").GetProperty("upserts").EnumerateArray());
        Assert.Equal(6, _inventory.Current.Disks.Count);
    }

    [Fact]
    public async Task ABurstOfEventsReadsTheDisksOnce()
    {
        await _service.StartAsync(TestContext.Current.CancellationToken);
        var before = _commands.Calls.Count(c => c.FileName == "lsblk");

        for (var i = 0; i < 5; i++)
        {
            _commands.Push($"UDEV  [1.{i}] change   /devices/.../block/sdg (block)");
        }
        await Task.Delay(100, TestContext.Current.CancellationToken);
        _time.Advance(SystemStateService.Debounce);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        _time.Advance(SystemStateService.Debounce);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.InRange(_commands.Calls.Count(c => c.FileName == "lsblk") - before, 1, 2);
    }

    [Fact]
    public async Task UnchangedDisksPublishNothing()
    {
        await _service.StartAsync(TestContext.Current.CancellationToken);
        await _hub.SubscribeAsync(_subscriber, "c1", DiskInventory.Topic, TestContext.Current.CancellationToken);
        while (_subscriber.Outbox.Reader.TryRead(out _))
        {
        }

        _commands.Push("UDEV  [1.0] change   /devices/.../block/sdg (block)");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        _time.Advance(SystemStateService.Debounce);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.False(_subscriber.Outbox.Reader.TryRead(out _));
    }
}
