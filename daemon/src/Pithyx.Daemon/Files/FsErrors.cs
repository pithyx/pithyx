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
            LinuxFs.EROFS => new PithyxException(ErrorCodes.FsVolumeReadOnly, null, error),
            LinuxFs.ENAMETOOLONG => new PithyxException(ErrorCodes.FsNameInvalid, null, error),
            _ => new PithyxException(ErrorCodes.FsIoFailed, new Dictionary<string, object?> { ["errno"] = error.Errno }, error),
        };
    }
}
