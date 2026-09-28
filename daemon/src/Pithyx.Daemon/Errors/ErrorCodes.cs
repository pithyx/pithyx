using System.Collections.Frozen;
using System.Net;
using System.Reflection;

namespace Pithyx.Daemon.Errors;

/// <summary>
/// Every error code the API can return. Codes are part of the stable API contract: never rename or remove one
/// without a major version. Each code needs an English text in <c>i18n/en/errors.json</c>.
/// </summary>
public static class ErrorCodes
{
    /// <summary>The app lacks a permission the call needs, or an administrator revoked it; <c>params.permission</c> names it.</summary>
    [ErrorStatus(HttpStatusCode.Forbidden)]
    public const string AppPermissionDenied = "app.permission.denied";

    /// <summary>A value an app stores is larger than its limit; <c>params.maxKb</c> is the limit.</summary>
    [ErrorStatus(HttpStatusCode.RequestEntityTooLarge)]
    public const string AppStorageTooLarge = "app.storage.too_large";

    [ErrorStatus(HttpStatusCode.Unauthorized)]
    public const string AuthChallengeExpired = "auth.challenge.expired";

    [ErrorStatus(HttpStatusCode.Unauthorized)]
    public const string AuthCredentialsInvalid = "auth.credentials.invalid";

    [ErrorStatus(HttpStatusCode.Forbidden)]
    public const string AuthCsrfHeaderMissing = "auth.csrf.header_missing";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AuthDisplayNameInvalid = "auth.display_name.invalid";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AuthPasswordTooCommon = "auth.password.too_common";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AuthPasswordTooLong = "auth.password.too_long";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AuthPasswordTooShort = "auth.password.too_short";

    [ErrorStatus(HttpStatusCode.Forbidden)]
    public const string AuthPermissionDenied = "auth.permission.denied";

    [ErrorStatus(HttpStatusCode.TooManyRequests)]
    public const string AuthRateLimited = "auth.rate.limited";

    [ErrorStatus(HttpStatusCode.Unauthorized)]
    public const string AuthRecoveryInvalid = "auth.recovery.invalid";

    [ErrorStatus(HttpStatusCode.Unauthorized)]
    public const string AuthSessionExpired = "auth.session.expired";

    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string AuthSetupAlreadyDone = "auth.setup.already_done";

    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string AuthTotpAlreadyEnabled = "auth.totp.already_enabled";

    [ErrorStatus(HttpStatusCode.Unauthorized)]
    public const string AuthTotpInvalid = "auth.totp.invalid";

    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string AuthTotpNotEnrolled = "auth.totp.not_enrolled";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AuthUsernameInvalid = "auth.username.invalid";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AuthUsernameReserved = "auth.username.reserved";

    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string DesktopDeviceNotFound = "desktop.device.not_found";

    /// <summary>A change of a desktop item named an older version (If-Match); <c>params.version</c> is the current one.</summary>
    [ErrorStatus(HttpStatusCode.PreconditionFailed)]
    public const string DesktopItemConflict = "desktop.item.conflict";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string DesktopItemInvalid = "desktop.item.invalid";

    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string DesktopItemLimitReached = "desktop.item.limit_reached";

    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string DesktopItemNotFound = "desktop.item.not_found";

    /// <summary>A layout save named an older version (If-Match); <c>params.version</c> is the current one.</summary>
    [ErrorStatus(HttpStatusCode.PreconditionFailed)]
    public const string DesktopLayoutConflict = "desktop.layout.conflict";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string DesktopLayoutInvalid = "desktop.layout.invalid";

    [ErrorStatus(HttpStatusCode.RequestEntityTooLarge)]
    public const string DesktopLayoutTooLarge = "desktop.layout.too_large";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string DesktopNoteTooLong = "desktop.note.too_long";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string FsCursorInvalid = "fs.cursor.invalid";

    [ErrorStatus(HttpStatusCode.Forbidden)]
    public const string FsEntryAccessDenied = "fs.entry.access_denied";

    /// <summary><c>params.name</c> is the name that is taken.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string FsEntryExists = "fs.entry.exists";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string FsEntryIsFolder = "fs.entry.is_folder";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string FsEntryNotFolder = "fs.entry.not_folder";

    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string FsEntryNotFound = "fs.entry.not_found";

    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string FsFolderNotEmpty = "fs.folder.not_empty";

    /// <summary><c>params.errno</c> is the Linux error number.</summary>
    [ErrorStatus(HttpStatusCode.InternalServerError)]
    public const string FsIoFailed = "fs.io.failed";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string FsNameInvalid = "fs.name.invalid";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string FsOperationIntoItself = "fs.operation.into_itself";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string FsOperationInvalid = "fs.operation.invalid";

    /// <summary><c>params.maxBytes</c> is the longest allowed path.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string FsPathInvalid = "fs.path.invalid";

    [ErrorStatus(HttpStatusCode.RequestedRangeNotSatisfiable)]
    public const string FsRangeNotSatisfiable = "fs.range.not_satisfiable";

    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string FsShareExists = "fs.share.exists";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string FsShareInvalid = "fs.share.invalid";

    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string FsShareNotFound = "fs.share.not_found";

    [ErrorStatus(HttpStatusCode.Forbidden)]
    public const string FsShareReadOnly = "fs.share.read_only";

    /// <summary><c>params.max</c> is the longest allowed retention in days.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string FsTrashRetentionInvalid = "fs.trash.retention_invalid";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string FsUploadIncomplete = "fs.upload.incomplete";

    /// <summary>The share's dataset has no snapshot of this name, or the path is not in it.</summary>
    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string FsVersionNotFound = "fs.version.not_found";

    [ErrorStatus(HttpStatusCode.InsufficientStorage)]
    public const string FsVolumeFull = "fs.volume.full";

    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string FsVolumeReadOnly = "fs.volume.read_only";

    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string JobsJobNotFound = "jobs.job.not_found";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string RealtimeMessageInvalid = "realtime.message.invalid";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string RealtimeTopicInvalid = "realtime.topic.invalid";

    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string RealtimeTopicUnknown = "realtime.topic.unknown";

    [ErrorStatus(HttpStatusCode.UpgradeRequired)]
    public const string RealtimeUpgradeRequired = "realtime.upgrade.required";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string RequestBodyInvalid = "request.body.invalid";

    [ErrorStatus(HttpStatusCode.UnsupportedMediaType)]
    public const string RequestMediaTypeUnsupported = "request.media_type.unsupported";

    [ErrorStatus(HttpStatusCode.MethodNotAllowed)]
    public const string RequestMethodNotAllowed = "request.method.not_allowed";

    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string RequestRouteNotFound = "request.route.not_found";

    /// <summary>No current storage alert has this key (it went away); <c>params.key</c> is the key sent.</summary>
    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string StorageAlertNotFound = "storage.alert.not_found";

    /// <summary>A storage command of a plan failed; <c>params.command</c> is the program, <c>params.exitCode</c> its exit code.</summary>
    [ErrorStatus(HttpStatusCode.BadGateway)]
    public const string StorageCommandFailed = "storage.command.failed";

    /// <summary>Compression Pithyx does not set; <c>params.compression</c> is the value sent. Allowed: <c>off</c>, <c>lz4</c>, <c>zle</c>, <c>zstd</c>, <c>zstd-1</c> to <c>zstd-19</c>, <c>gzip</c>, <c>gzip-1</c> to <c>gzip-9</c>.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string StorageDatasetCompressionInvalid = "storage.dataset.compression_invalid";

    /// <summary>A dataset with this name exists already; <c>params.name</c> is the name.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string StorageDatasetExists = "storage.dataset.exists";

    /// <summary>A share has its folder in the dataset or one of its children, so it is not destroyed; <c>params.dataset</c> is the dataset, <c>params.share</c> the share's name.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string StorageDatasetInUseByShare = "storage.dataset.in_use_by_share";

    /// <summary>A quota or reservation below zero; <c>params.property</c> is <c>quota</c> or <c>reservation</c>.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string StorageDatasetLimitInvalid = "storage.dataset.limit_invalid";

    /// <summary>Pithyx finds this dataset by its name (a home, an app or system data), so it is not renamed or destroyed here; <c>params.dataset</c> is its name.</summary>
    [ErrorStatus(HttpStatusCode.Forbidden)]
    public const string StorageDatasetManaged = "storage.dataset.managed";

    /// <summary>Dataset names use letters, digits, <c>.</c>, <c>-</c> and <c>_</c> and start with a letter or digit, up to 64 characters.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string StorageDatasetNameInvalid = "storage.dataset.name_invalid";

    /// <summary>The typed name does not match the dataset's full name; <c>params.name</c> is the dataset's name.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string StorageDatasetNameMismatch = "storage.dataset.name_mismatch";

    /// <summary>No dataset of this name on the pool (any more); <c>params.dataset</c> is the name sent.</summary>
    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string StorageDatasetNotFound = "storage.dataset.not_found";

    /// <summary>The dataset does not take this setting: volumes, the pool root and the Pithyx layout datasets take only compression, and record size is for file systems; <c>params.dataset</c> and <c>params.property</c> say which.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string StorageDatasetPropertyUnsupported = "storage.dataset.property_unsupported";

    /// <summary>The pool root or a dataset of the Pithyx layout (<c>pithyx</c>, <c>shares</c>, <c>homes</c>, <c>apps</c>, <c>system</c>, <c>volumes</c>) is never renamed or destroyed; <c>params.dataset</c> is its name.</summary>
    [ErrorStatus(HttpStatusCode.Forbidden)]
    public const string StorageDatasetProtected = "storage.dataset.protected";

    /// <summary>A quota below the space the dataset uses now; <c>params.usedBytes</c> is that space.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string StorageDatasetQuotaBelowUsed = "storage.dataset.quota_below_used";

    /// <summary>Record sizes are powers of two from <c>params.minBytes</c> to <c>params.maxBytes</c>.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string StorageDatasetRecordSizeInvalid = "storage.dataset.record_size_invalid";

    /// <summary>A reservation above what the pool can give the dataset now; <c>params.maxBytes</c> is the most it can get.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string StorageDatasetReservationTooLarge = "storage.dataset.reservation_too_large";

    /// <summary>The request changes nothing: every setting sent has its current value already; <c>params.dataset</c> is the dataset.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string StorageDatasetUnchanged = "storage.dataset.unchanged";

    /// <summary>A disk named in the request does not exist (any more); <c>params.disk</c> is its id.</summary>
    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string StorageDiskNotFound = "storage.disk.not_found";

    /// <summary>The disk guard refused a disk; <c>params.disk</c> is its id and <c>params.reason</c> the guard's reason.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string StorageDiskRefused = "storage.disk.refused";

    /// <summary>Encryption is chosen when a pool is created, and becomes available with the key vault.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string StorageEncryptionUnavailable = "storage.encryption.unavailable";

    /// <summary>A device of the pool is on no disk the daemon knows, so the disk guard cannot check it; <c>params.device</c> names it.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string StorageImportDeviceUnknown = "storage.import.device_unknown";

    /// <summary>The pool was last used by another system: importing it needs <c>force</c> and its name typed; <c>params.name</c> is the name.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string StorageImportForceRequired = "storage.import.force_required";

    /// <summary>A new scan does not find the pool (any more); <c>params.pool</c> is the id sent.</summary>
    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string StorageImportNotFound = "storage.import.not_found";

    /// <summary>ZFS cannot import the pool, usually because too many devices are missing; <c>params.name</c> and <c>params.state</c> say which and how.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string StorageImportNotImportable = "storage.import.not_importable";

    /// <summary>A pool is imported read-write only after it was imported read-only and looked at; <c>params.name</c> is the pool.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string StorageImportPreviewRequired = "storage.import.preview_required";

    /// <summary>Unknown plan action; <c>params.action</c> is the value sent.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string StoragePlanActionInvalid = "storage.plan.action_invalid";

    /// <summary>A plan waited too long between being shown and being run; it must be shown again.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string StoragePlanExpired = "storage.plan.expired";

    /// <summary>The plan does not exist (any more), was run already or belongs to another user; it must be shown again.</summary>
    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string StoragePlanNotFound = "storage.plan.not_found";

    /// <summary>A disk changed after the plan was shown (serial, size, partitions or signatures); <c>params.disk</c> is its id.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string StoragePlanStale = "storage.plan.stale";

    /// <summary>The layout needs more or fewer disks; <c>params.layout</c>, <c>params.min</c> and <c>params.count</c> say how many.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string StoragePoolDiskCount = "storage.pool.disk_count";

    /// <summary>A new pool needs the encryption question answered; <c>encryption</c> was missing.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string StoragePoolEncryptionRequired = "storage.pool.encryption_required";

    /// <summary>A RAID-Z expansion of the pool is running; the next one can start when it is done. <c>params.name</c> is the pool.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string StoragePoolExpanding = "storage.pool.expanding";

    /// <summary>Unknown pool layout; <c>params.layout</c> is the value sent.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string StoragePoolLayoutInvalid = "storage.pool.layout_invalid";

    /// <summary>Pool names start with a lowercase letter, then up to 31 lowercase letters, digits, <c>-</c> or <c>_</c>, and are not a ZFS keyword.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string StoragePoolNameInvalid = "storage.pool.name_invalid";

    /// <summary>The typed name does not match the pool's name; <c>params.name</c> is the pool's name.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string StoragePoolNameMismatch = "storage.pool.name_mismatch";

    /// <summary>A pool with this name exists already; <c>params.name</c> is the name.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string StoragePoolNameTaken = "storage.pool.name_taken";

    /// <summary>No imported pool has this name; <c>params.name</c> is the name.</summary>
    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string StoragePoolNotFound = "storage.pool.not_found";

    /// <summary>The pool has no <c>&lt;pool&gt;/pithyx</c> dataset (Pithyx did not create it, for example an imported pool), so Pithyx creates nothing on it; <c>params.pool</c> is its name.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string StoragePoolNotPrepared = "storage.pool.not_prepared";

    /// <summary>The pool is imported read-only and cannot be changed; import it read-write first. <c>params.name</c> is the pool.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string StoragePoolReadOnly = "storage.pool.read_only";

    /// <summary>Unknown schedule kind or frequency, or a scrub schedule for a pool that does not exist; <c>params.kind</c> and <c>params.frequency</c> are the values sent.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string StorageScheduleInvalid = "storage.schedule.invalid";

    /// <summary>The pool has no running scrub to pause or stop; <c>params.pool</c> is its name.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string StorageScrubNotRunning = "storage.scrub.not_running";

    /// <summary>The pool is being scrubbed already; <c>params.pool</c> is its name.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string StorageScrubRunning = "storage.scrub.running";

    /// <summary>The dataset name typed to confirm a rollback does not match the dataset.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string StorageSnapshotConfirmationMismatch = "storage.snapshot.confirmation_mismatch";

    /// <summary>No such dataset (any more); <c>params.dataset</c> is the name sent.</summary>
    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string StorageSnapshotDatasetNotFound = "storage.snapshot.dataset_not_found";

    /// <summary>A snapshot of this name exists already (two in the same second); <c>params.name</c> is its name.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string StorageSnapshotExists = "storage.snapshot.exists";

    /// <summary>The snapshot has a hold and cannot be destroyed; <c>params.name</c> is its name.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string StorageSnapshotHeld = "storage.snapshot.held";

    /// <summary>A rollback would destroy newer snapshots that the request did not list; <c>params.count</c> is how many there are.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string StorageSnapshotNewerExist = "storage.snapshot.newer_exist";

    /// <summary>No snapshot of this name on the dataset; <c>params.name</c> is the name sent.</summary>
    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string StorageSnapshotNotFound = "storage.snapshot.not_found";

    /// <summary>Pithyx deletes only snapshots it took (D104); <c>params.name</c> is the snapshot's name.</summary>
    [ErrorStatus(HttpStatusCode.Forbidden)]
    public const string StorageSnapshotNotPithyx = "storage.snapshot.not_pithyx";

    /// <summary>Unknown or repeated schedule kind, or a count outside 1 to <c>params.max</c>.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string StorageSnapshotScheduleInvalid = "storage.snapshot.schedule_invalid";

    /// <summary>The pool has no device with this GUID; <c>params.vdev</c> is the GUID.</summary>
    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string StorageVdevNotFound = "storage.vdev.not_found";

    /// <summary>A volume size outside <c>params.minBytes</c> to <c>params.maxBytes</c>.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string StorageVolumeSizeInvalid = "storage.volume.size_invalid";

    /// <summary>A volume that is not sparse must fit the free space; <c>params.availableBytes</c> is what the pool has.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string StorageVolumeTooLarge = "storage.volume.too_large";

    /// <summary>Only a raidz vdev can be expanded by a disk; <c>params.vdev</c> is the vdev's name.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string StorageVdevNotRaidz = "storage.vdev.not_raidz";

    [ErrorStatus(HttpStatusCode.InternalServerError)]
    public const string SystemInternalUnexpected = "system.internal.unexpected";

    [ErrorStatus(HttpStatusCode.Forbidden)]
    public const string SystemOriginForbidden = "system.origin.forbidden";

    /// <summary>All codes with their HTTP status, ordered by code.</summary>
    public static FrozenDictionary<string, HttpStatusCode> All { get; } = typeof(ErrorCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral)
        .ToFrozenDictionary(
            field => (string)field.GetRawConstantValue()!,
            field => field.GetCustomAttribute<ErrorStatusAttribute>()?.Status
                ?? throw new InvalidOperationException($"{field.Name} has no [ErrorStatus]."));

    public static HttpStatusCode StatusOf(string code) =>
        All.TryGetValue(code, out var status) ? status : throw new ArgumentException($"Unknown error code {code}.", nameof(code));
}
