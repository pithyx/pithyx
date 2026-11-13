using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Files;

/// <summary>Maps errno values of failed file calls to stable error codes.</summary>
internal static class FsErrors
{
    public static PithyxException From(FsError error, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Errno switch
        {
            // EXDEV and ELOOP come from openat2 refusing a path that leaves the share: to the client it does not exist.
            LinuxFs.ENOENT or LinuxFs.EXDEV or LinuxFs.ELOOP => new PithyxException(ErrorCodes.FsEntryNotFound, null, error),
            LinuxFs.ENOTDIR => new PithyxException(ErrorCodes.FsEntryNotFolder, null, error),
            LinuxFs.EISDIR => new PithyxException(ErrorCodes.FsEntryIsFolder, null, error),
            LinuxFs.EEXIST => new PithyxException(ErrorCodes.FsEntryExists, new Dictionary<string, object?> { ["name"] = name ?? "" }, error),
            LinuxFs.ENOTEMPTY => new PithyxException(ErrorCodes.FsFolderNotEmpty, null, error),
            LinuxFs.EACCES or LinuxFs.EPERM => new PithyxException(ErrorCodes.FsEntryAccessDenied, null, error),
            LinuxFs.ENOSPC => new PithyxException(ErrorCodes.FsVolumeFull, null, error),
            LinuxFs.EDQUOT => new PithyxException(ErrorCodes.FsQuotaExceeded, null, error),
            LinuxFs.EROFS => new PithyxException(ErrorCodes.FsVolumeReadOnly, null, error),
            LinuxFs.ENAMETOOLONG => new PithyxException(ErrorCodes.FsNameInvalid, null, error),
            _ => new PithyxException(ErrorCodes.FsIoFailed, new Dictionary<string, object?> { ["errno"] = error.Errno }, error),
        };
    }

    /// <summary>
    /// A write through a <see cref="FileStream"/> that failed for lack of space: .NET throws a plain
    /// <see cref="IOException"/> with the errno as its HResult. Null for any other failure.
    /// </summary>
    public static PithyxException? NoSpace(IOException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.HResult switch
        {
            LinuxFs.ENOSPC => new PithyxException(ErrorCodes.FsVolumeFull, null, exception),
            LinuxFs.EDQUOT => new PithyxException(ErrorCodes.FsQuotaExceeded, null, exception),
            _ => null,
        };
    }
}
