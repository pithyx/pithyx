using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pithyx.Daemon.Files;

/// <summary>
/// The Linux calls the file service needs, on directory handles. Every path below a share root is opened with
/// <c>openat2(RESOLVE_BENEATH | RESOLVE_NO_MAGICLINKS)</c>, so the kernel refuses anything that would leave the share,
/// including symlinks swapped in while an operation runs. Structures and syscall numbers used here are the same on
/// x86-64 and arm64; the open flags that differ are chosen per architecture.
/// </summary>
internal static unsafe partial class LinuxFs
{
    private const long SysOpenat2 = 437;
    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const int AtRemoveDir = 0x200;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxBasicStats = 0x7ff;
    private const uint RenameNoReplace = 1;

    public const int ReadOnly = 0;
    public const int WriteOnly = 1;
    public const int Create = 0x40;
    public const int Exclusive = 0x80;
    public const int Truncate = 0x200;
    public const int Path = 0x200000;
    private const int CloseOnExec = 0x80000;

    private const ulong ResolveNoMagicLinks = 0x02;
    private const ulong ResolveBeneath = 0x08;

    public const uint InCloseWrite = 0x08;
    public const uint InAttrib = 0x04;
    public const uint InMovedFrom = 0x40;
    public const uint InMovedTo = 0x80;
    public const uint InCreate = 0x100;
    public const uint InDelete = 0x200;
    public const uint InDeleteSelf = 0x400;
    public const uint InMoveSelf = 0x800;
    public const uint InQueueOverflow = 0x4000;
    public const uint InIgnored = 0x8000;
    public const uint InOnlyDir = 0x1000000;
    private const int InNonBlock = 0x800;

    public const int ENOENT = 2;
    public const int EINTR = 4;
    public const int EAGAIN = 11;
    public const int EACCES = 13;
    public const int EEXIST = 17;
    public const int EXDEV = 18;
    public const int ENOTDIR = 20;
    public const int EISDIR = 21;
    public const int EINVAL = 22;
    public const int ENOSPC = 28;
    public const int EROFS = 30;
    public const int ENAMETOOLONG = 36;
    public const int ENOSYS = 38;
    public const int ENOTEMPTY = 39;
    public const int ELOOP = 40;
    public const int EOPNOTSUPP = 95;
    public const int EPERM = 1;

    private static readonly bool _arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    public static int Directory => _arm ? 0x4000 : 0x10000;

    public static int NoFollow => _arm ? 0x8000 : 0x20000;

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow
    {
        public ulong Flags;
        public ulong Mode;
        public ulong Resolve;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer
    {
        [FieldOffset(16)] public uint Nlink;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(24)] public uint Gid;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(96)] public long ChangedSeconds;
        [FieldOffset(104)] public uint ChangedNanoseconds;
        [FieldOffset(112)] public long ModifiedSeconds;
        [FieldOffset(120)] public uint ModifiedNanoseconds;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long SyscallOpenat2(long number, int directory, byte* path, OpenHow* how, nuint size);

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static partial int OpenAt(int directory, byte* path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static partial int StatxCall(int directory, byte* path, int flags, uint mask, StatxBuffer* buffer);

    [LibraryImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    private static partial nint FdOpenDir(int descriptor);

    // Without SetLastError: an error and the end of the directory both end the listing, and errno costs on every entry.
    [LibraryImport("libc", EntryPoint = "readdir64")]
    private static partial byte* ReadDir(nint directory);

    [LibraryImport("libc", EntryPoint = "closedir")]
    private static partial int CloseDir(nint directory);

    [LibraryImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static partial int Dup(int descriptor);

    [LibraryImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static partial int RenameAt2(int fromDirectory, byte* fromName, int toDirectory, byte* toName, uint flags);

    [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static partial int UnlinkAt(int directory, byte* name, int flags);

    [LibraryImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static partial int MkdirAt(int directory, byte* name, uint mode);

    [LibraryImport("libc", EntryPoint = "copy_file_range", SetLastError = true)]
    private static partial nint CopyFileRange(int input, long* inputOffset, int output, long* outputOffset, nuint length, uint flags);

    [LibraryImport("libc", EntryPoint = "inotify_init1", SetLastError = true)]
    private static partial int InotifyInit1(int flags);

    [LibraryImport("libc", EntryPoint = "inotify_add_watch", SetLastError = true)]
    private static partial int InotifyAddWatch(int descriptor, byte* path, uint mask);

    [LibraryImport("libc", EntryPoint = "inotify_rm_watch", SetLastError = true)]
    private static partial int InotifyRemoveWatch(int descriptor, int watch);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static partial nint Read(int descriptor, byte* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static partial nint Write(int descriptor, byte* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "eventfd", SetLastError = true)]
    private static partial int EventFd(uint initial, int flags);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static partial int PollCall(PollFd* descriptors, nuint count, int timeout);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Descriptor;
        public short Events;
        public short ReturnedEvents;
    }

    /// <summary>Opens a share root (an absolute path the administrator configured) as a directory handle.</summary>
    public static SafeFileHandle OpenRoot(string path)
    {
        var bytes = Utf8(path);
        fixed (byte* pointer = bytes)
        {
            var fd = OpenAt(AtFdCwd, pointer, Path | Directory | CloseOnExec, 0);
            return fd >= 0 ? new SafeFileHandle(fd, ownsHandle: true) : throw Failure(path);
        }
    }

    /// <summary>
    /// Opens <paramref name="relative"/> below <paramref name="root"/>; "." is the root itself. Fails with EXDEV or
    /// ELOOP when the path would leave the root.
    /// </summary>
    public static SafeFileHandle OpenBeneath(SafeFileHandle root, string relative, int flags, uint mode = 0)
    {
        ArgumentNullException.ThrowIfNull(root);
        var bytes = Utf8(relative);
        var how = new OpenHow
        {
            Flags = (ulong)(uint)(flags | CloseOnExec),
            Mode = (flags & Create) != 0 ? mode : 0,
            Resolve = ResolveBeneath | ResolveNoMagicLinks,
        };
        var added = false;
        root.DangerousAddRef(ref added);
        try
        {
            fixed (byte* pointer = bytes)
            {
                while (true)
                {
                    var fd = SyscallOpenat2(SysOpenat2, (int)root.DangerousGetHandle(), pointer, &how, (nuint)sizeof(OpenHow));
                    if (fd >= 0)
                    {
                        return new SafeFileHandle((nint)fd, ownsHandle: true);
                    }
                    // openat2 may ask for a retry when a rename raced with the path walk.
                    if (Marshal.GetLastPInvokeError() is not (EAGAIN or EINTR))
                    {
                        throw Failure(relative);
                    }
                }
            }
        }
        finally
        {
            if (added)
            {
                root.DangerousRelease();
            }
        }
    }

    /// <summary>Metadata of an entry in <paramref name="directory"/> without following a final symlink, or of the handle itself for "".</summary>
    public static FsStat Stat(SafeFileHandle directory, string name)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var bytes = Utf8(name);
        var buffer = default(StatxBuffer);
        var flags = name.Length == 0 ? AtEmptyPath : AtSymlinkNoFollow;
        var added = false;
        directory.DangerousAddRef(ref added);
        try
        {
            fixed (byte* pointer = bytes)
            {
                if (StatxCall((int)directory.DangerousGetHandle(), pointer, flags, StatxBasicStats, &buffer) != 0)
                {
                    throw Failure(name);
                }
            }
        }
        finally
        {
            if (added)
            {
                directory.DangerousRelease();
            }
        }
        return new FsStat(
            KindOf(buffer.Mode),
            (long)buffer.Size,
            DateTimeOffset.FromUnixTimeSeconds(buffer.ModifiedSeconds).AddTicks(buffer.ModifiedNanoseconds / 100),
            DateTimeOffset.FromUnixTimeSeconds(buffer.ChangedSeconds).AddTicks(buffer.ChangedNanoseconds / 100),
            $"{buffer.DeviceMajor:x}.{buffer.DeviceMinor:x}.{buffer.Inode:x}",
            buffer.Uid,
            buffer.Gid);
    }

    /// <summary>
    /// The names in a directory (without "." and ".."), with the kind readdir reports, without a stat per entry.
    /// Names that are not valid UTF-8 are decoded with replacement characters.
    /// </summary>
    public static List<(string Name, FsKind Kind)> ReadNames(SafeFileHandle directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var copy = WithHandle(directory, Dup);
        if (copy < 0)
        {
            throw Failure("");
        }
        var stream = FdOpenDir(copy);
        if (stream == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            using var _ = new SafeFileHandle(copy, ownsHandle: true);
            throw new FsError(error, "");
        }
        try
        {
            var names = new List<(string, FsKind)>();
            while (true)
            {
                var entry = ReadDir(stream);
                if (entry == null)
                {
                    break;
                }
                // The record length bounds the name; reading a fixed 256 bytes could run past the buffer.
                var record = *(ushort*)(entry + 16);
                var name = new ReadOnlySpan<byte>(entry + 19, record - 19);
                name = name[..name.IndexOf((byte)0)];
                if (name is [(byte)'.'] or [(byte)'.', (byte)'.'])
                {
                    continue;
                }
                names.Add((Encoding.UTF8.GetString(name), DirentKind(entry[18])));
            }
            return names;
        }
        finally
        {
            _ = CloseDir(stream);
        }
    }

    /// <summary>Renames within or across directories of one file system; replaces an existing entry only with <paramref name="replace"/>.</summary>
    public static void Rename(SafeFileHandle fromDirectory, string fromName, SafeFileHandle toDirectory, string toName, bool replace = false)
    {
        var from = Utf8(fromName);
        var to = Utf8(toName);
        var result = WithHandle(fromDirectory, source => WithHandle(toDirectory, target =>
        {
            fixed (byte* fromPointer = from)
            fixed (byte* toPointer = to)
            {
                return RenameAt2(source, fromPointer, target, toPointer, replace ? 0 : RenameNoReplace);
            }
        }));
        if (result != 0)
        {
            throw Failure(fromName);
        }
    }

    public static void Unlink(SafeFileHandle directory, string name, bool folder)
    {
        var bytes = Utf8(name);
        var result = WithHandle(directory, fd =>
        {
            fixed (byte* pointer = bytes)
            {
                return UnlinkAt(fd, pointer, folder ? AtRemoveDir : 0);
            }
        });
        if (result != 0)
        {
            throw Failure(name);
        }
    }

    public static void MakeDirectory(SafeFileHandle directory, string name, uint mode = 0x1ed)
    {
        var bytes = Utf8(name);
        var result = WithHandle(directory, fd =>
        {
            fixed (byte* pointer = bytes)
            {
                return MkdirAt(fd, pointer, mode);
            }
        });
        if (result != 0)
        {
            throw Failure(name);
        }
    }

    /// <summary>
    /// Copies up to <paramref name="length"/> bytes in the kernel (file systems may clone the blocks). Returns the
    /// bytes copied, 0 at the end of the input, or -1 when the file system cannot do it and the caller must copy itself.
    /// </summary>
    public static long CopyRange(SafeFileHandle input, SafeFileHandle output, long length)
    {
        var copied = WithHandle(input, source => WithHandle(output, target => (long)CopyFileRange(source, null, target, null, (nuint)length, 0)));
        if (copied >= 0)
        {
            return copied;
        }
        var error = Marshal.GetLastPInvokeError();
        return error is EXDEV or EINVAL or ENOSYS or EOPNOTSUPP ? -1 : throw new FsError(error, "");
    }

    public static SafeFileHandle InotifyInit()
    {
        var fd = InotifyInit1(InNonBlock | CloseOnExec);
        return fd >= 0 ? new SafeFileHandle(fd, ownsHandle: true) : throw Failure("");
    }

    /// <summary>
    /// Watches the directory behind an open handle through its /proc/self/fd link (so not with IN_DONT_FOLLOW); the handle
    /// pins the directory, so no path can be swapped in.
    /// </summary>
    public static int InotifyWatch(SafeFileHandle inotify, SafeFileHandle directory, uint mask)
    {
        var path = Utf8($"/proc/self/fd/{WithHandle(directory, fd => fd)}");
        var watch = WithHandle(inotify, fd =>
        {
            fixed (byte* pointer = path)
            {
                return InotifyAddWatch(fd, pointer, mask | InOnlyDir);
            }
        });
        return watch >= 0 ? watch : throw Failure("");
    }

    public static void InotifyUnwatch(SafeFileHandle inotify, int watch) =>
        WithHandle(inotify, fd => InotifyRemoveWatch(fd, watch));

    /// <summary>Reads pending inotify events; 0 when none are pending.</summary>
    public static int ReadEvents(SafeFileHandle inotify, Span<byte> buffer)
    {
        fixed (byte* pointer = buffer)
        {
            var target = pointer;
            var length = buffer.Length;
            var read = WithHandle(inotify, fd => (long)Read(fd, target, (nuint)length));
            if (read >= 0)
            {
                return (int)read;
            }
            var error = Marshal.GetLastPInvokeError();
            return error is EAGAIN or EINTR ? 0 : throw new FsError(error, "");
        }
    }

    /// <summary>An eventfd that wakes a thread blocked in <see cref="WaitReadable"/>.</summary>
    public static SafeFileHandle EventCreate()
    {
        var fd = EventFd(0, CloseOnExec);
        return fd >= 0 ? new SafeFileHandle(fd, ownsHandle: true) : throw Failure("");
    }

    public static void EventSignal(SafeFileHandle eventHandle)
    {
        var one = stackalloc ulong[1];
        one[0] = 1;
        var pointer = (byte*)one;
        _ = WithHandle(eventHandle, fd => (long)Write(fd, pointer, sizeof(ulong)));
    }

    /// <summary>Blocks until <paramref name="first"/> or <paramref name="second"/> is readable; returns which (1 or 2).</summary>
    public static int WaitReadable(SafeFileHandle first, SafeFileHandle second)
    {
        var descriptors = stackalloc PollFd[2];
        descriptors[0] = new PollFd { Descriptor = WithHandle(first, fd => fd), Events = 1 };
        descriptors[1] = new PollFd { Descriptor = WithHandle(second, fd => fd), Events = 1 };
        while (true)
        {
            var ready = PollCall(descriptors, 2, -1);
            if (ready > 0)
            {
                return (descriptors[1].ReturnedEvents & 1) != 0 ? 2 : 1;
            }
            if (ready < 0 && Marshal.GetLastPInvokeError() != EINTR)
            {
                throw Failure("");
            }
        }
    }

    private static T WithHandle<T>(SafeFileHandle handle, Func<int, T> action)
    {
        var added = false;
        handle.DangerousAddRef(ref added);
        try
        {
            return action((int)handle.DangerousGetHandle());
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    private static byte[] Utf8(string value)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }

    private static FsError Failure(string name) => new(Marshal.GetLastPInvokeError(), name);

    private static FsKind KindOf(ushort mode) => (mode & 0xf000) switch
    {
        0x4000 => FsKind.Folder,
        0x8000 => FsKind.File,
        0xa000 => FsKind.Symlink,
        _ => FsKind.Other,
    };

    private static FsKind DirentKind(byte type) => type switch
    {
        4 => FsKind.Folder,
        8 => FsKind.File,
        10 => FsKind.Symlink,
        0 => FsKind.Unknown,
        _ => FsKind.Other,
    };
}

public enum FsKind
{
    File,
    Folder,
    Symlink,
    Other,
    Unknown,
}

/// <summary>An entry's metadata. <c>Id</c> is device and inode, stable across renames on the same file system.</summary>
public sealed record FsStat(FsKind Kind, long Size, DateTimeOffset Modified, DateTimeOffset Changed, string Id, uint Uid, uint Gid);

/// <summary>A failed Linux call with its errno.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Always carries an errno.")]
internal sealed class FsError(int errno, string name) : IOException($"errno {errno} for '{name}'")
{
    public int Errno { get; } = errno;
}
