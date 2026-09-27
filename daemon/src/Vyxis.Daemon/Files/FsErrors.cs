using Vyxis.Daemon.Errors;

namespace Vyxis.Daemon.Files;

/// <summary>Maps errno values of failed file calls to stable error codes.</summary>
internal static class FsErrors
{
    public static VyxisException From(FsError error, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Errno switch
        {
            // EXDEV and ELOOP come from openat2 refusing a path that leaves the share: to the client it does not exist.
            LinuxFs.ENOENT or LinuxFs.EXDEV or LinuxFs.ELOOP => new VyxisException(ErrorCodes.FsEntryNotFound, null, error),
            LinuxFs.ENOTDIR => new VyxisException(ErrorCodes.FsEntryNotFolder, null, error),
            LinuxFs.EISDIR => new VyxisException(ErrorCodes.FsEntryIsFolder, null, error),
            LinuxFs.EEXIST => new VyxisException(ErrorCodes.FsEntryExists, new Dictionary<string, object?> { ["name"] = name ?? "" }, error),
            LinuxFs.ENOTEMPTY => new VyxisException(ErrorCodes.FsFolderNotEmpty, null, error),
            LinuxFs.EACCES or LinuxFs.EPERM => new VyxisException(ErrorCodes.FsEntryAccessDenied, null, error),
            LinuxFs.ENOSPC => new VyxisException(ErrorCodes.FsVolumeFull, null, error),
            LinuxFs.EROFS => new VyxisException(ErrorCodes.FsVolumeReadOnly, null, error),
            LinuxFs.ENAMETOOLONG => new VyxisException(ErrorCodes.FsNameInvalid, null, error),
            _ => new VyxisException(ErrorCodes.FsIoFailed, new Dictionary<string, object?> { ["errno"] = error.Errno }, error),
        };
    }
}
