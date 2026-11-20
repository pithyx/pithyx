using System.Globalization;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Pithyx.Daemon.Files.Search;

/// <summary>A folder or file of the index, as a child of a folder.</summary>
internal sealed record IndexedChild(long Id, string Name, bool Folder, string? Node);

/// <summary>A name that matched a search, before its path is known and it is checked on disk.</summary>
internal sealed record SearchCandidate(long Id, long ShareId, string Name, long Folder);

/// <summary>
/// One name index (D413): a SQLite database in a pool's cache dataset (<c>&lt;pool&gt;/pithyx/cache</c>, folder
/// <c>search</c>), so it leaves with its pool and never fills the system disk. It holds the folders and files of every
/// indexed share and home on that pool as a tree of names, with an FTS5 trigram table over the names for substring
/// matches. It is derived data: a database of another version is thrown away and built again. Content search can be
/// added as a second FTS table keyed by the same entry ids.
/// </summary>
internal sealed class SearchStore(string directory) : IDisposable
{
    public const string Folder = "search";
    public const string FileName = "names.db";
    private const int Version = 1;

    private const string Schema = """
        CREATE TABLE roots (
            share_id INTEGER PRIMARY KEY,
            path TEXT NOT NULL,
            entry INTEGER NOT NULL,
            complete INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE entries (
            id INTEGER PRIMARY KEY,
            share_id INTEGER NOT NULL,
            parent INTEGER REFERENCES entries (id) ON DELETE CASCADE,
            name TEXT NOT NULL,
            key TEXT NOT NULL,
            folder INTEGER NOT NULL,
            node TEXT,
            stamp INTEGER
        );
        CREATE UNIQUE INDEX entries_children ON entries (parent, name);
        CREATE INDEX entries_key ON entries (key);
        CREATE INDEX entries_share ON entries (share_id);
        CREATE VIRTUAL TABLE names USING fts5 (key, content = 'entries', content_rowid = 'id', tokenize = 'trigram');
        CREATE TRIGGER entries_added AFTER INSERT ON entries BEGIN
            INSERT INTO names (rowid, key) VALUES (new.id, new.key);
        END;
        CREATE TRIGGER entries_removed AFTER DELETE ON entries BEGIN
            INSERT INTO names (names, rowid, key) VALUES ('delete', old.id, old.key);
        END;
        CREATE TRIGGER entries_renamed AFTER UPDATE OF key ON entries BEGIN
            INSERT INTO names (names, rowid, key) VALUES ('delete', old.id, old.key);
            INSERT INTO names (rowid, key) VALUES (new.id, new.key);
        END;
        """;

    private SqliteConnection? _writer;

    public string Directory { get; } = directory;

    public string FilePath => Path.Combine(Directory, FileName);

    /// <summary>Whether the database is open for writing; a closed store holds no file on its pool.</summary>
    public bool IsOpen => _writer is not null;

    /// <summary>The connection, opened (and the schema made) on first use. Only under the index's gate.</summary>
    public SqliteConnection Writer => _writer ??= OpenWriter();

    /// <summary>The key a name is matched by: composed (NFC) and lower case, so a name a client wrote decomposed still matches.</summary>
    public static string KeyOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    /// <summary>Lets go of the database file, so its dataset can be unmounted. Indexer thread or under its gate.</summary>
    public void Close()
    {
        _writer?.Dispose();
        _writer = null;
    }

    public void Dispose() => Close();

    /// <summary>The root entry of a share, made when the share is new here or its folder moved; <paramref name="fresh"/> says so.</summary>
    public long EnsureRoot(long shareId, string path, out bool fresh)
    {
        var connection = Writer;
        var existing = connection.QuerySingleOrDefault<(string Path, long Entry)?>("SELECT path, entry FROM roots WHERE share_id = @shareId", new { shareId });
        if (existing is { } row && row.Path == path)
        {
            fresh = false;
            return row.Entry;
        }
        using var transaction = connection.BeginTransaction();
        connection.Execute("DELETE FROM entries WHERE share_id = @shareId; DELETE FROM roots WHERE share_id = @shareId", new { shareId }, transaction);
        var entry = connection.ExecuteScalar<long>(
            "INSERT INTO entries (share_id, parent, name, key, folder) VALUES (@shareId, NULL, '', '', 1) RETURNING id", new { shareId }, transaction);
        connection.Execute("INSERT INTO roots (share_id, path, entry) VALUES (@shareId, @path, @entry)", new { shareId, path, entry }, transaction);
        transaction.Commit();
        fresh = true;
        return entry;
    }

    /// <summary>How many entries the database holds, every share's together.</summary>
    public long Count() => Writer.ExecuteScalar<long>("SELECT count(*) FROM entries");

    /// <summary>The shares this database holds.</summary>
    public IReadOnlyList<long> Shares() => [.. Writer.Query<long>("SELECT share_id FROM roots")];

    public void RemoveRoot(long shareId)
    {
        using var transaction = Writer.BeginTransaction();
        Writer.Execute("DELETE FROM entries WHERE share_id = @shareId; DELETE FROM roots WHERE share_id = @shareId", new { shareId }, transaction);
        transaction.Commit();
    }

    public bool IsComplete(long shareId) => Writer.ExecuteScalar<long?>("SELECT complete FROM roots WHERE share_id = @shareId", new { shareId }) == 1;

    public void SetComplete(long shareId) => Writer.Execute("UPDATE roots SET complete = 1 WHERE share_id = @shareId", new { shareId });

    /// <summary>When a folder's entries last changed as the index saw it (its mtime or ctime, whichever is later), and its device and inode.</summary>
    public (long? Stamp, string? Node) FolderState(long id) =>
        Writer.QuerySingleOrDefault<(long?, string?)>("SELECT stamp, node FROM entries WHERE id = @id", new { id });

    public IReadOnlyList<IndexedChild> Children(long parent) =>
        [.. Writer.Query<(long Id, string Name, long Folder, string? Node)>("SELECT id, name, folder, node FROM entries WHERE parent = @parent", new { parent })
            .Select(row => new IndexedChild(row.Id, row.Name, row.Folder == 1, row.Node))];

    public IReadOnlyList<(long Id, string Name)> ChildFolders(long parent) =>
        [.. Writer.Query<(long, string)>("SELECT id, name FROM entries WHERE parent = @parent AND folder = 1", new { parent })];

    /// <summary>The folder at <paramref name="path"/> below a root entry, or the deepest folder the index knows on the way and the rest of the path.</summary>
    public (long Id, string Path) Deepest(long root, string path)
    {
        var id = root;
        var known = "";
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var child = Writer.ExecuteScalar<long?>("SELECT id FROM entries WHERE parent = @id AND name = @segment AND folder = 1", new { id, segment });
            if (child is null)
            {
                break;
            }
            id = child.Value;
            known = FsPath.Join(known, segment);
        }
        return (id, known);
    }

    /// <summary>
    /// Writes what a folder holds now: removes the entries that went (with everything below them), renames folders that
    /// kept their inode, adds the new ones and stores the folder's stamp. Returns the ids of the new folders by name.
    /// </summary>
    public Dictionary<string, long> Apply(long shareId, long folder, long stamp, string node, IReadOnlyCollection<long> removed, IReadOnlyCollection<(long Id, string Name)> renamed, IReadOnlyCollection<(string Name, bool Folder, string? Node)> added)
    {
        var connection = Writer;
        using var transaction = connection.BeginTransaction();
        foreach (var id in removed)
        {
            connection.Execute("DELETE FROM entries WHERE id = @id", new { id }, transaction);
        }
        foreach (var (id, name) in renamed)
        {
            connection.Execute("UPDATE entries SET name = @name, key = @key WHERE id = @id", new { id, name, key = KeyOf(name) }, transaction);
        }
        var folders = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (name, isFolder, childNode) in added)
        {
            var id = connection.ExecuteScalar<long?>(
                "INSERT OR IGNORE INTO entries (share_id, parent, name, key, folder, node) VALUES (@shareId, @folder, @name, @key, @isFolder, @childNode) RETURNING id",
                new { shareId, folder, name, key = KeyOf(name), isFolder = isFolder ? 1 : 0, childNode },
                transaction);
            if (isFolder && id is { } newId)
            {
                folders[name] = newId;
            }
        }
        connection.Execute("UPDATE entries SET stamp = @stamp, node = @node WHERE id = @folder", new { stamp, node, folder }, transaction);
        transaction.Commit();
        return folders;
    }

    /// <summary>
    /// Names of the given shares that contain every term: first those that start with the first term (the B-tree on
    /// the keys), then any other match (the trigram table, which needs a term of three or more characters; shorter
    /// terms only filter). A search of short terms only finds names that start with the first.
    /// </summary>
    public static List<SearchCandidate> Candidates(SqliteConnection reader, IReadOnlyCollection<long> shareIds, IReadOnlyList<string> terms, int cap)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(terms);
        var parameters = new DynamicParameters();
        parameters.Add("shares", shareIds);
        parameters.Add("cap", cap);
        var filters = new StringBuilder();
        for (var i = 0; i < terms.Count; i++)
        {
            parameters.Add($"like{i}", "%" + EscapeLike(terms[i]) + "%");
            filters.Append(CultureInfo.InvariantCulture, $" AND e.key LIKE @like{i} ESCAPE '\\'");
        }
        parameters.Add("prefix", terms[0]);
        parameters.Add("prefixEnd", terms[0] + char.ConvertFromUtf32(0x10FFFF));
        const string columns = "SELECT e.id AS Id, e.share_id AS ShareId, e.name AS Name, e.folder AS Folder FROM ";
        var found = reader.Query<SearchCandidate>(
            columns + "entries e WHERE e.key >= @prefix AND e.key < @prefixEnd AND e.parent IS NOT NULL AND e.share_id IN @shares" + filters + " LIMIT @cap",
            parameters).ToList();
        var trigramTerms = terms.Where(term => term.Length >= 3).ToList();
        if (trigramTerms.Count > 0 && found.Count < cap)
        {
            parameters.Add("match", string.Join(' ', trigramTerms.Select(term => "\"" + term.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")));
            var seen = found.Select(candidate => candidate.Id).ToHashSet();
            found.AddRange(reader.Query<SearchCandidate>(
                columns + "names JOIN entries e ON e.id = names.rowid WHERE names MATCH @match AND e.parent IS NOT NULL AND e.share_id IN @shares" + filters + " LIMIT @cap",
                parameters).Where(candidate => !seen.Contains(candidate.Id)));
        }
        return found;
    }

    /// <summary>The path of an entry relative to its share root.</summary>
    public static string PathOf(SqliteConnection reader, long id)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var names = reader.Query<string>(
            """
            WITH RECURSIVE up (id, parent, name, depth) AS (
                SELECT id, parent, name, 0 FROM entries WHERE id = @id
                UNION ALL SELECT e.id, e.parent, e.name, up.depth + 1 FROM entries e JOIN up ON e.id = up.parent
            )
            SELECT name FROM up WHERE parent IS NOT NULL ORDER BY depth DESC
            """,
            new { id });
        return string.Join('/', names);
    }

    /// <summary>Which of the shares have been read completely at least once.</summary>
    public static HashSet<long> CompleteShares(SqliteConnection reader) =>
        [.. reader.Query<long>("SELECT share_id FROM roots WHERE complete = 1")];

    private static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = FilePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        // Without pooling a closed connection really closes the file, so the dataset can be unmounted.
        Pooling = false,
        DefaultTimeout = 30,
    }.ToString();

    private SqliteConnection OpenWriter()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            if (OperatingSystem.IsLinux())
            {
                System.IO.Directory.CreateDirectory(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            else
            {
                System.IO.Directory.CreateDirectory(Directory);
            }
        }
        var connection = Connect();
        if (connection.ExecuteScalar<long>("PRAGMA user_version") is var version and not 0 and not Version)
        {
            connection.Dispose();
            foreach (var file in new[] { FilePath, FilePath + "-wal", FilePath + "-shm" })
            {
                File.Delete(file);
            }
            connection = Connect();
        }
        if (connection.ExecuteScalar<long>("PRAGMA user_version") == 0)
        {
            connection.Execute(Schema + $"PRAGMA user_version = {Version};");
        }
        return connection;
    }

    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        // The index is rebuilt from the disks, so a lost transaction after a power cut costs a walk, not data.
        connection.Execute("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;");
        return connection;
    }
}
