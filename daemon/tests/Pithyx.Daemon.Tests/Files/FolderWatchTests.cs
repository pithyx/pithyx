using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.Files;
using Pithyx.Daemon.Tests.Support;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Files;

public sealed class FolderWatchTests : IDisposable
{
    private readonly DaemonFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _root;

    public FolderWatchTests()
    {
        _client = _factory.CreateApiClient();
        _root = Directory.CreateDirectory(Path.Combine(_factory.DataDirectory, "pool", "share")).FullName;
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<(WebSocket Socket, string Topic)> OpenFolderAsync()
    {
        using var setup = await _client.PostAsync("/api/setup", new { username = "admin", password = AdminPassword });
        var cookie = setup.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("pithyx_session=", StringComparison.Ordinal)).Split(';')[0];
        using var created = await _client.PostAsync("/api/shares", new { name = "Media", path = _root });
        var share = (await created.JsonAsync()).GetProperty("id").GetInt64();
        using var listed = await _client.GetAsync($"/api/files/{share}/list", Token);
        var topic = (await listed.JsonAsync()).GetProperty("topic").GetString()!;

        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers.Cookie = cookie;
            request.Headers.Origin = "http://localhost";
        };
        var socket = await client.ConnectAsync(new Uri("ws://localhost/ws"), Token);
        await SendAsync(socket, new { type = "sub", id = "1", topic });
        Assert.Equal("ack", (await ReceiveAsync(socket))!.Value.GetProperty("type").GetString());
        var snapshot = (await ReceiveAsync(socket))!.Value;
        Assert.True(snapshot.GetProperty("data").GetProperty("watching").GetBoolean());
        return (socket, topic);
    }

    private static Task SendAsync(WebSocket socket, object message) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, endOfMessage: true, Token);

    private static async Task<JsonElement?> ReceiveAsync(WebSocket socket, TimeSpan? wait = null)
    {
        var buffer = new byte[64 * 1024];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(wait ?? TimeSpan.FromSeconds(10));
        try
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            return JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement.Clone();
        }
        catch (OperationCanceledException) when (!Token.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Moves the daemon's clock past the batch delay until the folder event arrives.</summary>
    /// <remarks>A deadline, not a number of attempts: on a runner busy with the whole suite, five seconds were too few.</remarks>
    private async Task<JsonElement> NextEventAsync(WebSocket socket)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            _factory.Time.Advance(FolderWatches.BatchDelay);
            if (await ReceiveAsync(socket, TimeSpan.FromMilliseconds(50)) is { } message)
            {
                Assert.Equal("event", message.GetProperty("type").GetString());
                return message.GetProperty("data");
            }
        }
        throw new TimeoutException("No folder event arrived.");
    }

    private static string[] Names(JsonElement data, string property) =>
        property == "changed"
            ? [.. data.GetProperty("changed").EnumerateArray().Select(entry => entry.GetProperty("name").GetString()!)]
            : [.. data.GetProperty("removed").EnumerateArray().Select(name => name.GetString()!)];

    /// <summary>
    /// Collects folder events until every expected name was reported as changed or removed; the kernel may split one
    /// action (create, then close after writing) over two batches.
    /// </summary>
    private async Task<(HashSet<string> Changed, HashSet<string> Removed, List<JsonElement> Events)> CollectAsync(
        WebSocket socket, string[] changed, string[] removed)
    {
        var seenChanged = new HashSet<string>(StringComparer.Ordinal);
        var seenRemoved = new HashSet<string>(StringComparer.Ordinal);
        var events = new List<JsonElement>();
        while (!changed.All(seenChanged.Contains) || !removed.All(seenRemoved.Contains))
        {
            var data = await NextEventAsync(socket);
            events.Add(data);
            seenChanged.UnionWith(Names(data, "changed"));
            seenRemoved.UnionWith(Names(data, "removed"));
        }
        return (seenChanged, seenRemoved, events);
    }

    [Fact]
    public async Task SendsCreatedChangedAndRemovedEntriesOfAnOpenFolder()
    {
        var (socket, _) = await OpenFolderAsync();
        using var connection = socket;

        await File.WriteAllTextAsync(Path.Combine(_root, "new.txt"), "hello", Token);
        var created = await CollectAsync(socket, ["new.txt"], []);
        Assert.Equal(5, created.Events[^1].GetProperty("changed")[0].GetProperty("size").GetInt64());

        // Reading is not a change: what follows is only the rename.
        _ = await File.ReadAllTextAsync(Path.Combine(_root, "new.txt"), Token);
        File.Move(Path.Combine(_root, "new.txt"), Path.Combine(_root, "renamed.txt"));
        var moved = await CollectAsync(socket, ["renamed.txt"], ["new.txt"]);
        Assert.DoesNotContain("new.txt", moved.Changed);
        Assert.All(moved.Events, data => Assert.False(data.GetProperty("resync").GetBoolean()));
    }

    [Fact]
    public async Task HidesUploadsInProgressAndStopsWatchingWithoutSubscribers()
    {
        var (socket, topic) = await OpenFolderAsync();
        var watches = _factory.Services.GetRequiredService<FolderWatches>();
        Assert.Contains(topic, watches.Watched);

        await File.WriteAllTextAsync(Path.Combine(_root, $"{FsPath.UploadPrefix}abc"), "partial", Token);
        File.Move(Path.Combine(_root, $"{FsPath.UploadPrefix}abc"), Path.Combine(_root, "done.bin"));
        var done = await CollectAsync(socket, ["done.bin"], []);
        Assert.DoesNotContain(done.Changed.Concat(done.Removed), name => name.StartsWith(FsPath.UploadPrefix, StringComparison.Ordinal));

        await SendAsync(socket, new { type = "unsub", id = "2", topic });
        // A late batch of the upload may still be on its way; the watch is released by the time the ack arrives.
        JsonElement? message;
        do
        {
            message = await ReceiveAsync(socket);
        }
        while (message is { } received && !(received.GetProperty("type").GetString() == "ack" && received.GetProperty("id").GetString() == "2"));
        Assert.DoesNotContain(topic, watches.Watched);
        socket.Dispose();
    }

    // Found on a dev VM: an open Files window kept the share's dataset busy, so zfs destroy could not unmount it.
    [Fact]
    public async Task ReleasingAMountPointClosesTheWatchHandlesAndReportsTheFolderGone()
    {
        var (socket, _) = await OpenFolderAsync();
        var watches = _factory.Services.GetRequiredService<FolderWatches>();
        Assert.Contains(OpenHandles(), target => target == _root);

        Assert.Equal(1, watches.ReleaseBelow([Path.GetDirectoryName(_root)!]));

        var gone = await NextEventAsync(socket);
        Assert.True(gone.GetProperty("gone").GetBoolean());
        Assert.DoesNotContain(OpenHandles(), target => target.StartsWith(_root, StringComparison.Ordinal));
        Assert.Empty(watches.Watched);
    }

    private static IEnumerable<string> OpenHandles() =>
        new DirectoryInfo("/proc/self/fd").EnumerateFileSystemInfos().Select(link => link.LinkTarget ?? "");

    [Fact]
    public async Task ReportsAFolderThatWasDeleted()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_root, "sub")).FullName;
        var (socket, _) = await OpenFolderAsync();
        using var connection = socket;
        Directory.Delete(_root, recursive: true);
        var gone = await NextEventAsync(socket);
        Assert.True(gone.GetProperty("gone").GetBoolean() || Names(gone, "removed").Contains(Path.GetFileName(sub)));
    }
}
