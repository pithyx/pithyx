using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Dapper;
using Pithyx.Daemon.Database;
using Pithyx.Daemon.Errors;
using Pithyx.Daemon.FileServices;
using Pithyx.Daemon.Realtime;

namespace Pithyx.Daemon.Network;

/// <summary>One interface after a connection test of the installer.</summary>
/// <param name="Name">The interface.</param>
/// <param name="Up">Whether its link came up.</param>
/// <param name="Addresses">The addresses it got.</param>
/// <param name="Gateways">Its gateways.</param>
/// <param name="GatewayReachable">Whether the first gateway answered a ping; null without a gateway.</param>
public sealed record NetworkTestInterface(string Name, bool Up, IReadOnlyList<string> Addresses, IReadOnlyList<string> Gateways, bool? GatewayReachable);

/// <summary>The connection test of the installer: the settings are applied, checked and rolled back.</summary>
/// <param name="Running">Whether the test still runs; the page may lose its connection meanwhile.</param>
/// <param name="Interfaces">What each tested interface got.</param>
/// <param name="ErrorCode">Why the test could not run.</param>
/// <param name="ErrorParams">Values for its text.</param>
public sealed record NetworkTestStatus(bool Running, IReadOnlyList<NetworkTestInterface> Interfaces, string? ErrorCode, IReadOnlyDictionary<string, object?> ErrorParams)
{
    public static NetworkTestStatus None { get; } = new(false, [], null, new Dictionary<string, object?>());
}

/// <summary>
/// Changes of the network (step 14.2, D110, D292, D337): the settings are checked, stored in SQLite, written as
/// networkd and resolved files (<see cref="NetworkdConfig"/>) and applied by <see cref="NetworkHost"/>, as one change
/// that waits 60 seconds for its confirmation. Without it, or when undone, the previous files, settings and host name come
/// back; a daemon that restarts while a change waits rolls it back at start. Every change is published on the topic
/// <c>system/network</c>.
/// </summary>
public sealed partial class NetworkService(
    SqliteConnectionFactory connections,
    NetworkHost host,
    NetworkConfirmation confirmation,
    TopicHub hub,
    FileServiceChanges fileServices,
    TimeProvider time,
    ILogger<NetworkService> logger) : IDisposable
{
    public const string Topic = "system/network";

    private static readonly TimeSpan _testWait = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _changing = new(1, 1);
    private readonly Lock _lock = new();
    private PendingNetworkChange? _pending;
    private NetworkTestStatus _test = NetworkTestStatus.None;

    /// <summary><c>system/network</c> for administrators; <paramref name="service"/> is resolved per snapshot, since the service publishes through the hub built from this definition.</summary>
    public static TopicDefinition Definition(Func<NetworkService> service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return new TopicDefinition(
            Topic,
            typeof(NetworkState),
            typeof(NetworkState),
            async (_, cancellationToken) => await service().StateAsync(null, cancellationToken),
            Monitoring.ResourceMonitor.AdministratorsOnly);
    }

    /// <summary>Raised after every change, confirmation and rollback.</summary>
    public event Action<NetworkState>? Changed;

    public void Dispose() => _changing.Dispose();

    public NetworkTestStatus Test
    {
        get
        {
            lock (_lock)
            {
                return _test;
            }
        }
    }

    /// <summary>What Pithyx keeps, or no settings at all with the host name the system has.</summary>
    public async Task<NetworkConfig> ConfigAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var json = await connection.ExecuteScalarAsync<string?>("SELECT config FROM network_config WHERE id = 1");
        return json is null ? Default() : NetworkJson.Deserialize<NetworkConfig>(json);
    }

    /// <summary>The network for a request that came in on <paramref name="localAddress"/> (null for the topic).</summary>
    public async Task<NetworkState> StateAsync(IPAddress? localAddress, CancellationToken cancellationToken)
    {
        var config = await ConfigAsync(cancellationToken);
        var links = Links(config);
        PendingNetworkChange? pending;
        lock (_lock)
        {
            pending = _pending;
        }
        return new NetworkState(
            config,
            links,
            host.HostName,
            host.Policy.GlobalChangeable,
            pending,
            (int)NetworkConfirmation.Window.TotalSeconds,
            ConnectionInterface(localAddress, links));
    }

    /// <summary>What a change would do, without doing it.</summary>
    public async Task<NetworkCheck> CheckAsync(NetworkConfig requested, IPAddress? localAddress, CancellationToken cancellationToken)
    {
        var current = await ConfigAsync(cancellationToken);
        var links = Links(current);
        var config = NetworkValidator.Validate(requested, links, current, host.Policy);
        var plan = NetworkdConfig.Plan(host.OwnedFiles(), NetworkdConfig.Render(config));
        var interfaces = Affected(plan, links);
        var connection = ConnectionInterface(localAddress, links);
        var connectionLink = links.FirstOrDefault(link => link.Name == connection);
        var affected = connectionLink is not null
            && (interfaces.Contains(connectionLink.Name) || (connectionLink.VlanParent is { } parent && interfaces.Contains(parent)));
        return new NetworkCheck(config, interfaces, config.Global.HostName != host.HostName, affected, Addresses(config, interfaces, connection));
    }

    /// <summary>Applies <paramref name="requested"/> as one change and starts the confirmation timer.</summary>
    public async Task<PendingNetworkChange> ChangeAsync(NetworkConfig requested, NetworkChangeOrigin origin, CancellationToken cancellationToken)
    {
        await _changing.WaitAsync(cancellationToken);
        try
        {
            if (confirmation.Pending is not null)
            {
                throw new PithyxException(ErrorCodes.NetworkChangePending);
            }
            var current = await ConfigAsync(cancellationToken);
            var links = Links(current);
            var config = NetworkValidator.Validate(requested, links, current, host.Policy);
            var previousFiles = host.OwnedFiles();
            var plan = NetworkdConfig.Plan(previousFiles, NetworkdConfig.Render(config));
            var previousHostName = host.HostName;
            var hostNameChanged = config.Global.HostName != previousHostName;
            if (plan.IsEmpty && !hostNameChanged && NetworkJson.Same(config, current))
            {
                throw new PithyxException(ErrorCodes.NetworkChangeEmpty);
            }
            var interfaces = Affected(plan, links);
            var deadline = time.GetUtcNow() + NetworkConfirmation.Window;
            var change = new PendingNetworkChange(Guid.NewGuid(), deadline, interfaces, Addresses(config, interfaces, null), hostNameChanged ? config.Global.HostName : null, origin);
            await using (var connection = await connections.OpenAsync(cancellationToken))
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                var stored = await connection.ExecuteScalarAsync<string?>("SELECT config FROM network_config WHERE id = 1", transaction: transaction);
                await connection.ExecuteAsync(
                    """
                    INSERT INTO network_pending (id, change_id, previous_config, previous_files, previous_host_name, change, deadline)
                    VALUES (1, @id, @stored, @files, @previousHostName, @change, @deadline)
                    """,
                    new
                    {
                        id = change.Id.ToString(),
                        stored,
                        files = NetworkJson.Serialize(previousFiles),
                        previousHostName,
                        change = NetworkJson.Serialize(change),
                        deadline = Format(deadline),
                    },
                    transaction);
                await SaveAsync(connection, transaction, config);
                await transaction.CommitAsync(cancellationToken);
            }
            lock (_lock)
            {
                _pending = change;
            }
            var failed = await host.ApplyAsync(plan, config, cancellationToken);
            if (failed is null && hostNameChanged)
            {
                failed = await host.SetHostNameAsync(config.Global.HostName, cancellationToken);
                fileServices.Announce();
            }
            if (failed is not null)
            {
                await RollBackLockedAsync("failed", CancellationToken.None);
                throw new PithyxException(ErrorCodes.NetworkApplyFailed, new Dictionary<string, object?> { ["command"] = failed });
            }
            confirmation.TryStart(change.Id, RollBackAsync, deadline);
            LogChanged(change.Id, interfaces, origin);
            await PublishAsync(cancellationToken);
            return change;
        }
        finally
        {
            _changing.Release();
        }
    }

    /// <summary>Keeps the waiting change.</summary>
    public async Task ConfirmAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!confirmation.Confirm(id))
        {
            throw new PithyxException(ErrorCodes.NetworkChangeNotFound);
        }
        await using (var connection = await connections.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync("DELETE FROM network_pending WHERE id = 1");
        }
        lock (_lock)
        {
            _pending = null;
        }
        LogConfirmed(id);
        await PublishAsync(cancellationToken);
    }

    /// <summary>Rolls the waiting change back now.</summary>
    public async Task RevertAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await confirmation.RevertAsync(id))
        {
            throw new PithyxException(ErrorCodes.NetworkChangeNotFound);
        }
    }

    /// <summary>At start: a change that waited when the daemon stopped was never confirmed, so it is rolled back.</summary>
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        if (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM network_pending") > 0)
        {
            await RollBackAsync("restart");
        }
    }

    /// <summary>
    /// The console's "Reset network to DHCP": every card back on the image's DHCP file, no bonds, VLANs, name servers,
    /// search domains or default route; the host name stays. It has no timer: it restores the state the box ships with,
    /// and a rollback would bring back what locked the owner out (D337). A waiting change is dropped first.
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await _changing.WaitAsync(cancellationToken);
        try
        {
            if (confirmation.Pending is { } waiting)
            {
                confirmation.Confirm(waiting.Id);
            }
            var config = new NetworkConfig([], new GlobalNetworkConfig(host.HostName, [], []));
            var plan = NetworkdConfig.Plan(host.OwnedFiles(), NetworkdConfig.Render(config));
            await using (var connection = await connections.OpenAsync(cancellationToken))
            {
                await connection.ExecuteAsync("DELETE FROM network_pending WHERE id = 1");
                await connection.ExecuteAsync("DELETE FROM network_config WHERE id = 1");
            }
            lock (_lock)
            {
                _pending = null;
            }
            var failed = await host.ApplyAsync(plan, config, cancellationToken);
            LogReset(failed ?? "");
            await PublishAsync(cancellationToken);
            if (failed is not null)
            {
                throw new PithyxException(ErrorCodes.NetworkApplyFailed, new Dictionary<string, object?> { ["command"] = failed });
            }
        }
        finally
        {
            _changing.Release();
        }
    }

    /// <summary>
    /// The installer's connection test: applies the settings on the live system, waits until the interfaces have their
    /// addresses (at most 20 seconds), pings their gateways and rolls the change back. Runs in the background; the page
    /// asks for <see cref="Test"/>, since it may lose its connection meanwhile.
    /// </summary>
    public void StartTest(NetworkConfig requested)
    {
        lock (_lock)
        {
            if (_test.Running)
            {
                throw new PithyxException(ErrorCodes.NetworkChangePending);
            }
            _test = new NetworkTestStatus(true, [], null, new Dictionary<string, object?>());
        }
        _ = Task.Run(async () =>
        {
            NetworkTestStatus result;
            try
            {
                result = new NetworkTestStatus(false, await TestAsync(requested), null, new Dictionary<string, object?>());
            }
            catch (PithyxException exception)
            {
                result = new NetworkTestStatus(false, [], exception.Code, exception.Parameters ?? new Dictionary<string, object?>());
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogTestFailed(exception);
                result = new NetworkTestStatus(false, [], ErrorCodes.NetworkApplyFailed, new Dictionary<string, object?> { ["command"] = "test" });
            }
            lock (_lock)
            {
                _test = result;
            }
        });
    }

    private async Task<IReadOnlyList<NetworkTestInterface>> TestAsync(NetworkConfig requested)
    {
        var change = await ChangeAsync(requested, NetworkChangeOrigin.Web, CancellationToken.None);
        try
        {
            var config = await ConfigAsync(CancellationToken.None);
            var expected = config.Interfaces.Where(item => item.HasAddress).ToList();
            var until = time.GetUtcNow() + _testWait;
            IReadOnlyList<NetworkLink> links = Links(config);
            while (time.GetUtcNow() < until && !expected.All(item => Ready(item, links)))
            {
                await Task.Delay(TimeSpan.FromSeconds(1), time);
                links = Links(config);
            }
            var results = new List<NetworkTestInterface>();
            foreach (var item in expected)
            {
                var link = links.FirstOrDefault(candidate => candidate.Name == item.Name);
                bool? reachable = (link is { Gateways.Count: > 0 } ? link.Gateways[0] : null) is { } gateway ? await PingAsync(gateway) : null;
                results.Add(new NetworkTestInterface(item.Name, link?.Up ?? false, link?.Addresses ?? [], link?.Gateways ?? [], reachable));
            }
            return results;
        }
        finally
        {
            if (confirmation.Pending?.Id == change.Id)
            {
                await confirmation.RevertAsync(change.Id);
            }
        }
    }

    private static bool Ready(InterfaceConfig item, IReadOnlyList<NetworkLink> links)
    {
        var link = links.FirstOrDefault(candidate => candidate.Name == item.Name);
        if (link is null || !link.Up)
        {
            return false;
        }
        var ipv4 = link.Addresses.Where(address => !address.Contains(':', StringComparison.Ordinal)).ToList();
        return item.Ipv4 switch
        {
            Ipv4Mode.Static => link.Addresses.Contains(item.Ipv4Address!),
            Ipv4Mode.Dhcp => ipv4.Count > 0,
            _ => item.Ipv6 != Ipv6Mode.Static || link.Addresses.Contains(item.Ipv6Address!),
        };
    }

    private async Task<bool> PingAsync(string gateway)
    {
        if (host.Simulated)
        {
            return true;
        }
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(IPAddress.Parse(gateway.Split('%')[0]), TimeSpan.FromSeconds(2), null, null, CancellationToken.None);
            return reply.Status == IPStatus.Success;
        }
        catch (PingException)
        {
            return false;
        }
    }

    private async Task RollBackAsync(string reason)
    {
        await _changing.WaitAsync();
        try
        {
            await RollBackLockedAsync(reason, CancellationToken.None);
        }
        finally
        {
            _changing.Release();
        }
    }

    /// <summary>Puts back the files, settings and host name the waiting change replaced. Never throws.</summary>
    private async Task RollBackLockedAsync(string reason, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connections.OpenAsync(cancellationToken);
            var row = await connection.QuerySingleOrDefaultAsync<(string ChangeId, string? PreviousConfig, string PreviousFiles, string PreviousHostName)?>(
                "SELECT change_id, previous_config, previous_files, previous_host_name FROM network_pending WHERE id = 1");
            if (row is not { } pending)
            {
                return;
            }
            var previous = pending.PreviousConfig is null ? null : NetworkJson.Deserialize<NetworkConfig>(pending.PreviousConfig);
            var files = NetworkJson.Deserialize<Dictionary<string, string>>(pending.PreviousFiles);
            var plan = NetworkdConfig.Plan(host.OwnedFiles(), files);
            var failed = await host.ApplyAsync(plan, previous ?? new NetworkConfig([], new GlobalNetworkConfig(pending.PreviousHostName, [], [])), cancellationToken);
            if (host.HostName != pending.PreviousHostName)
            {
                failed ??= await host.SetHostNameAsync(pending.PreviousHostName, cancellationToken);
                fileServices.Announce();
            }
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                if (previous is null)
                {
                    await connection.ExecuteAsync("DELETE FROM network_config WHERE id = 1", transaction: transaction);
                }
                else
                {
                    await SaveAsync(connection, transaction, previous);
                }
                await connection.ExecuteAsync("DELETE FROM network_pending WHERE id = 1", transaction: transaction);
                await transaction.CommitAsync(cancellationToken);
            }
            lock (_lock)
            {
                _pending = null;
            }
            LogRolledBack(pending.ChangeId, reason, failed ?? "");
            await PublishAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRollBackFailed(exception);
        }
    }

    /// <summary>Sends the state to the topic and to <see cref="Changed"/>.</summary>
    public async Task PublishAsync(CancellationToken cancellationToken)
    {
        var state = await StateAsync(null, cancellationToken);
        hub.Publish(Topic, state);
        Changed?.Invoke(state);
    }

    private NetworkConfig Default() => new([], new GlobalNetworkConfig(host.HostName, [], []));

    /// <summary>The system's interfaces with what Pithyx keeps for them and whether it may change them.</summary>
    private List<NetworkLink> Links(NetworkConfig config)
    {
        var policy = host.Policy;
        var byName = config.Interfaces.ToDictionary(item => item.Name, StringComparer.Ordinal);
        return [.. host.Links().Select(link =>
        {
            var item = byName.GetValueOrDefault(link.Name);
            var changeable = item is not null ? policy.Changeable(item, config) : link.Kind == InterfaceKind.Ethernet && policy.CardChangeable(link.Name);
            return link with
            {
                Configured = item is not null,
                Changeable = changeable,
                Method = item is null ? link.Master is not null ? "off" : link.Method : !item.Enabled ? "off" : SimulatedNetwork.MethodOf(item.Ipv4),
            };
        })];
    }

    /// <summary>The interfaces a plan touches; a changed name server drop-in touches every card without settings.</summary>
    private static List<string> Affected(NetworkdPlan plan, IReadOnlyList<NetworkLink> links)
    {
        var names = plan.Write.Keys.Concat(plan.Delete).Select(NetworkdConfig.NameOf).OfType<string>().Concat(plan.RemoveNetdevs).ToHashSet(StringComparer.Ordinal);
        if (plan.DefaultFileChanged)
        {
            names.UnionWith(links.Where(link => link.Kind == InterfaceKind.Ethernet && !link.Configured && link.Master is null).Select(link => link.Name));
        }
        return [.. names.Order(StringComparer.Ordinal)];
    }

    /// <summary>The static addresses of the changed interfaces, those of the connection's interface first.</summary>
    private static List<string> Addresses(NetworkConfig config, IReadOnlyList<string> interfaces, string? connection) =>
        [.. config.Interfaces.Where(item => interfaces.Contains(item.Name))
            .OrderBy(item => item.Name == connection || item.Bond?.Members.Contains(connection ?? "") == true ? 0 : 1)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .SelectMany(NetworkValidator.StaticAddresses)];

    private string? ConnectionInterface(IPAddress? localAddress, IReadOnlyList<NetworkLink> links)
    {
        if (localAddress is null)
        {
            return null;
        }
        var address = localAddress.IsIPv4MappedToIPv6 ? localAddress.MapToIPv4() : localAddress;
        if (IPAddress.IsLoopback(address))
        {
            // A simulated box is reached on loopback; it counts as its first card with an address, so the page's warning can be tried.
            return host.Simulated ? links.FirstOrDefault(link => link.Addresses.Count > 0)?.Name : null;
        }
        var text = (address.AddressFamily == AddressFamily.InterNetworkV6 ? new IPAddress(address.GetAddressBytes()) : address).ToString();
        return links.FirstOrDefault(link => link.Addresses.Any(candidate => candidate.Split('/')[0] == text))?.Name;
    }

    private Task<int> SaveAsync(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction? transaction, NetworkConfig config) =>
        connection.ExecuteAsync(
            """
            INSERT INTO network_config (id, config, updated_at) VALUES (1, @config, @now)
            ON CONFLICT (id) DO UPDATE SET config = excluded.config, updated_at = excluded.updated_at
            """,
            new { config = NetworkJson.Serialize(config), now = Format(time.GetUtcNow()) },
            transaction);

    private static string Format(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Information, Message = "Network change {ChangeId} of {Interfaces} from {Origin} applied; it waits for its confirmation")]
    private partial void LogChanged(Guid changeId, IReadOnlyList<string> interfaces, NetworkChangeOrigin origin);

    [LoggerMessage(Level = LogLevel.Information, Message = "Network change {ChangeId} confirmed")]
    private partial void LogConfirmed(Guid changeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Network change {ChangeId} rolled back ({Reason}); failed command: {Failed}")]
    private partial void LogRolledBack(string changeId, string reason, string failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Network reset to DHCP from the console; failed command: {Failed}")]
    private partial void LogReset(string failed);

    [LoggerMessage(Level = LogLevel.Error, Message = "Rolling back a network change failed")]
    private partial void LogRollBackFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The network connection test failed")]
    private partial void LogTestFailed(Exception exception);
}
