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
/// folder with 100,000 entries are built once, the first page is picked from them without sorting the rest, and the full
/// order (a plain ordinal sort of the keys) is built in the background for the pages after it, each a binary search from
/// the cursor. Only the entries that are sent get a stat. Size and date orders stat every entry once and are built on
/// first use.
/// </summary>
internal sealed class FolderSnapshot(string folderId, DateTimeOffset folderChanged, (string Name, FsKind Kind)[] entries, DateTimeOffset createdAt)
{
    private readonly Lock _lock = new();
    private readonly Lock _keysLock = new();
    private readonly Lock _sortLock = new();
    private string[]? _keys;
    private SortedNames? _sortedNames;
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
        if (afterKey is null && Volatile.Read(ref _sortedNames) is null)
        {
            // Sorting every key costs about 1.7 million comparisons for 100,000 entries, and their speed depends on how
            // the JIT tuned the shared string code for the rest of the process. The first page needs only the smallest
            // (or largest) keys, which a selection finds with a fraction of that; the pages after it wait for the sort
            // only if they come quickly.
            var first = FirstByName(order, count);
            _ = Task.Run(Sorted);
            return first;
        }
        return PageByName(Sorted(), afterKey, order, count);
    }

    private static IEnumerable<(string Name, string Key)> PageByName(SortedNames sorted, string? afterKey, SortOrder order, int count)
    {
        var (keys, names) = sorted;
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

    /// <summary>
    /// The first <paramref name="count"/> entries in order. A quickselect with random pivots moves them to the front in
    /// expected linear time, whatever order readdir used (tmpfs, for one, returns the newest entry first); only they are
    /// sorted.
    /// </summary>
    private (string Name, string Key)[] FirstByName(SortOrder order, int count)
    {
        var keys = Keys();
        count = Math.Min(count, keys.Length);
        if (count <= 0)
        {
            return [];
        }
        var sign = order == SortOrder.Asc ? 1 : -1;
        var indexes = new int[keys.Length];
        for (var i = 0; i < indexes.Length; i++)
        {
            indexes[i] = i;
        }
        var (low, high) = (0, indexes.Length - 1);
        while (low < high)
        {
            var pivot = keys[indexes[Random.Shared.Next(low, high + 1)]];
            var (left, right) = (low, high);
            while (left <= right)
            {
                while (sign * string.CompareOrdinal(keys[indexes[left]], pivot) < 0)
                {
                    left++;
                }
                while (sign * string.CompareOrdinal(keys[indexes[right]], pivot) > 0)
                {
                    right--;
                }
                if (left <= right)
                {
                    (indexes[left], indexes[right]) = (indexes[right], indexes[left]);
                    left++;
                    right--;
                }
            }
            // Everything up to right sorts before the pivot, everything from left after it.
            if (count - 1 <= right)
            {
                high = right;
            }
            else if (count - 1 >= left)
            {
                low = left;
            }
            else
            {
                break;
            }
        }
        Array.Sort(indexes, 0, count, Comparer<int>.Create((a, b) => sign * string.CompareOrdinal(keys[a], keys[b])));
        var page = new (string Name, string Key)[count];
        for (var i = 0; i < count; i++)
        {
            page[i] = (Entries[indexes[i]].Name, keys[indexes[i]]);
        }
        return page;
    }

    /// <summary>The natural-order key of every entry, in readdir order.</summary>
    private string[] Keys()
    {
        lock (_keysLock)
        {
            if (_keys is null)
            {
                var keys = new string[Entries.Length];
                // Building the keys is a large part of the work for a big folder; it spreads well over the cores.
                Parallel.ForEach(
                    System.Collections.Concurrent.Partitioner.Create(0, Math.Max(Entries.Length, 1), 4096),
                    range =>
                    {
                        for (var i = range.Item1; i < Math.Min(range.Item2, Entries.Length); i++)
                        {
                            keys[i] = NaturalSort.Key(Entries[i].Name);
                        }
                    });
                _keys = keys;
            }
            return _keys;
        }
    }

    private SortedNames Sorted()
    {
        lock (_sortLock)
        {
            if (_sortedNames is not { } sorted)
            {
                var keys = (string[])Keys().Clone();
                var names = Array.ConvertAll(Entries, entry => entry.Name);
                Array.Sort(keys, names, StringComparer.Ordinal);
                sorted = new SortedNames(keys, names);
                Volatile.Write(ref _sortedNames, sorted);
            }
            return sorted;
        }
    }

    private sealed record SortedNames(string[] Keys, string[] Names);

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
