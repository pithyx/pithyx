using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pithyx.Daemon.Files.Search;

/// <summary>
/// One event of a fanotify group that reports the directory of a changed entry: an entry was created, deleted or moved
/// in the directory behind <paramref name="Directory"/> (a <c>struct file_handle</c>) on the file system
/// <paramref name="Fsid"/>, or the kernel's queue overflowed and events were lost.
/// </summary>
public sealed record FanotifyEvent(ulong Fsid, byte[] Directory, bool Overflow);

/// <summary>
/// fanotify with filesystem marks (D413): every entry created, deleted or moved anywhere on a marked file system,
/// whoever made the change (Samba, SFTP, a container, a script), with no watch per directory. Each ZFS dataset is a
/// file system of its own, so every indexed dataset gets one mark. Needs root (CAP_SYS_ADMIN) and Linux 5.9 or later;
/// without them the index falls back to walks.
/// </summary>
internal static unsafe partial class Fanotify
{
    public const ulong Create = 0x100;
    public const ulong Delete = 0x200;
    public const ulong MovedFrom = 0x40;
    public const ulong MovedTo = 0x80;
    public const ulong OnDirectory = 0x40000000;
    public const ulong QueueOverflow = 0x4000;
    public const ulong Mask = Create | Delete | MovedFrom | MovedTo | OnDirectory;

    private const uint CloseOnExec = 0x1;
    private const uint NonBlock = 0x2;
    private const uint ReportDirectoryFidName = 0xC00;
    private const uint MarkAdd = 0x1;
    private const uint MarkRemove = 0x2;
    private const uint MarkFilesystem = 0x100;
    private const int AtFdCwd = -100;
    private const int OpenPath = 0x200000;
    private const int OpenCloseOnExec = 0x80000;
    private const int MetadataLength = 24;
    private const byte InfoDirectoryFidName = 2;
    private const byte InfoDirectoryFid = 3;
    private const int FsidOffset = 56;

    [LibraryImport("libc", EntryPoint = "fanotify_init", SetLastError = true)]
    private static partial int InitCall(uint flags, uint eventFlags);

    [LibraryImport("libc", EntryPoint = "fanotify_mark", SetLastError = true)]
    private static partial int MarkCall(int fanotify, uint flags, ulong mask, int directory, byte* path);

    [LibraryImport("libc", EntryPoint = "open_by_handle_at", SetLastError = true)]
    private static partial int OpenByHandle(int mount, byte* handle, int flags);

    [LibraryImport("libc", EntryPoint = "readlink", SetLastError = true)]
    private static partial nint ReadLink(byte* path, byte* buffer, nuint size);

    [LibraryImport("libc", EntryPoint = "statfs", SetLastError = true)]
    private static partial int StatFs(byte* path, byte* buffer);

    /// <summary>A fanotify group for directory events, or null with the errno when this system cannot make one.</summary>
    public static SafeFileHandle? TryInit(out int errno)
    {
        var fd = InitCall(ReportDirectoryFidName | CloseOnExec | NonBlock, 0);
        errno = fd < 0 ? Marshal.GetLastPInvokeError() : 0;
        return fd >= 0 ? new SafeFileHandle(fd, ownsHandle: true) : null;
    }

    /// <summary>Marks the whole file system that holds <paramref name="path"/>; returns the errno, 0 on success.</summary>
    public static int Mark(SafeFileHandle fanotify, string path, bool add)
    {
        ArgumentNullException.ThrowIfNull(fanotify);
        var bytes = Utf8(path);
        var added = false;
        fanotify.DangerousAddRef(ref added);
        try
        {
            fixed (byte* pointer = bytes)
            {
                var result = MarkCall((int)fanotify.DangerousGetHandle(), (add ? MarkAdd : MarkRemove) | MarkFilesystem, Mask, AtFdCwd, pointer);
                return result == 0 ? 0 : Marshal.GetLastPInvokeError();
            }
        }
        finally
        {
            if (added)
            {
                fanotify.DangerousRelease();
            }
        }
    }

    /// <summary>The id fanotify reports for the file system that holds <paramref name="path"/>, or null.</summary>
    public static ulong? FsidOf(string path)
    {
        var bytes = Utf8(path);
        var buffer = stackalloc byte[256];
        fixed (byte* pointer = bytes)
        {
            return StatFs(pointer, buffer) == 0 ? BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(buffer + FsidOffset, 8)) : null;
        }
    }

    /// <summary>
    /// The path of the directory behind a file handle, resolved through <paramref name="mount"/>, a path on the same
    /// file system; null when the directory is gone. Needs CAP_DAC_READ_SEARCH.
    /// </summary>
    public static string? PathOf(string mount, byte[] handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        // The kernel takes the mount from a real open file: an O_PATH handle answers EBADF.
        using var path = LinuxFs.OpenRoot(mount);
        using var root = LinuxFs.OpenBeneath(path, ".", LinuxFs.ReadOnly | LinuxFs.Directory);
        int fd;
        var added = false;
        root.DangerousAddRef(ref added);
        try
        {
            fixed (byte* pointer = handle)
            {
                fd = OpenByHandle((int)root.DangerousGetHandle(), pointer, OpenPath | OpenCloseOnExec);
            }
        }
        finally
        {
            if (added)
            {
                root.DangerousRelease();
            }
        }
        if (fd < 0)
        {
            return null;
        }
        using var directory = new SafeFileHandle(fd, ownsHandle: true);
        var link = Utf8($"/proc/self/fd/{fd}");
        var target = new byte[4096];
        nint length;
        fixed (byte* linkPointer = link)
        fixed (byte* targetPointer = target)
        {
            length = ReadLink(linkPointer, targetPointer, (nuint)target.Length);
        }
        if (length <= 0)
        {
            return null;
        }
        var resolved = Encoding.UTF8.GetString(target, 0, (int)length);
        return resolved.EndsWith(" (deleted)", StringComparison.Ordinal) ? null : resolved;
    }

    /// <summary>
    /// The events in what one read of the group returned: <c>struct fanotify_event_metadata</c> records, each followed
    /// by info records. Only the directory of each event is kept; the name does not matter, the index reads the
    /// directory again. Pure.
    /// </summary>
    public static List<FanotifyEvent> Parse(ReadOnlySpan<byte> buffer)
    {
        var events = new List<FanotifyEvent>();
        while (buffer.Length >= MetadataLength)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer);
            if (length < MetadataLength || length > buffer.Length)
            {
                break;
            }
            var metadataLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer[6..]);
            var mask = BinaryPrimitives.ReadUInt64LittleEndian(buffer[8..]);
            var record = buffer[..length];
            buffer = buffer[length..];
            if ((mask & QueueOverflow) != 0)
            {
                events.Add(new FanotifyEvent(0, [], Overflow: true));
                continue;
            }
            if (metadataLength < MetadataLength || metadataLength > record.Length)
            {
                continue;
            }
            var info = record[metadataLength..];
            while (info.Length >= 4)
            {
                var type = info[0];
                var infoLength = BinaryPrimitives.ReadUInt16LittleEndian(info[2..]);
                if (infoLength < 4 || infoLength > info.Length)
                {
                    break;
                }
                // fanotify_event_info_fid: header (4), fsid (8), then struct file_handle: handle_bytes, handle_type, f_handle.
                if (type is InfoDirectoryFidName or InfoDirectoryFid && infoLength >= 20)
                {
                    var handleBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[12..]);
                    if (20 + handleBytes <= infoLength)
                    {
                        events.Add(new FanotifyEvent(BinaryPrimitives.ReadUInt64LittleEndian(info[4..]), info.Slice(12, 8 + handleBytes).ToArray(), Overflow: false));
                    }
                }
                info = info[infoLength..];
            }
        }
        return events;
    }

    private static byte[] Utf8(string value)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }
}
