using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pithyx.Daemon.SystemSettings;
using Pithyx.Daemon.Tests.Jobs;
using Pithyx.Daemon.Tests.Support;
using Pithyx.Daemon.Ups;
using static Pithyx.Daemon.Tests.Auth.AuthApi;

namespace Pithyx.Daemon.Tests.Ups;

/// <summary>Settings &gt; UPS against a NUT server played by the test (D377); the shutdown stays simulated.</summary>
public sealed class UpsApiTests : IAsyncLifetime
{
    private readonly DaemonFactory _factory = new();
    private readonly FakeUpsd _upsd = new("rack", "monuser", "secret");
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
        _upsd.Dispose();
    }

    private Task<HttpResponseMessage> SaveAsync(object settings) => _admin.PutAsJsonAsync("/api/system/ups", settings, Token);

    private Task<HttpResponseMessage> SaveNetworkAsync(string name = "rack") => SaveAsync(new
    {
        mode = "network",
        host = "127.0.0.1",
        port = _upsd.Port,
        name,
        username = "monuser",
        password = "secret",
        batteryLow = 30,
        runtimeLow = 5,
    });

    /// <summary>
    /// Moves the daemon's clock in poll steps until the state matches; the watch loop may not be waiting yet when the
    /// clock moves, so a single step could be missed.
    /// </summary>
    private async Task<JsonElement> StateAsync(Func<JsonElement, bool> condition)
    {
        JsonElement state = default;
        await EventuallyAsync(async () =>
        {
            state = (await _admin.GetFromJsonAsync<JsonElement>("/api/system/ups", Token)).GetProperty("state");
            return condition(state);
        });
        return state;
    }

    private async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        for (var step = 0; step < 400; step++)
        {
            if (await condition())
            {
                return;
            }
            _factory.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(25, Token);
        }
        throw new TimeoutException("the UPS never reached the expected state");
    }

    private async Task<string[]> NotificationsAsync()
    {
        var list = await _admin.GetFromJsonAsync<JsonElement>("/api/notifications", Token);
        return [.. list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("code").GetString()!)];
    }

    [Fact]
    public async Task ANetworkUpsIsReadAsASecondaryWithoutShowingItsPassword()
    {
        using (var saved = await SaveNetworkAsync())
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        var state = await StateAsync(state => state.GetProperty("connection").GetString() == "connected");
        var settings = (await _admin.GetFromJsonAsync<JsonElement>("/api/system/ups", Token)).GetProperty("settings");

        Assert.Equal("online", state.GetProperty("power").GetString());
        Assert.Equal(100, state.GetProperty("charge").GetInt32());
        Assert.Equal(1800, state.GetProperty("runtime").GetInt32());
        Assert.Equal("Example Rack 1500", state.GetProperty("model").GetString());
        Assert.True(settings.GetProperty("hasPassword").GetBoolean());
        Assert.False(settings.TryGetProperty("password", out _));
        Assert.Contains("LOGIN rack", _upsd.Commands);
    }

    [Fact]
    public async Task OnBatteryTheAdministratorsHearOfItAndAgainWhenThePowerIsBack()
    {
        (await SaveNetworkAsync()).Dispose();
        await StateAsync(state => state.GetProperty("connection").GetString() == "connected");

        _upsd.Set("OB DISCHRG", charge: 80, runtime: 1500);
        var onBattery = await StateAsync(state => state.GetProperty("power").GetString() == "onBattery");
        _upsd.Set("OL CHRG", charge: 79, runtime: 1500);
        await StateAsync(state => state.GetProperty("power").GetString() == "online");
        await EventuallyAsync(async () => (await NotificationsAsync()) is var codes && codes.Contains(UpsService.OnBatteryCode) && codes.Contains(UpsService.OnlineCode));

        Assert.Equal(_factory.Time.GetUtcNow().Year, onBattery.GetProperty("onBatterySince").GetDateTimeOffset().Year);
        Assert.Null(_factory.Services.GetRequiredService<SystemSettingsService>().SimulatedPower);
    }

    [Theory]
    [InlineData("OB DISCHRG", 30, 1500, "charge")]
    [InlineData("OB DISCHRG", 60, 300, "runtime")]
    [InlineData("OB LB", 60, 1500, "low_battery")]
    [InlineData("OL FSD", 100, 1800, "forced")]
    public async Task TheBoxShutsDownAtTheLimits(string status, int charge, int runtime, string reason)
    {
        (await SaveNetworkAsync()).Dispose();
        await StateAsync(state => state.GetProperty("connection").GetString() == "connected");

        _upsd.Set(status, charge, runtime);
        var state = await StateAsync(state => state.GetProperty("shutdownReason").ValueKind == JsonValueKind.String);

        Assert.Equal(reason, state.GetProperty("shutdownReason").GetString());
        // The state names the reason a moment before the power action runs.
        var system = _factory.Services.GetRequiredService<SystemSettingsService>();
        await EventuallyAsync(() => Task.FromResult(system.SimulatedPower == PowerAction.Shutdown));
        await EventuallyAsync(async () => (await NotificationsAsync()).Contains(UpsService.ShutdownCode));
    }

    [Fact]
    public async Task AUpsOnBatteryThatStopsAnsweringShutsTheBoxDown()
    {
        (await SaveNetworkAsync()).Dispose();
        _upsd.Set("OB DISCHRG", charge: 90, runtime: 2000);
        await StateAsync(state => state.GetProperty("power").GetString() == "onBattery");

        _upsd.Dispose();
        var state = await StateAsync(state => state.GetProperty("shutdownReason").ValueKind == JsonValueKind.String);

        Assert.Equal("lost", state.GetProperty("shutdownReason").GetString());
        Assert.Equal("unreachable", state.GetProperty("connection").GetString());
    }

    [Fact]
    public async Task AnUnknownUpsIsUnreachableAndNeverShutsTheBoxDown()
    {
        (await SaveNetworkAsync("nope")).Dispose();

        var state = await StateAsync(state => state.GetProperty("connection").GetString() == "unreachable");

        Assert.Equal("unknown_ups", state.GetProperty("problem").GetString());
        await EventuallyAsync(async () => (await NotificationsAsync()).Contains(UpsService.UnreachableCode));
        Assert.Null(_factory.Services.GetRequiredService<SystemSettingsService>().SimulatedPower);
    }

    [Fact]
    public async Task SettingsAreCheckedAndOnlyForAdministrators()
    {
        using (var refused = await SaveAsync(new { mode = "network", host = "" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            var problem = await refused.JsonAsync();
            Assert.Equal("system.ups.invalid", problem.GetProperty("code").GetString());
            Assert.Equal("host", problem.GetProperty("params").GetProperty("field").GetString());
        }
        var anna = await _factory.InsertUserAsync("anna");
        using var user = _factory.ClientWithCookie(await _factory.SessionCookieAsync(anna));
        using var read = await user.GetAsync("/api/system/ups", Token);
        using var write = await user.PutAsJsonAsync("/api/system/ups", new { mode = "off" }, Token);

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    /// <summary>A NUT server with one UPS whose status the test sets; it records every command it gets.</summary>
    private sealed class FakeUpsd : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<TcpClient> _clients = [];
        private readonly string _name;
        private readonly string _username;
        private readonly string _password;
        private volatile string[] _vars = [];

        public FakeUpsd(string name, string username, string password)
        {
            _name = name;
            _username = username;
            _password = password;
            Set("OL", 100, 1800);
            _listener.Start();
            _ = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public ConcurrentQueue<string> Commands { get; } = new();

        public void Set(string status, int charge, int runtime) => _vars =
        [
            $"VAR {_name} battery.charge \"{charge.ToString(CultureInfo.InvariantCulture)}\"",
            $"VAR {_name} battery.runtime \"{runtime.ToString(CultureInfo.InvariantCulture)}\"",
            $"VAR {_name} device.mfr \"Example\"",
            $"VAR {_name} device.model \"Rack 1500\"",
            $"VAR {_name} ups.load \"30\"",
            $"VAR {_name} ups.status \"{status}\"",
        ];

        public void Dispose()
        {
            if (_stop.IsCancellationRequested)
            {
                return;
            }
            _stop.Cancel();
            _listener.Stop();
            foreach (var client in _clients)
            {
                client.Dispose();
            }
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _clients.Add(client);
                    _ = ServeAsync(client);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Stopped.
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream);
                await using var writer = new StreamWriter(stream) { NewLine = "\n", AutoFlush = true };
                string? user = null;
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    Commands.Enqueue(line);
                    var parts = line.Split(' ');
                    switch (parts[0])
                    {
                        case "USERNAME":
                            user = parts[1];
                            await writer.WriteLineAsync("OK");
                            break;
                        case "PASSWORD":
                            await writer.WriteLineAsync(user == _username && parts[1] == _password ? "OK" : "ERR ACCESS-DENIED");
                            break;
                        case "LOGIN":
                            await writer.WriteLineAsync(parts[1] == _name ? "OK" : "ERR UNKNOWN-UPS");
                            break;
                        case "LIST" when parts.Length == 3 && parts[2] == _name:
                            await writer.WriteLineAsync($"BEGIN LIST VAR {_name}");
                            foreach (var variable in _vars)
                            {
                                await writer.WriteLineAsync(variable);
                            }
                            await writer.WriteLineAsync($"END LIST VAR {_name}");
                            break;
                        case "LIST":
                            await writer.WriteLineAsync("ERR UNKNOWN-UPS");
                            break;
                        case "LOGOUT":
                            await writer.WriteLineAsync("OK Goodbye");
                            return;
                        default:
                            await writer.WriteLineAsync("ERR UNKNOWN-COMMAND");
                            break;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The daemon or the test closed the connection.
            }
        }
    }
}
