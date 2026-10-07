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
    /// <summary>An install, update or removal of the app is running already; <c>params.id</c> names the app.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string AppAppBusy = "app.app.busy";

    /// <summary>No app with this id is installed; <c>params.id</c> is the id asked for.</summary>
    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string AppAppNotFound = "app.app.not_found";

    /// <summary>
    /// The package declares a backend (containers). This version of Pithyx installs apps with a frontend only; backends
    /// arrive with the Docker runtime.
    /// </summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppBackendUnsupported = "app.backend.unsupported";

    /// <summary>
    /// A key of the app's Compose file is not in the allowlist (<c>sdk/schema/compose.v1.rules.json</c>), either unknown or
    /// one the box sets itself (<c>ports</c>, <c>networks</c>, <c>user</c>, <c>restart</c>, <c>logging</c>, limits);
    /// <c>params.path</c> is its JSON Pointer, <c>params.key</c> the key.
    /// </summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppComposeKeyUnsupported = "app.compose.key_unsupported";

    /// <summary>
    /// A name refers to something that is not declared: a volume missing from the Compose file's top-level
    /// <c>volumes</c>, a service in <c>depends_on</c>, or a service of the manifest's <c>backend.endpoints</c> or
    /// <c>storage[].services</c> missing from the Compose file; <c>params.path</c> is the reference (in the Compose file for
    /// <c>/services/...</c>, in the manifest for <c>/backend/...</c> and <c>/storage/...</c>), <c>params.name</c> the name.
    /// </summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppComposeReferenceUnknown = "app.compose.reference_unknown";

    /// <summary>
    /// The Compose file uses a key that needs an elevated right the manifest's <c>backend.elevated</c> does not declare
    /// (<c>devices</c>, <c>cap_add</c>, <c>network_mode: host</c>, <c>pid: host</c>); <c>params.path</c> is the use,
    /// <c>params.right</c> the right it needs.
    /// </summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppComposeRightMissing = "app.compose.right_missing";

    /// <summary>
    /// The manifest declares an elevated right the Compose file never uses; <c>params.path</c> is the right in the
    /// manifest, <c>params.right</c> the right. Rights are checked in both directions, so an app holds no right it does
    /// not need.
    /// </summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppComposeRightUnused = "app.compose.right_unused";

    /// <summary>
    /// A storage request of the manifest and the Compose file disagree; <c>params.storage</c> is the storage id,
    /// <c>params.path</c> the place and <c>params.reason</c> one of <c>not_mounted</c> (a service listed in
    /// <c>storage[].services</c> does not mount the volume), <c>not_listed</c> (a service mounts it without being listed) or
    /// <c>read_only</c> (storage with <c>access: read</c> is mounted without <c>:ro</c>).
    /// </summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppComposeStorageMismatch = "app.compose.storage_mismatch";

    /// <summary>
    /// A value of the app's Compose file is refused; <c>params.path</c> is its JSON Pointer and <c>params.reason</c> one of
    /// <c>required</c>, <c>type</c>, <c>format</c>, <c>range</c>, <c>unpinned</c> (an image without <c>@sha256:</c>),
    /// <c>interpolation</c> (a <c>$</c> that is not <c>$$</c>), <c>no_value</c> (an environment variable without a value,
    /// which Compose would take from the box), <c>duplicate</c>, <c>reserved</c> (a label under <c>com.pithyx</c> or
    /// <c>com.docker</c>, or a mount on <c>/</c>, <c>/proc</c>, <c>/sys</c> or <c>/dev</c>), <c>host_path</c> (a bind
    /// mount of a host path), <c>anonymous</c> (a volume without a name) or <c>cycle</c> (in <c>depends_on</c>).
    /// </summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppComposeValueInvalid = "app.compose.value_invalid";

    /// <summary>
    /// The app's Compose file is not YAML the box reads; <c>params.reason</c> says why: <c>too_large</c> (over 64 KB),
    /// <c>not_utf8</c>, <c>character</c> (a tab, a byte order mark or another character YAML parsers disagree about),
    /// <c>syntax</c>, <c>documents</c> (not exactly one document), <c>too_deep</c>, <c>directive</c>, <c>anchor</c>
    /// (anchors and aliases), <c>tag</c>, <c>duplicate_key</c>, <c>merge_key</c> or <c>key_not_string</c>, the last three
    /// with <c>params.path</c>.
    /// </summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppComposeYamlInvalid = "app.compose.yaml_invalid";

    /// <summary>
    /// The manifest asks for an elevated right this version of Pithyx does not grant: <c>docker.socket</c>, <c>host.full</c>
    /// or a capability that reaches the host's secrets; <c>params.right</c> names it. When the Compose file needs it
    /// (<c>privileged</c>, a mount of the Docker socket, <c>cap_add</c>), <c>params.path</c> is the use.
    /// </summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppElevatedUnsupported = "app.elevated.unsupported";

    /// <summary>
    /// The app's <c>icon.svg</c> is refused; <c>params.reason</c> says why: <c>too_large</c> (over 64 KB), <c>malformed</c>
    /// (not UTF-8 or not well-formed XML), <c>not_svg</c>, <c>viewbox</c> (missing or not square), <c>doctype</c>,
    /// <c>processing_instruction</c>, <c>element_forbidden</c> (with <c>params.element</c>), <c>event_attribute</c> (with
    /// <c>params.attribute</c>), <c>external_href</c>, <c>style_import</c>, <c>style_url</c> or <c>style_escape</c>.
    /// </summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppIconInvalid = "app.icon.invalid";

    /// <summary>A schedule's <c>cron</c> is not five valid fields; <c>params.path</c> and <c>params.cron</c> name it.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppManifestCronInvalid = "app.manifest.cron_invalid";

    /// <summary>
    /// A value that must be unique in the manifest appears twice (storage ids, schedule ids, file extensions);
    /// <c>params.path</c> is the second one, <c>params.value</c> the value.
    /// </summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppManifestDuplicate = "app.manifest.duplicate";

    /// <summary>The manifest's <c>id</c> is not an app id of D302; <c>params.id</c> is the value.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppManifestIdInvalid = "app.manifest.id_invalid";

    /// <summary>The id lies in <c>org.pithyx.*</c>, which only system apps and the official catalog may use; <c>params.id</c> is the value.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppManifestIdReserved = "app.manifest.id_reserved";

    /// <summary><c>pithyx.json</c> is not a JSON document in UTF-8, or an object in it has a key twice.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppManifestInvalidJson = "app.manifest.invalid_json";

    /// <summary>
    /// A name in the manifest refers to something it does not declare: an endpoint that is not in
    /// <c>backend.endpoints</c>, or a backend service of a storage request without a <c>backend</c>; <c>params.path</c> is
    /// the reference, <c>params.name</c> the name.
    /// </summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppManifestReferenceUnknown = "app.manifest.reference_unknown";

    /// <summary>
    /// The manifest breaks its JSON Schema (<c>sdk/schema/manifest.v1.schema.json</c>); <c>params.path</c> is the JSON
    /// Pointer of the value (of the missing or unknown field for <c>required</c> and <c>additionalProperties</c>),
    /// <c>params.keyword</c> the schema keyword it fails.
    /// </summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppManifestSchemaViolation = "app.manifest.schema_violation";

    /// <summary>The manifest's <c>sdk</c> is not a SemVer range; <c>params.sdk</c> is the value.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppManifestSdkInvalid = "app.manifest.sdk_invalid";

    /// <summary>The manifest's <c>manifestVersion</c> is one this version of Pithyx does not know; <c>params.version</c> and <c>params.supported</c>.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppManifestVersionUnsupported = "app.manifest.version_unsupported";

    /// <summary>The window's <c>minSize</c> is larger than its <c>defaultSize</c> in width or height.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppManifestWindowSizeInvalid = "app.manifest.window_size_invalid";

    /// <summary>
    /// A folder cannot be mapped to a storage request of the app; <c>params.storage</c> names the request and
    /// <c>params.reason</c> says why: <c>unknown</c> (the app's manifest has no such request) or <c>access</c> (read and
    /// write for a request that asks for reading only).
    /// </summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppMappingInvalid = "app.mapping.invalid";

    /// <summary>
    /// The uploaded file is not a readable app package (a gzip-compressed tar archive); nothing of it was kept.
    /// <c>params.reason</c> says why: <c>gzip</c> (not gzip or damaged), <c>format</c> (not a ustar or pax archive, or a
    /// malformed header field), <c>checksum</c> (a header's checksum is wrong), <c>truncated</c> (the archive ends inside an
    /// entry or without its end marker), <c>trailing_data</c> (data after the end marker), <c>entry_type</c> (a link, device,
    /// FIFO or other entry that is not a regular file or folder; <c>params.path</c> names it), <c>extended_header</c> (a pax
    /// or GNU header that is malformed, global, or sets something other than the name and size) or <c>name_mismatch</c>
    /// (a long name that does not continue the entry's own name; <c>params.path</c>).
    /// </summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppPackageInvalid = "app.package.invalid";

    /// <summary>
    /// The files of an app package are not laid out as required; <c>params.path</c> names the file and
    /// <c>params.reason</c> says why: <c>missing</c> (<c>pithyx.json</c>, <c>icon.svg</c>, the entry or the Compose file),
    /// <c>unsafe</c> (an absolute path, empty segments, a segment starting with a dot, a character other than ASCII
    /// letters, digits and <c>._~@+-</c>, a segment over 255 or a path over 512 bytes, or more than 32 levels),
    /// <c>duplicate</c> (also when two paths differ only in case, or a file is in the way of a folder), <c>unexpected</c>
    /// (a folder other than <c>frontend/</c> and <c>backend/</c>, or files under <c>backend/</c> without a backend) or
    /// <c>reserved</c> (<c>frontend/api</c> and <c>frontend/api-*</c>, whose URLs belong to the backend).
    /// </summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppPackageLayoutInvalid = "app.package.layout_invalid";

    /// <summary>
    /// The checked package is gone: it expired 15 minutes after the upload, it was installed or discarded, another
    /// administrator uploaded it, or the daemon restarted. Upload the file again.
    /// </summary>
    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string AppPackageNotFound = "app.package.not_found";

    /// <summary>
    /// The package is larger than an app package may be; <c>params.reason</c> says which limit and <c>params.limit</c> what
    /// it is: <c>size</c> (the file, in MB), <c>unpacked</c> (all files together, in MB), <c>files</c> (the number of entries)
    /// or <c>manifest</c> (<c>pithyx.json</c>, in KB).
    /// </summary>
    [ErrorStatus(HttpStatusCode.RequestEntityTooLarge)]
    public const string AppPackageTooLarge = "app.package.too_large";

    /// <summary>The app lacks a permission the call needs, or an administrator revoked it; <c>params.permission</c> names it.</summary>
    [ErrorStatus(HttpStatusCode.Forbidden)]
    public const string AppPermissionDenied = "app.permission.denied";

    /// <summary>The app's manifest does not ask for the permission an administrator tried to grant or revoke; <c>params.permission</c> names it.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppPermissionNotRequested = "app.permission.not_requested";

    /// <summary>
    /// Apps live on one pool, chosen at the first install; while an app is installed it cannot move. <c>params.pool</c>
    /// is the app pool.
    /// </summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string AppPoolFixed = "app.pool.fixed";

    /// <summary>The first app needs a pool for the app data; none was chosen.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string AppPoolRequired = "app.pool.required";

    /// <summary>
    /// The app pool cannot take apps now; <c>params.pool</c> names it and <c>params.reason</c> says why: <c>missing</c> (not
    /// imported), <c>offline</c> (not online), <c>read_only</c>, <c>locked</c> (encrypted and locked) or <c>not_prepared</c>
    /// (a pool without the Pithyx layout).
    /// </summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string AppPoolUnavailable = "app.pool.unavailable";

    /// <summary>A value an app stores is larger than its limit; <c>params.maxKb</c> is the limit.</summary>
    [ErrorStatus(HttpStatusCode.RequestEntityTooLarge)]
    public const string AppStorageTooLarge = "app.storage.too_large";

    /// <summary>An administrator deactivated the account; the password was right.</summary>
    [ErrorStatus(HttpStatusCode.Forbidden)]
    public const string AuthAccountDisabled = "auth.account.disabled";

    [ErrorStatus(HttpStatusCode.Unauthorized)]
    public const string AuthChallengeExpired = "auth.challenge.expired";

    [ErrorStatus(HttpStatusCode.Unauthorized)]
    public const string AuthCredentialsInvalid = "auth.credentials.invalid";

    [ErrorStatus(HttpStatusCode.Forbidden)]
    public const string AuthCsrfHeaderMissing = "auth.csrf.header_missing";

    /// <summary>The user may not sign in to the desktop: it lacks the desktop login right. Shown only after the right password.</summary>
    [ErrorStatus(HttpStatusCode.Forbidden)]
    public const string AuthDesktopNotAllowed = "auth.desktop.not_allowed";

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

    /// <summary>A file service cannot be switched on because its program is not installed; <c>params.service</c> is <c>smb</c> or <c>sftp</c>.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string FileServicesServiceUnavailable = "fileservices.service.unavailable";

    /// <summary>The SMB workgroup is empty, too long or has other characters than letters, digits, dots, dashes and underscores; <c>params.max</c> is the longest allowed.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string FileServicesWorkgroupInvalid = "fileservices.workgroup.invalid";

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

    /// <summary>Deleting a shared folder needs an explicit choice of what happens with its data: <c>keep</c> or <c>destroy</c>.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string FsShareDataChoiceRequired = "fs.share.data_choice_required";

    /// <summary>No dataset name can be made from the shared folder's name (it has no letter or digit from a to z or 0 to 9); <c>params.name</c> is the name.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string FsShareDatasetNameInvalid = "fs.share.dataset_name_invalid";

    /// <summary>A description is longer than <c>params.max</c> characters or holds control characters.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string FsShareDescriptionInvalid = "fs.share.description_invalid";

    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string FsShareExists = "fs.share.exists";

    /// <summary>Registering an existing folder as a share works only on a development machine; elsewhere shared folders are datasets.</summary>
    [ErrorStatus(HttpStatusCode.Forbidden)]
    public const string FsShareFolderDevOnly = "fs.share.folder_dev_only";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string FsShareInvalid = "fs.share.invalid";

    /// <summary>The typed name does not match the shared folder's name; <c>params.name</c> is its name.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string FsShareNameMismatch = "fs.share.name_mismatch";

    /// <summary>The name would take a place Pithyx uses itself (the dataset <c>homes</c>, or a section name of Samba such as <c>global</c> or <c>printers</c>); <c>params.name</c> is the dataset name.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string FsShareNameReserved = "fs.share.name_reserved";

    /// <summary>The shared folder is a plain folder, not a dataset, so its files can only be kept.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string FsShareNoDataset = "fs.share.no_dataset";

    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string FsShareNotFound = "fs.share.not_found";

    /// <summary>
    /// The share exists and the user may use it, but its dataset cannot be reached now; <c>params.name</c> is the share
    /// and <c>params.reason</c> one of <c>pool_missing</c>, <c>pool_unavailable</c> or <c>not_mounted</c>
    /// (<see cref="Shares.ShareAvailability"/>). It comes back once its pool does.
    /// </summary>
    [ErrorStatus(HttpStatusCode.ServiceUnavailable)]
    public const string FsShareUnavailable = "fs.share.unavailable";

    /// <summary>Another dataset already mounts where the new shared folder would; <c>params.path</c> is the place.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string FsSharePathTaken = "fs.share.path_taken";

    /// <summary>The pool has no <c>&lt;pool&gt;/pithyx/shares</c> dataset, so no shared folder is created on it; <c>params.pool</c> is its name.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string FsSharePoolNotPrepared = "fs.share.pool_not_prepared";

    [ErrorStatus(HttpStatusCode.Forbidden)]
    public const string FsShareReadOnly = "fs.share.read_only";

    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string FsThumbnailNotFound = "fs.thumbnail.not_found";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string FsThumbnailTooMany = "fs.thumbnail.too_many";

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

    /// <summary>The box misses a requirement of the installer; <c>params.check</c> names it (see <c>InstallerCheck</c>).</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string InstallerCheckFailed = "installer.check.failed";

    /// <summary>The call needs the browser that owns the installation.</summary>
    [ErrorStatus(HttpStatusCode.Forbidden)]
    public const string InstallerClaimRequired = "installer.claim.required";

    /// <summary>Another browser owns the installation.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string InstallerClaimTaken = "installer.claim.taken";

    /// <summary>The system goes onto one disk or a mirror of two different disks.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string InstallerDiskCount = "installer.disk.count";

    /// <summary>A chosen disk cannot hold the system; <c>params.diskId</c> and <c>params.reason</c> say which and why.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string InstallerDiskUnsuitable = "installer.disk.unsuitable";

    /// <summary>The user did not confirm that the chosen disks are erased.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string InstallerEraseNotConfirmed = "installer.erase.not_confirmed";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string InstallerHostnameInvalid = "installer.hostname.invalid";

    /// <summary>A step of the installation failed; <c>params.step</c> names it. The log of the live system has the command.</summary>
    [ErrorStatus(HttpStatusCode.InternalServerError)]
    public const string InstallerInstallFailed = "installer.install.failed";

    /// <summary>The installation runs already or is done.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string InstallerInstallRunning = "installer.install.running";

    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string InstallerKeyboardInvalid = "installer.keyboard.invalid";

    /// <summary>The box restarts only once the installation is done.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string InstallerRebootNotReady = "installer.reboot.not_ready";

    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string JobsJobNotFound = "jobs.job.not_found";

    /// <summary>The app id of a log request is not a valid app id.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string LogsAppInvalid = "logs.app.invalid";

    /// <summary>The archive's pool cannot hold it (missing, locked, read-only or without the Pithyx layout); <c>params.pool</c> names it.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string LogsArchivePoolUnavailable = "logs.archive.pool_unavailable";

    /// <summary>An app sent more log entries in one request than allowed; <c>params.max</c> is the limit.</summary>
    [ErrorStatus(HttpStatusCode.RequestEntityTooLarge)]
    public const string LogsBatchTooLarge = "logs.batch.too_large";

    /// <summary>A log query has an invalid cursor, time range or limit.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string LogsQueryInvalid = "logs.query.invalid";

    /// <summary>A retention setting is out of range; <c>params.field</c>, <c>params.min</c> and <c>params.max</c> say which and what is allowed.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string LogsSettingsInvalid = "logs.settings.invalid";

    /// <summary>An app notification is invalid; <c>params.field</c> names the field (app, title, body), <c>params.maxLength</c> its limit.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string NotificationsMessageInvalid = "notifications.message.invalid";

    /// <summary>The signed-in user has no notification with this id (it was deleted, or belongs to someone else).</summary>
    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string NotificationsNotificationNotFound = "notifications.notification.not_found";

    /// <summary>A grant names an unknown resource, a level that does not fit it (<c>read</c> or <c>readWrite</c> for a shared folder, <c>allow</c> for the rest) or a resource twice; <c>params.resource</c> names it.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string PermissionsGrantInvalid = "permissions.grant.invalid";

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

    /// <summary>
    /// A storage command of a plan did not finish in time, usually because a disk stopped answering and ZFS waits for it in
    /// the kernel. The command may still finish later or wait until a restart. <c>params.command</c> is the program and its
    /// subcommand (<c>zpool destroy</c>), <c>params.minutes</c> how long the plan waited.
    /// </summary>
    [ErrorStatus(HttpStatusCode.GatewayTimeout)]
    public const string StorageCommandTimedOut = "storage.command.timed_out";

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

    /// <summary>
    /// ZFS did not answer a read a plan needs within <see cref="Storage.PlanReads.Limit"/>, or reported an error, so the
    /// plan cannot show what would change. A pool waiting for missing disks can block every <c>zpool</c> and <c>zfs</c> command.
    /// </summary>
    [ErrorStatus(HttpStatusCode.ServiceUnavailable)]
    public const string StorageZfsUnavailable = "storage.zfs.unavailable";

    /// <summary>
    /// The pool's I/O is suspended because too many of its disks are gone; destroying or exporting it now would wait in
    /// the kernel forever and block every other pool. <c>params.name</c> is the pool.
    /// </summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string StoragePoolSuspended = "storage.pool.suspended";

    /// <summary>
    /// Disks of the pool are gone, so a command that writes to all of them (destroy, export) would suspend it and block
    /// every other pool. <c>params.name</c> is the pool.
    /// </summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string StoragePoolDisksMissing = "storage.pool.disks_missing";

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

    /// <summary>The backup does not hold a part that should be restored; <c>params.category</c> names it.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string SystemBackupCategoryMissing = "system.backup.category_missing";

    /// <summary>The file is no Pithyx configuration backup, or it is damaged.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string SystemBackupInvalid = "system.backup.invalid";

    /// <summary>The backup password is wrong.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string SystemBackupPasswordWrong = "system.backup.password_wrong";

    /// <summary>The checked backup is gone: it expired after 15 minutes, another one was checked, or the daemon restarted.</summary>
    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string SystemBackupRestoreExpired = "system.backup.restore_expired";

    /// <summary>The restore would leave the database inconsistent; nothing was changed.</summary>
    [ErrorStatus(HttpStatusCode.InternalServerError)]
    public const string SystemBackupRestoreFailed = "system.backup.restore_failed";

    /// <summary>Another restore is running.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string SystemBackupRestoreRunning = "system.backup.restore_running";

    /// <summary>The file is larger than any configuration backup; <c>params.maxMb</c> is the limit.</summary>
    [ErrorStatus(HttpStatusCode.RequestEntityTooLarge)]
    public const string SystemBackupTooLarge = "system.backup.too_large";

    /// <summary>A newer Pithyx created the backup; <c>params.version</c> is its version. Update first.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string SystemBackupTooNew = "system.backup.too_new";

    [ErrorStatus(HttpStatusCode.InternalServerError)]
    public const string SystemInternalUnexpected = "system.internal.unexpected";

    [ErrorStatus(HttpStatusCode.Forbidden)]
    public const string SystemOriginForbidden = "system.origin.forbidden";

    /// <summary>Applying a time setting failed; <c>params.command</c> and <c>params.exitCode</c> say which command.</summary>
    [ErrorStatus(HttpStatusCode.BadGateway)]
    public const string SystemTimeApplyFailed = "system.time.apply_failed";

    /// <summary>A time server is neither a host name nor an address; <c>params.server</c> is the first such entry.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string SystemTimeServerInvalid = "system.time.server_invalid";

    /// <summary>More time servers than the daemon configures; <c>params.max</c> is the limit.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string SystemTimeServersTooMany = "system.time.servers_too_many";

    /// <summary>The system does not know the time zone; <c>params.zone</c> is the name that was sent.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string SystemTimeZoneInvalid = "system.time.zone_invalid";


    /// <summary>The Linux account of a user cannot be created; <c>params.reason</c> is <c>name_taken</c> (a system account has the name), <c>uid_taken</c> or <c>command_failed</c>.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string UsersAccountRefused = "users.account.refused";

    /// <summary>Every gid of the group range (30000 to 39999) has been given out once.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string UsersGidExhausted = "users.gid.exhausted";

    /// <summary>The built-in group <c>users</c> holds every user; it cannot be renamed, deleted or given members.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string UsersGroupBuiltin = "users.group.builtin";

    /// <summary>A group description is longer than <c>params.max</c> characters or has line breaks.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string UsersGroupDescriptionInvalid = "users.group.description_invalid";

    /// <summary>A group with this name exists; <c>params.name</c> names it.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string UsersGroupExists = "users.group.exists";

    /// <summary>A group name is not a valid Linux group name: lowercase letters, digits, underscores and hyphens, starting with a letter or underscore, at most 32 characters.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string UsersGroupNameInvalid = "users.group.name_invalid";

    /// <summary>The group name belongs to the system or to Pithyx; <c>params.name</c> names it.</summary>
    [ErrorStatus(HttpStatusCode.BadRequest)]
    public const string UsersGroupNameReserved = "users.group.name_reserved";

    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string UsersGroupNotFound = "users.group.not_found";

    /// <summary>The Linux group cannot be created; <c>params.name</c> is the group, <c>params.reason</c> <c>name_taken</c> (a system group has the name), <c>gid_taken</c> or <c>command_failed</c>.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string UsersGroupRefused = "users.group.refused";

    /// <summary>Every uid of the user range (20000 to 29999) has been given out once.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string UsersUidExhausted = "users.uid.exhausted";

    /// <summary>A user with this name exists; <c>params.username</c> names it.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string UsersUserExists = "users.user.exists";

    /// <summary>The last administrator can neither be deleted nor lose the right.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string UsersUserLastAdmin = "users.user.last_admin";

    [ErrorStatus(HttpStatusCode.NotFound)]
    public const string UsersUserNotFound = "users.user.not_found";

    /// <summary>An administrator tried to delete their own account.</summary>
    [ErrorStatus(HttpStatusCode.Conflict)]
    public const string UsersUserSelf = "users.user.self";

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
