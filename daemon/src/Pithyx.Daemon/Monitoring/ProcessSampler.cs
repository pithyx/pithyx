using System.Globalization;

namespace Pithyx.Daemon.Monitoring;

/// <summary>
/// Lists processes from <c>/proc/&lt;pid&gt;</c>: name and times from <c>stat</c>, the owner from <c>status</c> once
/// per process, user names from <c>/etc/passwd</c>. No process is started for it. Kernel threads are left out.
/// </summary>
public sealed class ProcessSampler(ResourceFiles files, TimeProvider time)
{
    private readonly Lock _lock = new();
    private readonly int _pageSize = Environment.SystemPageSize;
    private Dictionary<(int Pid, long Start), long> _previousTicks = [];
    private long? _previousTotal;
    private Dictionary<(int Pid, long Start), string> _owners = [];
    private (DateTime Written, IReadOnlyDictionary<int, string> Names)? _passwd;

    /// <summary>How many lists were read; the tests check that nobody watching costs nothing.</summary>
    public long SampleCount { get; private set; }

    /// <summary>Forgets the previous list, so the next one has no CPU shares.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _previousTicks = [];
            _previousTotal = null;
        }
    }

    /// <summary>
    /// Every process with its CPU share since the previous call. The share is measured against the jiffies of all
    /// cores together, so a process that keeps one of four cores busy shows 25 percent.
    /// </summary>
    public ProcessList Sample()
    {
        lock (_lock)
        {
            SampleCount++;
            var total = ResourceParsers.ParseCpuTimes(File.ReadAllText(Path.Combine(files.Proc, "stat")))[0].Total;
            var elapsed = _previousTotal is { } previousTotal ? total - previousTotal : 0;
            var names = UserNames();
            var ticks = new Dictionary<(int Pid, long Start), long>();
            var owners = new Dictionary<(int Pid, long Start), string>();
            var processes = new List<ProcessInfo>();
            foreach (var directory in Directory.EnumerateDirectories(files.Proc))
            {
                if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out _)
                    || ReadStat(directory) is not { } stat
                    || (stat.Flags & ResourceParsers.KernelThreadFlag) != 0)
                {
                    continue;
                }
                var key = (stat.Pid, stat.StartTicks);
                ticks[key] = stat.CpuTicks;
                if (!_owners.TryGetValue(key, out var owner))
                {
                    owner = OwnerOf(directory, names);
                }
                if (owner is null)
                {
                    // The process ended between reading its stat and its status.
                    continue;
                }
                owners[key] = owner;
                double? cpu = elapsed > 0 && _previousTicks.TryGetValue(key, out var before)
                    ? Math.Round(Math.Clamp(100.0 * (stat.CpuTicks - before) / elapsed, 0, 100), 1)
                    : null;
                processes.Add(new ProcessInfo(stat.Pid, stat.Name, owner, cpu, stat.RssPages * _pageSize));
            }
            _previousTicks = ticks;
            _previousTotal = total;
            _owners = owners;
            return new ProcessList(time.GetUtcNow(), processes);
        }
    }

    private static ProcessStat? ReadStat(string directory)
    {
        try
        {
            return ResourceParsers.ParseProcessStat(File.ReadAllText(Path.Combine(directory, "stat")));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? OwnerOf(string directory, IReadOnlyDictionary<int, string> names)
    {
        try
        {
            var uid = ResourceParsers.ParseUid(File.ReadAllText(Path.Combine(directory, "status")));
            return uid is null ? null : names.GetValueOrDefault(uid.Value) ?? uid.Value.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary><c>/etc/passwd</c>, read again only when it changed.</summary>
    private IReadOnlyDictionary<int, string> UserNames()
    {
        var written = File.Exists(files.Passwd) ? File.GetLastWriteTimeUtc(files.Passwd) : DateTime.MinValue;
        if (_passwd is not { } cached || cached.Written != written)
        {
            _passwd = (written, written == DateTime.MinValue ? new Dictionary<int, string>() : ResourceParsers.ParsePasswd(File.ReadAllText(files.Passwd)));
        }
        return _passwd.Value.Names;
    }
}
