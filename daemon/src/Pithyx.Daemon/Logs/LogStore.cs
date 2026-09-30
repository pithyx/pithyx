using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Pithyx.Daemon.Logs;

/// <summary>
/// The current logs of the daemon, the system services and every app, in <c>logs.db</c> next to the daemon's log files
/// on the system disk, so they are there without a pool, with a locked, exported or failed pool, and during boot.
/// Entries are queued in memory and written in batches by <see cref="LogWriter"/>, never one write per line.
/// </summary>
public sealed class LogStore : IDisposable
{
    public const string FileName = "logs.db";
    public const int QueueCapacity = 20_000;

    private const string GenerationKey = "generation";
    private const string JournalCursorKey = "journal_cursor";

    private readonly Channel<PendingLogEntry> _queue = Channel.CreateBounded<PendingLogEntry>(new BoundedChannelOptions(QueueCapacity)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    private readonly string _connectionString;
    private readonly SemaphoreSlim _ready = new(1, 1);
    private bool _created;
    private long _dropped;

    public LogStore(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        Path = System.IO.Path.Combine(directory, FileName);
        _connectionString = LogDatabase.ConnectionString(Path);
    }

    public string Path { get; }

    public void Dispose() => _ready.Dispose();

    internal ChannelReader<PendingLogEntry> Pending => _queue.Reader;

    /// <summary>Queues an entry; never blocks. A full queue drops it and counts it.</summary>
    public void Enqueue(PendingLogEntry entry)
    {
        if (!_queue.Writer.TryWrite(entry))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Entries the full queue dropped since the last call.</summary>
    internal long TakeDropped() => Interlocked.Exchange(ref _dropped, 0);

    internal void Complete() => _queue.Writer.TryComplete();

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (!_created)
        {
            await _ready.WaitAsync(cancellationToken);
            try
            {
                if (!_created)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                    await using var first = await LogDatabase.OpenAsync(_connectionString, cancellationToken);
                    await LogDatabase.EnsureSchemaAsync(first);
                    if (await LogDatabase.MetaAsync(first, GenerationKey) is null)
                    {
                        await LogDatabase.SetMetaAsync(first, GenerationKey, Guid.NewGuid().ToString("N"));
                    }
                    _created = true;
                }
            }
            finally
            {
                _ready.Release();
            }
        }
        return await LogDatabase.OpenAsync(_connectionString, cancellationToken);
    }

    /// <summary>Writes one batch in one transaction, with the journal position of its last journal entry.</summary>
    public async Task WriteAsync(IReadOnlyList<PendingLogEntry> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return;
        }
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await LogDatabase.InsertAsync(connection, transaction, batch);
        if (batch.LastOrDefault(entry => entry.JournalCursor is not null)?.JournalCursor is { } cursor)
        {
            await LogDatabase.SetMetaAsync(connection, JournalCursorKey, cursor, transaction);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Where the journal reader continues after a restart; null before the first journal entry.</summary>
    public async Task<string?> JournalCursorAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await LogDatabase.MetaAsync(connection, JournalCursorKey);
    }

    /// <summary>A random id of this file, so the archive notices when the system disk was replaced.</summary>
    public async Task<string> GenerationAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await LogDatabase.MetaAsync(connection, GenerationKey) ?? "";
    }
}
