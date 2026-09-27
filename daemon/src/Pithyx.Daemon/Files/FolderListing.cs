using System.Buffers.Text;
using System.Text.Json;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Files;

public enum FileSort
{
    Name,
    Size,
    Modified,
}

public enum SortOrder
{
    Asc,
    Desc,
}

/// <summary>
/// The names of one folder at one moment, as readdir returned them. Name order needs no stat per entry: the keys of a
/// folder with 100,000 entries are built and sorted once (a plain ordinal sort of strings, whatever order readdir
/// used), and every page is a binary search from the cursor. Only the entries that are sent get a stat. Size and date
/// orders stat every entry once and are built on first use.
/// </summary>
internal sealed class FolderSnapshot(string folderId, DateTimeOffset folderChanged, (string Name, FsKind Kind)[] entries, DateTimeOffset createdAt)
{
    private readonly Lock _lock = new();
    private string[]? _keys;
    private string[]? _names;
    private (long Value, string Key, string Name)[]? _bySize;
    private (long Value, string Key, string Name)[]? _byModified;

    public string FolderId { get; } = folderId;

    public DateTimeOffset FolderChanged { get; } = folderChanged;

    public DateTimeOffset CreatedAt { get; } = createdAt;

    public (string Name, FsKind Kind)[] Entries { get; } = entries;

    public int Count => Entries.Length;

    /// <summary>Up to <paramref name="count"/> entries in natural order after the entry with key <paramref name="afterKey"/>.</summary>
    public IEnumerable<(string Name, string Key)> ByName(string? afterKey, SortOrder order, int count)
    {
        var (keys, names) = SortedByName();
        var ascending = order == SortOrder.Asc;
        var start = ascending ? 0 : keys.Length - 1;
        if (afterKey is not null)
        {
            var found = Array.BinarySearch(keys, afterKey, StringComparer.Ordinal);
            // After an exact match, or at the insertion point when that entry is gone.
            start = found >= 0 ? (ascending ? found + 1 : found - 1) : (ascending ? ~found : ~found - 1);
        }
        for (var i = start; i >= 0 && i < keys.Length && count-- > 0; i += ascending ? 1 : -1)
        {
            yield return (names[i], keys[i]);
        }
    }

    private (string[] Keys, string[] Names) SortedByName()
    {
        lock (_lock)
        {
            if (_keys is null || _names is null)
            {
                var keys = new string[Entries.Length];
                var names = new string[Entries.Length];
                // Building the keys is a large part of the work for a big folder; it spreads well over the cores.
                Parallel.ForEach(
                    System.Collections.Concurrent.Partitioner.Create(0, Math.Max(Entries.Length, 1), 4096),
                    range =>
                    {
                        for (var i = range.Item1; i < Math.Min(range.Item2, Entries.Length); i++)
                        {
                            names[i] = Entries[i].Name;
                            keys[i] = NaturalSort.Key(Entries[i].Name);
                        }
                    });
                Array.Sort(keys, names, StringComparer.Ordinal);
                _keys = keys;
                _names = names;
            }
            return (_keys, _names);
        }
    }

    /// <summary>Entries sorted by a metadata value, then by name, built once per snapshot from <paramref name="stat"/>.</summary>
    public (long Value, string Key, string Name)[] ByMetadata(FileSort sort, Func<string, FsStat?> stat)
    {
        lock (_lock)
        {
            ref var cached = ref sort == FileSort.Size ? ref _bySize : ref _byModified;
            if (cached is null)
            {
                var values = new (long Value, string Key, string Name)[Entries.Length];
                Parallel.For(0, Entries.Length, i =>
                {
                    var name = Entries[i].Name;
                    var entry = stat(name);
                    var value = entry is null ? long.MinValue : sort == FileSort.Size ? (entry.Kind == FsKind.Folder ? -1 : entry.Size) : entry.Modified.UtcTicks;
                    values[i] = (value, NaturalSort.Key(name), name);
                });
                Array.Sort(values, MetadataOrder.Instance);
                cached = values;
            }
            return cached;
        }
    }
}

/// <summary>Value first, then the name's key, so the order is total and a cursor is exact.</summary>
internal sealed class MetadataOrder : IComparer<(long Value, string Key, string Name)>
{
    public static readonly MetadataOrder Instance = new();

    public int Compare((long Value, string Key, string Name) x, (long Value, string Key, string Name) y) =>
        x.Value != y.Value ? x.Value.CompareTo(y.Value) : string.CompareOrdinal(x.Key, y.Key);
}

/// <summary>Natural order ("file2" before "file10"), ignoring case, as an ordinal-comparable key.</summary>
public static class NaturalSort
{
    /// <summary>Compares two names exactly as their <see cref="Key"/>s compare, without building the keys.</summary>
    public static int Compare(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var left = new KeyReader(a);
        var right = new KeyReader(b);
        while (true)
        {
            int x = left.Next(), y = right.Next();
            if (x != y)
            {
                return x < y ? -1 : 1;
            }
            if (x < 0)
            {
                return 0;
            }
        }
    }

    /// <summary>Compares a name's key with a key, without building the name's key.</summary>
    public static int CompareToKey(string name, string key)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(key);
        var reader = new KeyReader(name);
        for (var i = 0; ; i++)
        {
            int x = reader.Next(), y = i < key.Length ? key[i] : -1;
            if (x != y)
            {
                return x < y ? -1 : 1;
            }
            if (x < 0)
            {
                return 0;
            }
        }
    }

    /// <summary>Yields the characters of <see cref="Key"/> one by one; -1 at the end.</summary>
    private ref struct KeyReader(string name)
    {
        private readonly string _name = name;
        private int _at;
        private bool _raw;
        private int _rawAt;
        private ReadOnlySpan<char> _digits;
        // -2: the marker is next, -1: the length is next, 0 and up: the digit at that position.
        private int _digit = int.MinValue;

        public int Next()
        {
            if (_raw)
            {
                return _rawAt < _name.Length ? _name[_rawAt++] : -1;
            }
            if (_digit != int.MinValue)
            {
                if (_digit == -2)
                {
                    _digit = -1;
                    return '0';
                }
                if (_digit == -1)
                {
                    _digit = 0;
                    return '0' + _digits.Length;
                }
                if (_digit < _digits.Length)
                {
                    return _digits[_digit++];
                }
                _digit = int.MinValue;
            }
            if (_at >= _name.Length)
            {
                _raw = true;
                return 0;
            }
            var c = _name[_at];
            if (char.IsAsciiDigit(c))
            {
                var start = _at;
                while (_at < _name.Length && char.IsAsciiDigit(_name[_at]))
                {
                    _at++;
                }
                _digits = _name.AsSpan(start, _at - start).TrimStart('0');
                _digit = -1;
                return '0';
            }
            _at++;
            return c < 0x80 ? (char.IsAsciiLetterLower(c) ? c - 32 : c) : char.ToUpperInvariant(c);
        }
    }

    /// <summary>
    /// Digit runs become a marker, their length and their digits without leading zeros, so an ordinal comparison of two
    /// keys compares numbers by value; letters are upper-cased. The name itself ends the key, so keys are unique.
    /// </summary>
    public static string Key(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        // A digit run of length n becomes at most n + 2 characters, so three per character plus the name always fit.
        var length = name.Length * 4 + 1;
        var buffer = length <= 1024 ? stackalloc char[length] : new char[length];
        var at = 0;
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsAsciiDigit(c))
            {
                var start = i;
                while (i + 1 < name.Length && char.IsAsciiDigit(name[i + 1]))
                {
                    i++;
                }
                var digits = name.AsSpan(start, i - start + 1).TrimStart('0');
                buffer[at++] = '0';
                buffer[at++] = (char)('0' + digits.Length);
                digits.CopyTo(buffer[at..]);
                at += digits.Length;
            }
            else
            {
                buffer[at++] = c < 0x80 ? (char.IsAsciiLetterLower(c) ? (char)(c - 32) : c) : char.ToUpperInvariant(c);
            }
        }
        buffer[at++] = '\0';
        name.CopyTo(buffer[at..]);
        at += name.Length;
        return new string(buffer[..at]);
    }
}

/// <summary>
/// Keyset cursors: the sort, the order and the position of the last entry sent. A cursor stays valid when entries are
/// added or removed; the next page starts after that position.
/// </summary>
public static class FolderCursor
{
    private sealed record State(string S, string O, string K, long V);

    public static string Encode(FileSort sort, SortOrder order, string key, long value)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new State(sort.ToString(), order.ToString(), key, value));
        return Base64Url.EncodeToString(json);
    }

    public static (string Key, long Value) Decode(string cursor, FileSort sort, SortOrder order)
    {
        try
        {
            var state = JsonSerializer.Deserialize<State>(Base64Url.DecodeFromChars(cursor));
            if (state is null || state.S != sort.ToString() || state.O != order.ToString() || state.K is null)
            {
                throw new PithyxException(ErrorCodes.FsCursorInvalid);
            }
            return (state.K, state.V);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new PithyxException(ErrorCodes.FsCursorInvalid, null, exception);
        }
    }
}
