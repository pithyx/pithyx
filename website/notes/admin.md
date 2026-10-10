# Admin docs research notes (code as of branch feature/website, 2026-10-10)

Conventions: paths relative to repo root. D### = TODO.md decision. `d/` = daemon/src/Pithyx.Daemon. i18n keys as `file:key`. "Admin" = policy `admin` (`d/Auth/Policies.cs:13`). Unless noted every endpoint named here is admin only.
Global facts:

- Env default: `/etc/pithyx/environment` dev|prod, prod if missing (CLAUDE.md). In dev only declared test disks usable (`storage.json:dev`).
- Storage/Settings/Store/Logs/Monitor apps are admin apps: `CoreApps.Admin` = Storage, Settings, Monitor, Logs, Store (`d/Apps/CoreApps.cs:29,41`). Users get Files + Viewer by grant; About for all (`CoreApps.cs:25`).
- Settings pages that exist (`settings.json:pages`): Info Center, Network, Regional settings, Shared folders, File services, Users, Backup and restore, Groups. nav also lists "Updates" and "About" keys but no page (see Open questions).

---

## storage.md

### Disk verdicts

- Verdict = Allowed / NeedsSerialConfirmation / Refused (`d/Storage/DiskGuard.cs:5-10`); UI "Available" / "Needs confirmation" / "Not available" (`storage.json:decision.*`); column header "For a new pool" (`storage.json:disks.status`).
- Order of checks (`DiskGuard.cs:83-120`), reason codes with UI text (`storage.json:reason.*`):
  - system_disk: "Holds the system. Never used for pools." Holds any of `/`, `/boot`, `/boot/efi`, `/var/lib/pithyx` directly or via partition/LVM/crypt/md/pool (`DiskGuard.cs:47`). No root mount in mount table => every disk refused as system_disk (`:52-56`).
  - read_only: "The disk is read-only."
  - mounted: "A file system on it is in use."
  - pool_member: "Part of the pool {pool}."
  - not_test_disk (dev only): "Not a declared test disk of this development machine." (`:99`)
  - duplicate_serial: "Another disk reports the same serial number, so it cannot be told apart."
  - empty: Allowed, "Empty and ready." (no partitions, no signatures)
  - no_serial: Refused, "Holds data and has no serial number to confirm with."
  - has_data: NeedsSerial, "Holds data ({contents}). Confirm with its serial number to use it; everything on it is lost."
  - not_checked: NeedsSerial, "Not checked yet. Confirm with its serial number to use it." (disk not probed yet)
  - confirmed: Allowed after the typed serial equals the disk serial (trimmed, exact) (`:116`), "Confirmed with its serial number."
  - confirm_replacement: replacement disk always needs serial even when empty (`StoragePlanner.cs:55-56`; `storage.json:reason.confirm_replacement`).
  - importable: for import of a pool, ZFS-labelled disks are expected, no serial typed (`DiskGuard.cs:123-131`).
- Verdict is re-evaluated right before every command of a plan; plan stale => nothing changes, error `storage.plan.stale` "A disk changed after the plan was shown. Nothing was changed; show the plan again." (`errors.json storage.plan.stale`).
- Create-pool dialog: "Type the serial number of {disk} ({serial})" (`storage.json:create.confirm`); "{count} disks are not available" (`create.refused`); "No disk can be used right now." (`create.noDisks`).
- A preview probes disks again and lists exact commands: "These commands would run, exactly as shown:" (`storage.json:create.commands`; button "Show commands" `create.preview`). Plan lifetime 10 min (`d/Storage/CommandPlan.cs:62`); expired => `storage.plan.expired` "This plan is more than 10 minutes old. Show it again to check the disks once more."; daemon restart/ran already => `storage.plan.not_found`.
- A plan runs in background; "You can close this window; it goes on in the background, and the notifications say when it is done." (`storage.json:run.background`). On failure `storage.command.failed` ("Nothing after it was run"); hang => `storage.command.timed_out`.
- Disks with data are cleared first: `zpool labelclear -f` on members then `wipefs --all` on partitions and disk (`d/Storage/Zfs/ZfsPlans.cs:246-254`).
- Devices are named by `/dev/disk/by-id` links (`ZfsPlans.cs` StablePath, summary of CreatePool).
- SMART state per disk shown on Disks page too (see Health).

### Creating a pool

- Admin only (`d/Storage/StorageEndpoints.cs:21-49`, Policies.Admin). Button "Create pool…" (`storage.json:create.button`); wizard steps "Disks", "Layout", "Encryption", "Name and check" (`wizard.steps.*`); buttons Back/Next/"Create pool".
- Pool name: starts with lowercase letter, then lowercase letters, digits, `-`, `_`, max 32 chars (`ZfsPlans.cs:263`, regex `^[a-z][a-z0-9_-]{0,31}$`); reserved: mirror, raidz, raidz1-3, draid, spare, log, cache, special, dedup, pithyx, and names starting `c0` (`ZfsPlans.cs:35,40`). Errors `storage.pool.name_invalid`, `storage.pool.name_taken`.
- Layouts and minimum disks (`ZfsPlans.cs:11-19`): single 1 (exactly one), stripe 2, mirror 2, mirrors (mirror pairs) 4 and even count, raidz1 3, raidz2 4, raidz3 5. Error `storage.pool.disk_count` "{layout} needs at least N disks", `storage.pool.layout_invalid`.
- UI labels (`storage.json:layouts.*`): "Single disk (no redundancy)", "Mirror", "RAID-Z1 (one disk may fail)", "RAID-Z2 (two disks may fail)", "RAID-Z3 (three disks may fail)", "Stripe (one failed disk loses everything)"; wizard titles plus "Mirror pairs" (`wizard.layouts.*`).
- Recommendation (`desktop/src/apps/storage/layouts.ts:103-111`): 1 disk single, 2 mirror, 3-5 raidz1, 6+ raidz2. Texts `wizard.recommend.*`. Mixed disk sizes: "every disk counts as large as the smallest, so {wasted} stay unused" (`wizard.mixedSizes`).
- Wizard shows usable size, tolerance, speed words (`wizard.usable`, `tolerance.*`, `speed.*`).
- Fixed pool options for every new pool (`ZfsPlans.cs:22-31`): ashift=12, compression=lz4, xattr=sa, acltype=posixacl, atime=off, normalization=formD, root canmount=off.
- Dataset layout created after the pool (`ZfsPlans.cs:90-100`): `<pool>/pithyx` (no mount), `pithyx/shares` (mountpoint /<pool>), `pithyx/homes` (/<pool>/homes), `pithyx/apps` (/<pool>/.pithyx/apps), `pithyx/system` (/<pool>/.pithyx/system), `pithyx/cache` (quota 2560M, compression off, no auto snapshot). Pools Pithyx did not create lack `pithyx`: error `storage.pool.not_prepared` "Pithyx did not set up the pool {pool} (it has no pithyx dataset), so it creates nothing on it."
- Other pool actions (admin): Replace… (always typed serial; "Type the serial number printed on the disk" `actions.replace.serialHint`), Add hot spare…, Remove spare, Take offline, Bring online, Destroy pool… (type pool name; `actions.destroy.*`; error `storage.pool.name_mismatch`). Destroy lists all datasets that go (`actions.datasets`).
- Destroy/export refused when disks missing (`storage.pool.disks_missing`) or pool suspended (`storage.pool.suspended`: would hang until box restarts).
- Pool in states: Healthy, Degraded, Faulted, Offline, Unavailable, Removed, Suspended, Unknown, "Not connected" (`storage.json:state.*`). Degraded hint: "A disk that comes back is rebuilt on its own (resilver); you can also replace it with a new disk." (`pools.degradedHint`).
- Hot spare: "A hot spare waits in {pool}; when a disk fails, ZFS rebuilds onto it right away." (`actions.spareAdd.explain`).
- Daemon reconnects returning disks: `zpool online` (30 s timeout) then `clear` (`ZfsPlans.cs` Reconnect/Resume, D270).

### Datasets and snapshots

- Datasets tab ("Datasets and volumes"); "Advanced" > Datasets > "Show datasets" (`storage.json:advanced.datasets`). Columns: Name, Used, Free, Limit, Compression, "Folder or size", Actions.
- Dataset name: letters, digits, `.`, `-`, `_`, start letter or digit, up to 64 chars (`errors.json storage.dataset.name_invalid`; regex `ZfsPlans.cs` DatasetName).
- Actions: Rename… (folder moves, shared folders follow), Limits… (Quota, Reservation, Compression, Record size), Delete… (type full name; lists child datasets and snapshots). Errors: `storage.dataset.managed` (home folder, app, system data cannot be renamed/deleted), `storage.dataset.protected` (pool root or Pithyx layout), `storage.dataset.in_use_by_share` ("Delete the share ... first"), `storage.dataset.quota_below_used`, `storage.dataset.reservation_too_large`, `storage.dataset.name_mismatch`, `storage.dataset.exists`, `storage.dataset.unchanged`.
- Compression choices: off, lz4, zle, zstd (levels 1 to 19), gzip (1 to 9) (`errors.json storage.dataset.compression_invalid`). Record size: power of two 4 KiB to 1 MiB, default 128 KiB; "1 MiB suits large files such as videos, 16 KiB databases" (`datasets.limits.recordSizeHint`, error `storage.dataset.record_size_invalid`). Quota/reservation: empty = no limit; units MB/GB/TB.
- Volumes: "Create volume…" only on pools Pithyx set up; size at least 1 MiB, rounded up to 16 KiB blocks; sparse option warns of write errors when pool fills (`datasets.volume.*`; `storage.volume.size_invalid`, `storage.volume.too_large`). Created below `<pool>/pithyx/volumes` (TODO.md line 1079 mentions; not verified in ZfsPlans listing above).
- Snapshots tab (`storage.json:snapshots.*`): "Take snapshot", columns Name/Taken/Space, kinds Hourly, Daily, Weekly, Monthly, Manual, "Before an app update" (`snapshots.kinds`), "Other" for snapshots not made by Pithyx. Names `pithyx-<kind>-<yyyyMMddTHHmmssZ>` (`d/Storage/Snapshots/Snapshot.cs:48`).
- Hold/Release; Roll back (type dataset name; deletes newer snapshots; "Every change made to the dataset since the snapshot is lost, for everyone who uses it." `warnings.changes_lost`); Delete (Pithyx deletes only its own snapshots, `storage.snapshot.not_pithyx`; held ones refuse, `storage.snapshot.held`).
- Schedules: hourly, daily, weekly (from Monday), monthly, one snapshot per period in UTC, keep N own snapshots, 1 to 1000 (`d/Storage/Snapshots/SnapshotService.cs:20`, `SnapshotScheduler.cs:74-84`); defaults when enabling: hourly 24, daily 7, weekly 4, monthly 6 (`Snapshot.cs:38-45`); new schedules start disabled (`SnapshotService.cs:232`). Held and foreign snapshots never pruned (`snapshots.schedulesHint`). Retention counts snapshots, not age (`SnapshotRetention.cs:14`). Error `storage.snapshot.schedule_invalid`.
- Users restore files from snapshots via "Previous versions" in Files (`snapshots.intro`; `d/Files/PreviousVersions.cs`).
- Shared-folder restore points = daily schedule kept 7 days, see shared-folders.

### Importing and exporting

- Import: "Import a pool…" (`import.button`). "The scan reads the labels of every disk and may wake a sleeping disk. It runs only when you start it." (`import.scanHint`; button "Scan disks"). POST `/api/storage/import/scan` (`StorageEndpoints.cs:36`).
- Pool always imported read-only first ("Import read-only"): nothing mounted, nothing changes; card shows "Read-only (preview)" (`pools.readOnlyHint`). Then "Import for use…" (exports and imports again, mounts safe datasets) or "Export" to end the preview (`change.import.*`, `change.discard.*`). Error `storage.import.preview_required`. Read-only pool cannot be changed: `storage.pool.read_only`.
- Datasets left unmounted and why (`storage.json:unmounted.*`): "it would cover a system folder", "it would hide something that is mounted there", "another dataset of the pool uses the same folder", "it is encrypted and locked"; they get `canmount=noauto` so stay unmounted after restart (`d/Storage/Zfs/ImportPlans.cs:72-84`). Shares/apps on them stop (`warnings.datasets_unavailable`).
- Pool last used by another system: shown "Used by another system"; needs "Import it anyway (force)" and pool name typed (`import.force`, `import.otherSystem`; errors `storage.import.force_required`). Forced preview remembered in memory only; after daemon restart confirm again (`StoragePlanner.cs:31-35`).
- Name already taken: choose "New name for the pool" (`import.newName`). Not importable (missing disks): `storage.import.not_importable`; unknown device: `storage.import.device_unknown` ("Pithyx does not know the disk of {devices}").
- Export…: unmounts datasets, "The data stays on the disks, which can then be moved to another system." (`change.export.explain`); lists datasets that become unavailable.

### Growing a pool

- "Add disks…" adds a new data vdev (layouts single, mirror, raidz1-3; `ZfsPlans.cs:201`; UI `addLayouts` needs mirror 2, raidz1 3, raidz2 4, raidz3 5, single only for exactly 1; `layouts.ts:122-137`). Warnings (`storage.json:warnings.*`, logic `d/Storage/Zfs/VdevRedundancy.cs`):
  - lowers_redundancy, replication_mismatch (needs typed pool name; pair rules at `VdevRedundancy.cs:Mismatch`: single only with single, mirror with same-width mirror, raidz same parity and width, mirror with raidz when same fault tolerance), irreversible ("these disks can never be removed from the pool again"), no_redundancy ("The new disk has no protection").
  - Removable again only while no data vdev is raidz/draid (`VdevRedundancy.cs` AssessAdd doc).
- "Expand with a disk…" = RAID-Z expansion, one disk at a time, pool stays usable, irreversible, always requires typed pool name, "data already on the pool keeps its old ratio of data to parity until it is written again" (`change.expand.explain`, cut off in my read; verify text). Only RAID-Z vdev (`storage.vdev.not_raidz`); one at a time (`storage.pool.expanding`); status "Expanding {vdev}", "waits until a rebuild is done" (`expansion.*`).
- Mirror: "More disks can be attached to the mirror later; the space stays that of one disk." Single can become a mirror by attaching a disk (`wizard.layouts.*.fixed`; `ZfsPlans` Attach, line ~95 of the 135-offset print). Not found as a UI button in i18n (no "attach" key): treat as plan only for UI.

### Encryption

- NOT IMPLEMENTED. Wizard step "Encryption" asks "Should the pool be encrypted?"; "No encryption" works; "Encrypt with a key" is refused: `storage.encryption.unavailable` "Encryption comes with the key vault in the next update. A pool can only be encrypted when it is created, so create it without encryption only if that is fine for its data." (`StoragePlanner.cs:315-326`, `storage.json:wizard.encryption.*`). Answer is mandatory (`storage.pool.encryption_required`).
- Decision on creation: "Encryption can only be chosen now ... It cannot be switched on later" (`wizard.encryption.explain`).
- Code only detects encrypted/locked datasets at import (`keystatus` in `ImportPlans.cs:55`; unmounted reason key_unavailable).
- Not in code (plan only): ZFS native encryption with root `<pool>/pithyx` (D106), key download/confirm, manual unlock, key vault on system disk, USB key, change-key (TODO.md "Step 9", marked [ ], moved after v0.2).

### Health and warnings

- Storage alerts (admin; `storage.json:health.*`), keys in `d/Storage/Health/StorageAlerts.cs`: pool suspended (critical), degraded (critical), faulted/unavail/removed/offline (critical), disk missing, data errors (critical), scrub errors (warning), capacity warning at 80 % and critical at 90 % (`StorageAlerts.cs:31-32`), SMART failed (critical), SMART warning (reallocated/pending/uncorrectable sectors or failed self-test). Mute / "Show again" per admin ("Muted for you"; `StorageHealthEndpoints.cs:40-57`). Admins get a desktop notification when an alert appears or worsens, and "Resolved: {problem}" when gone (`StorageAlertNotifier.cs:10,94,121`; `notifications.json:storage.alert.*`).
- Scrubs: "Scrub now", Pause, Resume, Stop (`StorageHealthEndpoints.cs:60-72`); errors `storage.scrub.running` / `storage.scrub.not_running`. Schedule per pool: Off / Weekly / Monthly, default monthly (`StorageSchedules.cs:34`), next run = last + 7 days or + 1 month (`:37-42`). PUT `/api/storage/schedules`; invalid => `storage.schedule.invalid`.
- SMART: read with `smartctl -n standby -j -a` (30 s timeout) so sleeping disks are not woken (`Health/SmartCommands.cs:14`). States Healthy / Warning / Failing / No SMART / Unknown; reasons: virtual machine, adapter does not report, asleep since start, not checked yet, read failed (`health.smart.reasons`). Self-tests: short (default weekly) and long (default monthly), one schedule each for all disks; asleep disks skipped and retried later, never woken (`health.selfTests.intro`; `StorageSchedules.cs:34`). Retry 1 h, read result after 10 min (short) / 2 h (long), max sleep 6 h (`StorageHealthMonitor.cs:20-28`).
- Temperature of disks: SMART page shows value ("{value} °C") from SMART; Monitor does not.
- Not in code (plan only): spindown per pool with hd-idle (TODO.md step 14, [ ], "off by default"); snapshot/SMART following spindown. No spindown option exists in storage.json. SSD cache (step 13) not checked.
- Not in code: notifications `storage.scrub.finished` and `storage.disk.warm` have texts in `notifications.json` but I found no emitter in daemon code (grep).

### Questions users will ask

- Why is my disk "Not available"? => reason text per disk (system disk, in a pool, mounted, read-only, duplicate serial). `storage.json:reason.*`.
- Why do I have to type a serial? => disk holds data/partitions, confirms you mean that disk; everything on it lost. `DiskGuard.cs:116-120`.
- Can I encrypt a pool? => not yet; create-time only, decision needed at creation. unknown timeline.
- Can I turn a single disk into a mirror / add disks later? => add vdev yes (warnings); RAID-Z expand yes one disk at a time; single-to-mirror attach exists in plan code, no UI key found.
- Does a scrub wake sleeping disks? => scrub reads all blocks, yes by nature; SMART and monitor reads do not. unknown for spindown (not implemented).
- Can I import a pool from another box? => yes, read-only preview first. `import.*`.
- Pool full (80 %/90 %)? => alerts at 80/90.
- Do normal users see Storage? => no, admin only.

---

## shared-folders.md

### Creating a shared folder

- Settings > Shared folders (`settings.json:nav.shares`, title "Shared folders"). Button "Create shared folder…" (`shares.create`). Admin only (`d/Shares/ShareEndpoints.cs:29-54`).
- Dialog (`shares.createDialog.*`): Name ("Letters, digits, spaces, dots, dashes and underscores."), Pool, "Description (optional)", "Show commands", "The shared folder becomes the dataset {dataset}.", "Create shared folder". No pool: "No pool is ready for shared folders yet. Create a pool in Storage first." + "Create a pool in Storage…".
- Every shared folder is its own ZFS dataset `<pool>/pithyx/shares/<name>`, own space, own snapshots (`d/Shares/SharePlanner.cs:92-139`; intro `shares.intro`). Mounted at `/<pool>/<dataset-name>`.
- Name: 1 to 64 chars, trimmed, letters (any Unicode letter) digits space `.` `-` `_`, not starting with `.` (`ShareService.cs:300-308`, MaxNameLength 64 at `:19`); error `fs.share.invalid`. Name must be unique case-insensitively (`fs.share.exists`). Description max 200 chars, one line (`ShareService.cs:21,310`; `fs.share.description_invalid`).
- Dataset name derived: lowercase, accents folded to Latin base letters (ß=ss, æ=ae ...), runs of other chars become `-`, max 64, no trailing `-`/`.` (`SharePlanner.cs:41-68`). Nothing usable (e.g. only non-Latin letters) => `fs.share.dataset_name_invalid`.
- Reserved names: `global homes printers print$ ipc$` (SMB, case-insens.) and dataset name `homes` (`SmbConfig.cs:38`, `SharePlanner.cs:33`); error `fs.share.name_reserved`. Path clash `fs.share.path_taken`. Pool without `pithyx/shares`: `fs.share.pool_not_prepared`.
- New folders start with restore points on (daily, kept 7 days) (`SharePlanner.cs:26,135`).
- Edit dialog (`shares.editDialog.*`): Name and description (renaming changes only the name users see, "the dataset keeps its name"), Rights, Network access (Offer over SMB, Show over SFTP, Offer over WebDAV, Offer over FTP, "Require encryption over SMB"). Defaults for each protocol on (`Database/Migrations/0017:19-21`, `0021:8-9`), SMB encrypt off.
- Overview table columns: Name, Pool, Used ("{used} used, {free} free" or "{used} of {quota}"), Description, Rights ("Administrators only" / "N users"; "{rw} read and write, {r} read only"), Restore points ("Daily, kept 7 days" / "Off") (`shares.columns.*`, `restorePoints.on/off`).
- Quota of a share dataset: set in Storage > Datasets > Limits (no field in share dialog). TODO step 11 "optional user quotas" is open.

### Rules for folders

- Folder shares (register an existing folder, `POST /api/shares` with path) exist only on development machines: `fs.share.folder_dev_only` "Existing folders can only be registered as shares on a development machine." Rules for those: absolute path, exists, not `/`, no symlinks, outside system dirs, not overlapping another share (`ShareService.cs:83-100,325+`). In UI only dataset shares are created; a plain folder shows "Folder" and delete can only keep files (`shares.folder`, `deleteDialog.folderOnly`, `fs.share.no_dataset`).
- Rights model: per user and group none/read/readWrite on the share (edit dialog "Rights"; "A user gets the most that it or one of its groups is given; administrators can always read and change everything." `editDialog.rightsIntro`; "Changes apply right away."). Every user is in group "users" -> row "every user" (`editDialog.everybody`). Enforcement on disk: Linux groups `pithyx-s<id>-r` / `-rw` and POSIX ACLs on dataset, so SMB, SFTP, Files agree (TODO.md step 11; `d/Users/ShareAcls.cs`).
- Deleting (type share name; `deleteDialog.*`): "Keep the data" (dataset and files stay, remain in Storage) or "Delete the data for good" (dataset, files, every snapshot). Errors `fs.share.data_choice_required`, `fs.share.name_mismatch`. Plan shows what is destroyed.
- Pool missing/locked: share unavailable `fs.share.unavailable` ("its pool is not connected" / "its pool stopped because disks are missing" / "its folder is not mounted"). Read-only grant: `fs.share.read_only`. No access looks like not found: `fs.share.not_found`.

### Recycle bin and retention

- Each share has a hidden folder `.pithyx-trash` at its root (`d/Files/FsPath.cs:20`); Files "Move to recycle bin" (Delete key) moves there; "Shift+Delete" deletes for good after asking (TODO.md line 973). Sidebar "Shares and recycle bin" (`files.json:sidebar`).
- Bin view "Recycle bin of {share}": columns origin ("From {path}"), "Deleted {date}", "Deleted for good: {date}"; actions Restore, "Delete for good", "Empty recycle bin" (confirm: "Everything in the recycle bin of {share} is deleted for good. This cannot be undone.") (`files.json:bin.*`, `emptyBody`). Restore recreates missing folders; name conflicts renamed by default (TODO.md line 955; code `FileOperations.Restore`, `Files/FileOperations.cs:238`).
- Retention per share: default 30 days, 0 = keep until emptied, max 3650 (`Database/Migrations/0007_trash.sql:4`, `Files/TrashService.cs:45`); cleanup runs hourly (`TrashService.cs:255`). Only admins may set it (`TrashService.cs:72-77`, PUT `/api/shares/{shareId}/trash`, error `fs.trash.retention_invalid` (ErrorCodes.cs:537)). NO UI found to change retention (grep desktop: only displays it, `desktop/src/apps/files/TrashView.tsx:42-65`). So retention is API only today.
- Display: "Deleted items are kept for {days} days." / "Deleted items are kept until the recycle bin is emptied." (`files.json:bin.retention*`).
- Over SMB and WebDAV deletes also go to this bin: SMB via `vfs_recycle` into `.pithyx-trash/<RecycleFolder>/%U`, keeptree, versions yes (`SmbConfig.cs:127-133`, `SmbRecycle.cs`); `veto files` hides the bin from SMB. WebDAV/FTP "files deleted ... go to the shared folder's recycle bin" (`settings.json:fileServices.webdav.trash`).
- Deleting a user's files by SFTP: SFTP does not use bin? unknown (TODO.md says SFTP sees bin folder but cannot open it).
- Restore points (snapshots): see Storage; "Restore points" dialog "What are restore points?" (`settings.json:restorePoints.*`): daily, kept 7 days, new folders on, space only for changed/deleted files, deleted file frees space after last point expires, help against ransomware, "no replacement for a backup on another device". Users restore via Files right-click "Previous versions"; Windows shows "Previous versions" (`settings.json:fileServices.smb.versions`, `SmbConfig.cs:134-137` shadow_copy2 reading `.zfs/snapshot`, format `pithyx-daily-%Y%m%dT%H%M%SZ`). Not available for folder shares ("Not for folder shares").

### Encryption over SMB

- Per share switch "Require encryption over SMB" with hint "Older clients without SMB encryption cannot connect then." (`editDialog.smbEncrypt*`). smb.conf: `server smb encrypt = required` else `if_required` (`SmbConfig.cs:119`). Global: SMB3 minimum, signing mandatory, no guests, NetBIOS off, port 445 only (`SmbConfig.cs:74-84`).
- Pool encryption (ZFS) is a different thing and not implemented (see Storage).

### Questions users will ask

- Can I share an existing folder? => no in production (dev only); create a shared folder, it becomes a dataset.
- Why can't I rename the dataset? => rename only changes the visible name.
- How long are deleted files kept? => 30 days default; admins can change per share via API only.
- Do restore points replace backup? => no, same disks.
- Why does Windows show Previous versions? => shadow_copy2 over daily snapshots.
- Can users make their own shares? => no, admin only.

---

## users-and-groups.md

### Creating a user

- Settings > Users (`settings.json:pages.users`), button "Create user" (`users.json:create.button`); admin only (`d/Users/UserEndpoints.cs:29-66`).
- Fields: User name (hint "Lowercase letters, digits, hyphens and underscores, starting with a letter or underscore."), "Display name (optional)", Password, Groups ("Every user is also in the group users.") (`users.json:field.*`). Rules: user name lowercased, regex `^[a-z_][a-z0-9_-]{0,31}$` (max 32), reserved list (root, daemon, sshd, pithyx, docker, users, ...) and `systemd-*` (`d/Auth/CredentialRules.cs:21-47,114`); errors `auth.username.invalid`, `auth.username.reserved`, `users.user.exists`. Display name max 64, no control chars (`CredentialRules.cs:15,58-70`). Name used by a system account outside the Pithyx range: `users.account.refused` "A system account may already use this name." (`UserService.cs:47-50`).
- Password: at least 8 characters, max 1024, well-known passwords refused (embedded common list); no composition rules; strength meter warns, never refuses beyond the list (`CredentialRules.cs:14-17,75-100`, `auth.json:password.*`). Errors `auth.password.too_short`, `too_long`, `too_common`. Hash: Argon2id 64 MiB, 3 passes (`PasswordHasher.cs:20-22`).
- Every user is a Linux account with own uid from 20000 to 29999, never reused; groups gid 30000 to 39999; apps uid 40000 to 49999 (`d/Users/AccountReconciler.cs:42-43`, `GroupReconciler.cs:41-42`, `UidAllocator.cs:28-29`); exhaustion errors `users.uid.exhausted`, `users.gid.exhausted`. Intro text: "Every user is a Linux account of its own, so files they create belong to them. Their uids are never given out again." (`users.json:intro`). Shell is `/usr/sbin/nologin` unless SSH login right and SSH on (`LinuxAccounts.cs:87`, `AccountReconciler.cs:44`).
- Default rights for a new user: desktop login, SMB, apps Files and Viewer (`d/Permissions/Grant.cs:53`); the create request may override grants (`UserService.cs:70-73`).
- Password also set on Linux and Samba accounts at creation (`UserService.cs:90-92`); a user without it: "Not ready for SMB and SFTP yet: the user signs in once on the web, or you set a new password." (`users.json:fileServices.signInOnce`).
- Edit (display name, groups, deactivate); "New password": "The user is signed out everywhere and signs in with the new password." + "SMB and SFTP use the new password as well." (`users.json:password.*`; `UserService.cs:108-145`).
- Badges: Administrator, Two-factor, Deactivated (`users.json:badge.*`).

### Groups

- Settings > Groups; "Create group"; admin only. Group: name (same rules as user names, plus not starting `pithyx-`, `d/Auth/CredentialRules.cs:49-64`), "Description (optional)" max 200 (`GroupService.cs:18`), members. Errors `users.group.name_invalid`, `name_reserved`, `exists`, `description_invalid`, `not_found`, `refused`.
- Built-in group "users": every user is a member, cannot be renamed, deleted or given members (`users.json:groups.builtinDescription`; `users.group.builtin`; gid 100 `GroupReconciler.cs:45`).
- "Rights given to a group apply to all its members." Delete group: "The group goes; its members stay. Files keep the group id, and it is never given to another group." (`groups.delete.intro`).
- Group rights dialog "Rights of the group {name}" (`groups.rights`).

### Special rights

- Four rights (`users.json:grants.right.*`, `d/Permissions/Grant.cs:26-29`): "Administrator (may change everything)", "Sign in to the desktop", "Sign in over SSH with a shell", "Restart and shut down". Rights dialog sections: "Special rights", "File services" (SMB (network drives), SFTP, WebDAV, FTP), "Apps", "Shared folders" (`grants.*`).
- Administrator has everything; no deny rules: "The user has these rights and everything its groups give it. There are no rules that take a right away." (`grants.userIntro`). Effective = union (`d/Permissions/PermissionResolver.cs:8-50`).
- Desktop login right missing => cannot sign in to the web desktop; open sessions and sockets end when revoked (`d/Auth/AuthService.cs:96,133`; `PermissionChangePublisher`, `PermissionEndpoints.cs:95-130`).
- Power right: restart/shutdown policy `power` = admin or right:power (`Auth/Policies.cs:16,36`). Endpoints for reboot not checked by me.
- SSH login right: shell only when "SSH logins" setting on (File services page); see security.

### Deactivating and deleting

- Deactivate / Activate button (`users.json:disable.*`): deactivated user has no rights at all, sessions deleted (`UserService.cs:121-125`, `PermissionResolver.cs:30-33`); cannot deactivate yourself (`users.user.self`).
- Delete: "The account goes. The user's files and home folder stay, and the user id is never given to anyone else." Type user name to confirm (`users.json:delete.*`). Removes grants and memberships, cancels the user's jobs, drops notifications (`UserService.cs:158-182`). Cannot delete yourself; last administrator stays: `users.user.last_admin` "The last administrator cannot be deleted or lose the administrator right." (also on deactivate/removing right: `PermissionService.cs:110-116`).
- Admin "forgot password" for other users: admin sets new password; no email reset ("Pithyx has no reset by email", `auth.json:forgot.intro`).

### Home folders

- Home = dataset `<first pool>/pithyx/homes/<user>` at `/<pool>/homes/<user>`, mode 0700, owned by user (`d/Users/LinuxAccounts.cs:56-69,173-198`, `ZfsPlans.cs:CreateHomeDataset`). "The user gets a home folder on the first pool when there is one." (`users.json:create.intro`); no pool => column shows "No pool yet" (`users.json:noHome`) and home is created later once a pool exists (reconcile loop `UserEndpoints.cs:130-141`).
- Home folders are kept on user deletion. Whether homes appear in Files: unknown (not checked).
- login.defs: daemon warns if `/etc/login.defs` ranges reach 20000-49999 (`d/Users/LoginDefs.cs`).

### Questions users will ask

- Forgot my password? => ask an admin; admin forgot => recovery key from setup; else reinstall. `auth.json:forgot.*`.
- Why was my user name refused? => reserved/system name or invalid chars.
- Does deleting a user delete files? => no.
- Can there be several admins? => yes, right "Administrator".
- Do weak passwords work? => length 8 and not in common list only.

---

## permissions.md

### The model

- Grants to users and groups; effective = union of own grants + all groups' grants (incl. built-in `users`); admins have everything; no deny rules; deactivated user none (`d/Permissions/PermissionResolver.cs:21-50`, D290).
- Resources (`Grant.cs:26-36`): `right:admin|desktopLogin|sshLogin|power`, `protocol:smb|sftp|webdav|ftp`, `share:<id>` (none/read/readWrite, highest wins `PermissionResolver.cs:44`), `app:<id>`.
- Subject kinds user, group, app (apps join via D303; an app never becomes admin through a grant `PermissionResolver.cs:49`).
- API: GET/PUT `/api/users/{id}/grants`, `/api/groups/{id}/grants`, GET `/api/permissions/catalog` (admin; `PermissionEndpoints.cs:51-81`).
- Not in code: NFS (D107; no NFS service code), Kerberos, per-user quotas (TODO step 11 [ ]).

### Shared folder access

- Levels "No access" / "Read" / "Read and write" (`users.json:grants.access.*`). Set per user/group in Rights dialog (Users) or per share in the share's edit dialog; "ends up with: {access}" shows the effective result (`settings.json:editDialog.effective`).
- Admins always read and write every share (`PermissionResolver.cs:17`).
- On disk: generated Linux groups `pithyx-s<id>-r` and `-rw` plus POSIX ACLs; reconciler compares `getfacl` and logs differences (TODO.md step 11 line 1203; `d/Users/ShareAcls.cs`). ACLs are never edited over SMB (Pithyx is the source), D102.
- Via the API: read needs read, write needs readWrite (`fs.share.read_only`); no access hides the share (`fs.share.not_found`).

### Protocol access

- Per user/group: SMB, SFTP, WebDAV, FTP (`protocol:*`) plus per share "Offer over ..." switches; both must allow (`settings.json:editDialog.protocolsIntro` "Users reach this folder over the protocols allowed for them on the Users page."). New user default: SMB only.
- Server-side switches live in File services (see file-services).
- SSH login is a right, not a protocol grant: needs right:sshLogin and the SSH logins switch (`FileServiceSync.cs:114`).

### App access

- Per app: grant `app:<id>`; core apps: Files and Viewer by grant (new users get both), About for everybody, Storage/Settings/Monitor/Logs/Store only with administrator right (`CoreApps.cs:22-60`; Store admin only D320).
- Third-party apps: "Who may use the app": Everybody / Administrators only / Selected groups and users (`store.json:detail.access.*`), "Administrators may use every app". Install dialog suggests admins only when app sees all users, runs root or asks special rights (`install.accessSuggested`).
- Per-app permission toggles ("Turn a permission off to take it from the app. It stays off after updates. Open windows of the app get the change within a few minutes." `store.json:detail.permissions.intro`).

### When a change applies

- Rights changes apply at once: shares/file services reconciled, every open WebSocket re-authorized, user gets `session/permissions`, connection of a user who lost desktop login is closed (`PermissionChangePublisher`, `PermissionEndpoints.cs:95-130`). settings.json: "Changes apply right away." (`editDialog.rightsIntro`).
- Revoking a protocol right: SMB/SFTP via reconcile of accounts/groups; WebDAV/FTP recheck rights each request/command (`FtpSession` doc, `WebDav.cs`).
- Sign-in caching for protocols: password check remembered 5 minutes keyed by stored hash (`FileServices/ProtocolSignIn.cs:22`), new password ends it at once (D291).
- App tokens: 5 minutes; app permission changes close windows within minutes (CLAUDE.md Apps; `store.json` text "within a few minutes").
- Sessions: idle 7 days, absolute 30 days (`d/Auth/SessionService.cs:14-15`); deactivate/delete/password reset delete the sessions.

### Questions users will ask

- Why can't I see a shared folder? => no grant (hidden as not found).
- Does a group overrule a user? => no, union, most access wins.
- Can I deny a user something a group gives? => no.
- Why can't I log in to the desktop? => missing "Sign in to the desktop".

---

## file-services.md

(Settings > File services; admin only; GET/PUT `/api/...` `FileServiceEndpoints.cs:99-106`. Intro: "File services let other computers open your shared folders over the network. Users sign in with their Pithyx user name and password." `settings.json:fileServices.intro`. Service state words: Off / Running / "Not running" / "Not installed" `fileServices.state.*`.)

- Users must sign in once on the web (or admin sets password) so SMB/SFTP learn it; shown at Users page (`fileServices.rights`, `users.json:fileServices.signInOnce`).
- Who may use what: user/group protocol rights; which folders: per share switches (`fileServices.rights`).

### SMB

- Default ON in fresh DB, workgroup `WORKGROUP` (`Database/Migrations/0017:12`). Switch "Offer shared folders over SMB"; Workgroup: 1 to 15 chars, letters/digits/dot/dash/underscore, starts alnum (`SmbConfig.cs:41-46`; `fileservices.workgroup.invalid`). Hint "Most networks use WORKGROUP".
- Text: "Network drives for Windows, macOS and Linux. Only encrypted, signed connections of current systems (SMB 3) are accepted." (`fileServices.smb.explain`). Config facts: `server min protocol SMB3`, signing mandatory, `map to guest never`, `restrict anonymous 2`, NetBIOS disabled, port 445 only, per share encryption required or if_required (`SmbConfig.cs:68-137`). NetBIOS name = host name first 15 chars (`network.global.hostNameHint`).
- Connect: Windows File Explorer `\\<address>\<share>`, macOS/Linux `smb://<address>` (`fileServices.smb.windows/.mac`; the UI shows the addresses).
- smb.conf generated from SQLite with "Managed by pithyx" header; own settings in include file `SmbConfig.LocalInclude` never overwritten; checked with testparm before reload (TODO step 12; header in `SmbConfig.cs`, `FileServiceOptions` `FileServiceContracts.cs:15-18`).
- Deletes go to recycle bin; restore points = "Previous versions" in Windows. ACLs cannot be changed over SMB (`nt acl support` off, D102 / TODO line 1240s).
- Not set up: Windows network browsing (WS-Discovery) and Bonjour; clients connect by address (TODO.md line 1244, D287).
- Samba unit disabled in image; daemon starts it when a share needs it (`image/customize.sh:38-39`).

### SFTP

- Default OFF (`0017:12`). Switch "Offer shared folders over SFTP". "Encrypted file transfer over SSH, for example with FileZilla, WinSCP or the sftp command. Users see only their shared folders and get no shell." (`fileServices.sftp.explain`). Connect with `sftp://<address>`.
- Implementation: sshd drop-in `/etc/ssh/sshd_config.d/50-pithyx.conf`, `internal-sftp -u 0007`, chroot per user `/run/pithyx/sftp/%u` built from bind mounts of the user's shared folders, password only (pubkey off), no TTY, no forwarding (`FileServices/SshdConfig.cs:33-46`, `SftpViews.cs`). Pithyx users without SFTP/SSH right cannot use SSH at all (`:50-56`).
- "Switching SFTP off only stops letting Pithyx users in; it does not stop SSH for other accounts." Pithyx never stops sshd (`FileServiceSync.cs:289-293`). Enabling SFTP or SSH logins runs `systemctl enable --now ssh.service` (`:300`).
- Config checked with `sshd -t`, reverted if refused (`FileServiceSync.cs:272-285`); state Failed shown.
- Gives no shell; SSH logins with shell are separate (see security).

### WebDAV

- Default OFF. Served by the daemon at `/webdav/` on its own address (7484 today), Basic auth with Pithyx name and password, class 1 and 2 (locks granted not enforced), methods OPTIONS PROPFIND PROPPATCH GET HEAD PUT DELETE MKCOL COPY MOVE LOCK UNLOCK (`FileServices/WebDav.cs:121-135,161`). Needs right protocol:webdav and share switch "Offer over WebDAV". Root lists user's allowed shares. Deletes to bin; new files owned by the writing user (`settings.json:fileServices.webdav.trash`).
- Basic auth over plain HTTP sends password readable: the box has no HTTPS yet (see security).

### FTP

- Default OFF; "Offer shared folders over FTP with TLS" (explicit FTPS: AUTH TLS, PROT P, no implicit TLS); "Also allow plain FTP without encryption" (default off; warning `fileServices.ftp.plainWarning`; refusing again ends plain sessions). Only passive mode: PASV/EPSV; PORT and EPRT answer "502 Only passive mode" (`Ftp/FtpSession.cs:249-252`).
- Control port 21 in real mode (`Program.cs:95`, `FileServiceContracts.cs:20`), env `PITHYX_FTP_PORT` override. Passive data ports 50000 to 50099 (`Ftp/FtpServer.cs:27`). Data connections only from control connection address. Idle timeout 10 min, data timeout 30 s, max line 4096 (`FtpSession.cs:31-33`).
- Certificate: self-signed ECDSA P-256 for host name, valid -1 day to +10 years, stored `/var/lib/pithyx/tls/ftps.pfx` mode 0600, part of config backup; SHA-256 fingerprint shown in UI ("Certificate fingerprint (SHA-256):") (`TlsCertificate.cs:13-38`, `settings.json:ftp.tls/fingerprint`). Program asks once whether to trust it; choose "explicit FTP over TLS".
- Sign-in: same as WebDAV, rate limit 5 failures per user and 30 per address per 5 minutes (`Auth/LoginRateLimiter.cs:13-15`); success remembered 5 min.
- Intended for devices like scanners ("For devices and programs that know neither SFTP nor WebDAV, such as scanners.").
- No firewall rules are written by the daemon for 21 or 50000-50099 (only an nftables chain for app containers, `Apps/Docker/DockerHost.cs`); image firewall state unknown.

### Limits

- NFS not implemented (D107 plan). No guest access anywhere. SMB 3 only (no old clients). SFTP password only. Rights cannot be changed from clients (ACL edits over SMB ignored).
- Symlinks are shown but never copied (Files; TODO line 1367). Mac/Windows name quirks: SMB uses `catia fruit streams_xattr` (`SmbConfig.cs:99`).
- File services run in/alongside daemon: FTP/WebDAV inside the daemon process.
- Windows discovery not available (see SMB).

### Questions users will ask

- Why can't I log in over SMB right after the account was created? => password learned at creation by admin; for migrated users sign in once on web.
- Can I use my phone's file app? => WebDAV or SFTP.
- Which ports? => 445 SMB, 22 SFTP (sshd), 7484 WebDAV (HTTP), 21 + 50000-50099 FTP.
- Is FTP safe? => TLS by default, plain only if admin allows.
- Does 2FA apply? => no, protocols use password only (D291).

---

## network.md

(Settings > Network, admin only; GET `/api/system/network`, `/check`, PUT, POST `/changes/{id}/confirm|revert` `d/Network/NetworkEndpoints.cs:30-50`; live topic `system/network`.)

### Cards and addresses

- Page lists network cards, bonds, VLANs (`settings.json:network.kinds`), state "Connected"/"No cable"/"Off", IPv4 method "Automatic (DHCP)"/"Static"/"Off"/"Set outside Pithyx", addresses, gateway, name servers, hardware address, MTU, speed, duplex (`network.*`).
- Per interface (`d/Network/NetworkContracts.cs:InterfaceConfig`; D337): Interface on/off, IPv4 DHCP/static/off, IPv6 automatic (router and DHCPv6)/static/off (`network.json:ip.*`), address with prefix length (e.g. 192.168.1.10/24, 2001:db8::10/64), gateway optional and inside the network, MTU 1280 to 9000 (576 to 9000 with IPv6 off), empty = 1500 (`NetworkValidator.cs:43-46`; `settings.json:dialog.mtuHint`). Cards without settings use DHCP (image file `80-pithyx-ethernet.network`, D292).
- Changes are drafted ("Changes not applied yet", "Apply…", "Discard changes") and apply together as one change (`network.draft.*`). "Back to DHCP" button, "Remove" settings.
- Where Pithyx may change: everything on prod; on dev only interfaces in `/etc/pithyx/test-nics`, never host name/DNS/route (`NetworkHost.cs:28-30`; `network.global.not_changeable`). "Pithyx does not change this interface on this machine." (`network.notChangeable`).
- Not changeable here: IPv6 "Auto" detail; bridges (come with VMs, D337).
- Engine: systemd-networkd and resolved files `/etc/systemd/network/10-pithyx-<name>.{network,netdev}`, `/etc/systemd/resolved.conf.d/pithyx.conf`; host name via hostnamectl and `/etc/hosts`; Samba netbios name follows (D292, `NetworkdConfig.cs`).
- Rule: never leave a box with no interface that has an address (`network.config.unreachable`).
- Names of bonds/VLANs: up to 15 lower-case letters, digits, dots, hyphens, underscores starting with a letter (`network.interface.name_invalid`); must not clash with Docker's `px-*`, `docker*`, `br-*`, `veth*` (D292).

### Bonds

- "Add bond"; modes (`network.bondModes/bondModeHints`): "Active-backup" ("One card carries the traffic; another takes over when its cable fails. Works with every switch."), "LACP (802.3ad)" ("All cards together. The switch ports must form an LACP group."), "Adaptive load balancing" ("All cards send and receive without help from the switch."). Fields: Name, Network cards (members, at least one, cards must have no settings of their own: "A card with its own settings must go back to DHCP first."), Mode, Primary card (active-backup), LACP rate Slow (every 30 seconds) / Fast (every second) (`network.dialog.*`; `NetworkContracts.cs` BondSettings). Errors `network.bond.members_missing`, `network.bond.member_invalid`, `network.bond.primary_invalid`.

### VLANs

- "Add VLAN": Parent (a card or a bond), VLAN ID 1 to 4094 "as the switch port tags it" (802.1Q); errors `network.vlan.id_invalid`, `network.vlan.duplicate`, `network.vlan.parent_invalid`.

### DNS, search domains and the default route

- Global card "Host name and name servers" (`network.global.*`): Host name (letters, digits, hyphens, 1 to 63 chars, no leading/trailing hyphen, error `network.host_name.invalid`; "Windows networks see its first 15 characters."), Name servers up to 4, "Empty uses those DHCP announces; set ones replace them." (`NetworkValidator.cs:41`, `network.dns.too_many`, `network.dns.invalid`), Search domains up to 6 (`network.search_domain.*`), "Default route via" with Automatic option; chosen interface must be enabled, have settings and a gateway (`network.default_interface.invalid`). Metric 100 for chosen route, higher for others (D337).

### The confirmation timer

- Every change waits 60 seconds (`NetworkConfirmation.Window`, `d/Network/NetworkConfirmation.cs:10`); text "Every change waits 60 seconds for your confirmation and goes back on its own without it, so a wrong address cannot lock you out." (`settings.json:network.intro`). Banner "{seconds} left to keep the new network settings", buttons "Keep" / "Undo now"; "If this page stops answering, open the box at its new address and keep the change there:" + addresses (`network.pending.*`, `apply.*`).
- One change at a time (`network.change.pending`). Rollback on timeout, Undo, or daemon restart (pending change stored in `network_pending`, rolled back at start; `NetworkService.cs:35,233`). `network_config` and `network_pending` are NOT in config backups on purpose (D292; `BackupDatabase.cs:72-73`).
- Failed apply (a command failed) rolls back at once: `network.apply.failed`.
- Warning when browser connection uses the changed interface (`network.dialog.connectionWarning`, `apply.connectionWarning`).
- Change from console shows in web with "Someone at the box's console changed the network. Keep the change here or on the console, or the box goes back on its own." (`network.pending.fromConsole`).

### Locked out: the console menu

- Console tty1 (`pithyx-console.service`, script `image/rootfs/usr/lib/pithyx/console.sh`) shows greeting with addresses and, no password needed (physical access is the proof): `[s] Set a static address`, `[r] Reset the network to DHCP`, and while a change waits `[k] Keep the new network settings`, `[u] Undo them now`, countdown; `Alt+F2: log in` (login prompt moved to tty2) (`console.sh:34-48,78-92,125`). Static asks Card number, Address with prefix length, Gateway (empty none), Name servers (empty keeps) (`console.sh:84-92`); keeps 60 s timer.
- Reset: "Reset every network card to DHCP and remove bonds, VLANs and name servers? [y/N]" (`console.sh:125`); no timer; keeps host name (`NetworkService.ResetAsync`, `NetworkService.cs:242-267`; D337).
- Mechanism: files under `/run/pithyx/console` (request/result/pending/cards), root only (`NetworkConsole.cs:25-47`). Only IPv4 static via console.
- CLAUDE.md says the live and installed system have no login (root locked, SSH off): so console menu is the only local recovery; tty2 login prompt exists but root locked: unclear what Alt+F2 accomplishes (see Open questions).

### Not in code / limits

- Bridges, Wi-Fi, firewall UI not present. IPv6 forwarding etc none. VPN none.

### Questions users will ask

- I set a wrong IP, now what? => wait 60 s, it rolls back; or console menu.
- Can I use two network cards? => yes: separate settings, bond, or VLAN.
- Why are my NIC settings greyed? => dev machine policy.

---

## security.md

### HTTPS and certificates

- NOT IMPLEMENTED. Daemon listens plain HTTP on 7484 (`d/Program.cs:33,61`); real-mode production build also listens on port 80 which redirects to 7484 (`Program.cs:57-58,209`, `Hosting/PlainHttpRedirect.cs:4`). No `UseHttps`, no ACME, no local CA code (grep for letsencrypt/acme/CA found nothing; comment `Program.cs:202`: "HTTPS on 443 arrives with the TLS work in M5"). Session cookie `Secure` only when request is HTTPS (`Auth/AuthEndpoints.cs:170-171`); `SameSite=Strict`, HttpOnly, name `pithyx_session` (`SessionService.cs:13,89-91`). CSRF: header `X-Pithyx-Request: 1` required on state-changing calls (`CsrfMiddleware.cs:11`).
- Only TLS in code today: FTPS self-signed certificate (`FileServices/TlsCertificate.cs`; doc says "The trust step of M5 replaces it with the box's certificate").
- Not in code (plan only): HTTPS on 443 with self-signed cert from install, trust wizard (local CA download or own domain with Let's Encrypt DNS-01 wildcard), "Trust this device" setup step, passkeys and service worker after trust (CLAUDE.md "Network", TODO.md line 613, M5 items, D?).
- Backup category "Certificates" holds `/var/lib/pithyx/tls/*` (so far the FTPS cert) and SSH host keys.

### SSH

- Off by default (`Migrations/0020:76` ssh_enabled 0; `image/customize.sh:38-39` disables ssh.service). Two separate things (File services page):
  - SFTP switch: SFTP only, no shell.
  - "SSH logins": "Let users with the SSH login right sign in with a shell" (`settings.json:fileServices.ssh.*`); "Users with the right to sign in over SSH get a shell on the box. Off by default." Warning: "A shell reaches the whole system with the user's rights. Give the right only to people who need it. Switching this off never stops SSH for the system's own accounts."
- Shell users: password and public key auth allowed, no forwarding/tunnel/X11 (`SshdConfig.cs:41-49`); needs `right:sshLogin`; account shell set to login shell only when SSH on (`LinuxAccounts.cs:87`). Pithyx accounts without SFTP/SSH: all SSH auth methods off (`SshdConfig.cs:50-56`).
- Host keys removed at image build and regenerated on first boot (`image/customize.sh:49`), included in config backup.
- Enabling uses `systemctl enable --now ssh.service`; Pithyx never stops sshd (`FileServiceSync.cs:289-300`).
- Not in code: MOTD with ASCII logo and warning (TODO.md line 1393 "M5"; no motd in image/ or daemon). Shell prompt `λ pithyx ~ ›` unverified.

### Two-factor sign-in

- TOTP per user (any user, self-service): user menu "Two-factor authentication" (`desktop.json:twoFactor`, `desktop/src/auth/TotpSetup.tsx`, opened from `desktop/Desktop.tsx:764`). Flow: "Set up" > scan QR / enter key > code > "Turn on" > 10 recovery codes shown once, each usable once (`auth.json:totpSetup.*`, `AuthService.cs:17,224`). otpauth: SHA1, 6 digits, 30 s, issuer Pithyx (`AuthService.cs:204`). Accepts previous/next step (window +-1), each time step only once (`:306-314`). Turn off needs the password (`totpSetup.disablePassword`).
- Login: password, then code or "Use a recovery code instead" (`auth.json:totp.*`); challenge valid 5 min (`Auth/LoginChallenges.cs:11`). Rate limit 5 failures per user and 30 per address in 5 min (`LoginRateLimiter.cs:13-15`).
- Sessions: idle 7 days, absolute 30 days (`SessionService.cs:14-15`).
- SMB/SFTP/WebDAV/FTP do not use 2FA (password only; D291).
- Users with 2FA badge "Two-factor"; recovery with key turns TOTP off for that admin (`AuthService.cs:254-289`).

### The recovery key

- Created in first setup: "Your recovery key ... the only way to reset the administrator password. It is shown only now." Buttons Copy key, Print, checkbox "I have stored the recovery key" (`auth.json:recoveryKey.*`). Format `PTHX-` + 26 chars Crockford base32 in groups of 4 (`AuthService.cs:15-16,293`); stored as Argon2id hash.
- Reset (Sign in > "Forgot password?" > administrators section): needs recovery key + username of an administrator (deactivated admin still ok) + new password; ends all that user's sessions, turns TOTP off, issues a NEW recovery key (old stops) shown once (`AuthService.cs:253-290`, `auth.json:forgot.*`). One key for the whole box (setting `recovery_key_hash`).
- Without key: "the system has to be reinstalled" (`auth.json:forgot.adminIntro`). Normal users: ask an administrator.
- Error: `auth.recovery.invalid` (same error for wrong key/non-admin; time wasting hash to avoid timing leak `AuthService.cs:269`).
- Key is part of the config backup (settings table), restore brings back backup's key (TODO.md line 1376).

### What leaves the box

- No telemetry, no phone-home (CLAUDE.md principle; no HttpClient to the internet found: HttpClient only for Docker socket `Apps/Docker/*`).
- Outgoing connections by design:
  - App catalogs: git over https/ssh to the catalog repository, default `https://github.com/pithyx/apps-official.git` (on) and `apps-community.git` (off), at 03:00 +0-60 min delay daily, when added, on "Check now" (`Migrations/0025:35-37`, `StoreService.cs:58-60`). Disable by switching catalog off. GitHub SSH host keys built in (`CatalogGit.cs:79-81`).
  - Container images for catalog apps from the registry named in the catalog (`ghcr.io/...`) unless packaged (CHANGELOG A14).
  - Time sync: systemd-timesyncd default servers, or Settings > Regional "Time servers" (`SystemSettingsService.cs:21`, `settings.json:regional.servers*`).
  - Apps with permission "Internet" (`store.json:permission.network.internet`) get a second network with outbound access; others none.
  - OS updates via Debian apt/updater not part of daemon today; Updates page absent.
- Crash reports/support bundles: not in code (grep). Logs stay local: `/var/log/pithyx/daemon.json` (20 MB x 10 files, `Hosting/LoggingSetup.cs:10-11`) and `logs.db`.

### Other facts worth a line

- Passwords: Argon2id; password list check; no password reset by email.
- Secrets masked in logs (`Hosting/SecretMaskingEnricher.cs`).
- No Secure Boot support (installer; CLAUDE.md) not checked in code.

### Questions users will ask

- Is my traffic encrypted? => not yet on the web UI (HTTP); FTPS/SMB3 encrypt where configured. Say plainly.
- Why does the browser say not secure? => same.
- Lost the recovery key? => reinstall (if no admin password either).
- Does Pithyx send data to the project? => no.
- Can I enable SSH for myself? => admin: grant right + enable "SSH logins".

---

## apps-and-store.md

### The Store

- System app `org.pithyx.store`, admin only (D320; `CoreApps.cs:33`). Users find apps in the Pithyx menu. Tabs: Installed, Official, Community, Own, "Updates ({count})" and gear "Store settings" (`store.json:tabs.*`, `settings.open`).
- Official ("Apps of the Pithyx project, reviewed, built and signed by the project."), Community ("Apps from developers outside the project, reviewed by the project before they are published."), Own ("Apps of the catalogs you added. They are not verified by the Pithyx project.") (`catalog.*.intro`). Search "Name, description or developer". Detail: screenshots, license, homepage, versions, "needs Pithyx {range}" (`catalog.*`).
- Badges: "Pithyx", "Community", "Unverified" (`catalog.badge.*`); sideloaded apps "Unverified".
- API `/api/store/**` admin (`StoreEndpoints.cs:14`).

### Installing from a file

- Store > Installed > "Install from file" (`.pithyxapp`), also "Update from file" in app detail. Dialog "Install an app": permissions list ("Permissions it asks for", "New" marker, "No longer asked for"), folders it asks for, backend yes/no text, root warning, special rights, "Who may use it", "Pool for app data", warning "This package comes from a file and is not verified." (`store.json:install.*`).
- Upload `PUT /api/apps/packages`: file max 256 MB, unpacked max 512 MB, max 10 000 entries, manifest max 64 KB (`d/Apps/Packages/PackageReader.cs:16-19`; errors `app.package.too_large`, `app.package.invalid`, `layout_invalid`). Checked package waits at most 15 minutes (`daemon/CLAUDE.md`; `app.package.not_found`).
- Version actions: install, update from X to Y, reinstall ("replaces its files"), downgrade ("Its data may not fit.") (`install.action.*`; downgrade may be refused `app.package.downgrade_unsupported`: use "Back to the previous version"). Needs Pithyx range else `app.package.box_too_old`.
- Ids: author's domain reversed + name, `org.pithyx.*` refused from sideloads (`app.manifest.id_reserved`, `id_invalid`).
- Uninstall: "Keep the data" (installing again finds it) or "Delete the data" (databases, settings, files and snapshots; irreversible); mapped shared folders stay either way (`store.json:uninstall.*`).
- Enable/disable: "Users can open the app"; disabled keeps data and access (`detail.enabledHint`).
- Folders: apps ask for storage; admin maps each to a folder in a shared folder (read only or read and write); required ones must be mapped or backend does not start (`detail.storage.*`, `app.backend.storage_unavailable`, `app.mapping.invalid`).
- Logging toggle: "Send debug messages to the Log Center" (`detail.logs.*`).

### The app pool

- First install: admin picks "Pool for app data": "Every app keeps its data on this pool. It cannot change while an app is installed." (`install.pool/poolHint`; `app.pool.fixed`, `app.pool.required`). Stored as setting `apps.pool` (`AppRegistry.cs:84`). No pool: "There is no pool that can hold apps. Create one in Storage first."
- Pool must be imported, online, writable, unlocked, prepared (`app.pool.unavailable` reasons: missing, offline, read_only, locked, not_prepared).
- Layout: dataset per app `<pool>/pithyx/apps/<id>` at `/<pool>/.pithyx/apps/<id>` (versions/, current, volumes/, run/); Docker data root `/<pool>/.pithyx/docker` (dataset `pithyx/docker`, no auto snapshots); nothing on system disk (`daemon/CLAUDE.md`; `ZfsPlans.cs CreateAppDataset/CreateDockerDataset`).
- Pool missing/locked: apps unavailable, "Pool not available" / "The pool with the app's data is not available. Nobody can open the app until it is back." (`installed.unavailable`, `detail.unavailable`).
- App uid 40000 to 49999 (`UidAllocator.cs:28`).

### Updates and rollback

- Settings (gear) > Updates (`store.json:settings.updates.*`): policy "When a catalog has a newer version": "Install it" (automatic), "Tell administrators" (notify, DEFAULT), "Do nothing" (none; only listed in tab Updates) (`StoreContracts.cs:6-16`, migration `0025:45-48`). Updates asking new permissions or special rights (or root newly) never install on their own (`StoreService.cs:1075-1077`).
- Daily check time default 03:00 (HH:MM, box time zone) plus a fixed random delay of 0 to 3600 s per box (`store.updates.delay`, `StoreService.cs:247-253`, `settings.updates.timeHint`); shows "Next check" and "last check"; "Check now" (`POST /api/store/check`).
- Per-app policy under app detail > Updates: "As set in the Store's settings" or its own (`detail.policy.*`, `PUT /api/apps/{id}/update-policy`).
- Notifications to administrators: "App updates" and "Catalog not fetched" (`notifications.json:store.*`). Automatic updates run as jobs admins see.
- Update flow: snapshot of app dataset before update (kind "Before an app update"), stop old backend, new starts; if new does not start the old comes back (CHANGELOG A14).
- Rollback: "Back to {version}" (only the one previous version): "{version} returns with the app's data as it was before the update. Changes since the update and snapshots of the app taken since are lost. If updates install on their own, this app announces them from now on." (`detail.rollback.*`; `app.app.no_previous`; `AppService.cs:437-494`).
- Channel: Stable ("Versions the catalogs' owners released.", default) / Beta ("Every new version as soon as a catalog publishes it, before its release.") (`settings.channel.*`, `StoreService.cs:187-188`). Beta = branch `catalog-next` (signed by `next` key), stable = branch `catalog` (D328/D330).
- Not in code (plan only): promoting; on 7484 the official catalog shows "Fetch failed" on stable until owner promotes (CLAUDE.md state). Box OS updates are separate (D297/D338, not in daemon docs scope).

### Catalogs and trust

- Catalogs: Official (on, `https://github.com/pithyx/apps-official.git`), Community (off, `.../apps-community.git`, switch on asks "Use the community catalog?" warning, `settings.community.*`), custom (added by admin). Official/community cannot be removed, only off (`store.catalog.built_in`).
- Add catalog: "Repository URL": "https://… for a public catalog, git@host:owner/repository.git for a private one." Accepted forms: `https://host/path`, scp-like `user@host:path`, `ssh://user@host[:port]/path`; no credentials in URL, no query/fragment, max 500 chars, host letters/digits/dots/hyphens (`d/Apps/Catalog/CatalogGit.cs:15-48`); `file://` only in simulation. Error `store.catalog.url_invalid` (reasons scheme/host/path). Duplicate => `store.catalog.exists`.
- Deploy key (private catalogs via SSH): the box generates an ed25519 key (`ssh-keygen -t ed25519`, `CatalogGit.cs:168`) and shows its public line; admin adds it to the repo as a read-only deploy key then "Fetch now" (`catalogs.deployKey*`). Server host key pinned at first connection (ssh-keyscan ed25519, shown "Server key pinned at the first connection: {key}"), GitHub keys built in; mismatch => `store.catalog.unreachable` reason host_key. Fetch timeout 3 min (`CatalogGit.cs:74`).
- Trust: official/community indexes carry an Ed25519 signature checked against keys built into the daemon: `pithyx-catalog-1` (stable and beta), `pithyx-next-1` (beta only) (`StoreService.cs:30-34`); index sequence must not go backwards (`store.catalog.sequence_old`), every file hash checked (`store.package.hash_mismatch`). Custom catalogs: unsigned, "Unverified": "Apps of your own catalogs are not checked by the Pithyx project. Add only catalogs you trust." (`catalogs.addWarning`).
- Fetch state labels: Off / Not fetched yet / Up to date / Fetch failed / Fetching; failed fetch keeps last verified copy (`catalogs.status.*`, `catalog.failedNotice`). Copies in `/var/lib/pithyx/catalogs/<id>/`.
- Reserved ids `org.pithyx.*` only from the official catalog (`store.catalog.id_reserved`).

### Backends and Docker

- App = static frontend served by daemon in sandboxed iframe + optional backend of containers (Compose subset, images by digest) (CLAUDE.md Apps). Install text: "Brings its own background service in containers." / "Runs in the browser only, without a background service." (`install.backend.*`).
- Docker (Debian `docker.io`) is owned by daemon: configured on the app pool, started only while a backend app is installed and pool mounted, otherwise disabled/stopped (`Apps/Docker/DockerHost.cs:7-14`). Userns remap to user `pithyx-docker`; nftables rule keeps containers from host services; own internal network per app without internet unless permission Internet (second network) (`DockerHost.cs`, CHANGELOG A14).
- Images come from `backend/images/<arch>/` in the package or the registry, id must equal the lock (`app.image.mismatch`, `app.image.unavailable`, `app.package.lock_invalid`). Compose rules: `app.compose.*` errors; v0.1 refuses docker socket / host rights: `app.elevated.unsupported` (`ComposeValidator.cs:1018`). Root container only if declared: warning "A container of this app runs as root. Pithyx keeps it without rights on the box itself..." (`install.root`).
- Lifecycle: `always` runs whenever possible; `onDemand` starts when a user opens the app or a request arrives (request waits up to 30 s), stops idleTimeout (default 10 min) after last window closed; failed start retries after 1 min (`BackendLifecycle.cs:46-49`, CHANGELOG A15).
- Logs: container output goes to Log Center source `app/<id>` (stdout info, stderr warning).
- Error `app.docker.unavailable`, `app.backend.start_failed`, `app.backend.unavailable`.
- Not in code: elevated rights (docker.socket, host.full) refused in v0.1; web apps with own port ("kind web") after SDK 1.0.

### Questions users will ask

- Where is app data? => on the app pool, under `/<pool>/.pithyx/apps/<id>`.
- Can users install apps? => no, admins only.
- Are catalog apps safe? => official signed; custom unsigned.
- Why does the official catalog fail on stable? => owner has not promoted yet (project state), see open questions.
- Can I move apps to another pool? => not while any app is installed.

---

## backup.md

### Creating a backup

- Settings > Backup and restore ("Save the configuration of this box and restore it"; `settings.json:pages.backup`), admin only (`d/Backup/BackupEndpoints.cs:14`). "Create a backup": choose Backup password + repeat; "Create and download" -> file `pithyx-config-<host>-<yyyyMMdd-HHmm>.pithyxbackup` (`ConfigBackupService.cs:471`). POST `/api/system/backup`.
- Password must satisfy the normal password rules (min 8, not common) (`ConfigBackupService.cs:68`). "Without this password the backup cannot be restored. Pithyx does not keep it, and nobody can recover it." (`backup.create.keepPassword`). "Keep it somewhere other than this box."
- Format: magic `PITHYXCB`, Argon2id (64 MiB, 3 passes) key, AES-256-GCM (`BackupArchive.cs:19-44`, `Config:PasswordHasher.cs:20-21`). Max archive 16 MB, content 256 MB (`BackupArchive.cs:31-34`, error `system.backup.too_large`).

### What it holds

- Intro: "users, groups and passwords, shared folders and their rights, file services, storage schedules, regional settings, certificates and SSH keys. It does not hold the files on your pools." (`backup.intro`).
- Categories (`backup.categories.*`, tables `Backup/BackupDatabase.cs:45-52`): Users and groups (accounts, groups, passwords also SMB/SFTP, 2FA, recovery key, personal settings, desktop layouts); Shared folders and file services (shares with rights, SMB/SFTP/WebDAV/FTP settings); Storage settings (snapshot schedules = restore points, scrub and self-test schedules); System settings (language, formats, time zone, time servers, log retention); Certificates and keys (FTPS cert, SSH host keys; only when present); Apps (registry, permissions, folder mappings, catalogs, app pool; "the apps' files and data stay on the pool").
- Also stored: Linux shadow hashes (`users/shadow`) and Samba passdb entries (`users/smbpasswd`) for managed users, time settings (`ConfigBackupService.cs:38-44,92-112`), schema version + Pithyx version + host name in manifest.
- NOT included (runtime, `BackupDatabase.cs:72-73`): sessions, jobs, notifications, storage alert notices/schedule runs, recycle-bin index (`trash_items`), network config and pending change (on purpose, D292), pools/data, app files, logs, Docker data.

### Restoring

- "Restore a backup": choose file + password, "Check backup" (nothing changes yet), shows "This backup holds": Created, From (host), Pithyx version, counts (users, groups, shared folders), parts; choose "What to restore" (checkboxes of categories present), "Restore…" and confirm "Restore now" (`backup.restore.*`, `backup.confirm.*`). Checked backup stays 15 minutes in `/run/pithyx/backup/` (tmpfs) (`ConfigBackupService.cs:47`, `daemon/CLAUDE.md`); `system.backup.restore_expired`.
- "Each part you choose replaces the current one completely. Files on your pools stay untouched. Pithyx restarts afterwards." (`backup.restore.warning`). Restoring users signs everybody out including you; sign in with an account/password from the backup; backup's recovery key returns (`confirm.signOut`; TODO.md line 1376).
- Safety copy: before restore database copied to `pithyx.db.pre-restore-<time>.bak` next to the DB (`ConfigBackupService.cs:~255`). Failure => nothing changed `system.backup.restore_failed`. Steps that can report problems: Linux accounts and groups, passwords for SFTP and SSH, passwords for SMB, time zone and servers, certificates, SSH host keys (`backup.steps.*`); "Some steps did not work; the Log Center has the details".
- Other errors: `system.backup.invalid`, `password_wrong`, `too_new` (newer Pithyx made it; update first), `category_missing`, `restore_running`.
- Restore on a fresh/reinstalled box: works from Settings after setup. Not in code: restore during first setup (follow-up, not in web installer D294).

### Limits

- Manual only; no automatic backups to a pool yet (TODO follow-up). Not a data backup. Network settings not restored (by design). Restoring onto different hardware: card names differ (why network excluded).
- Backup file holds password hashes and keys: protect it.

### Questions users will ask

- Does it back up my files? => no.
- Forgot backup password? => unrecoverable.
- Can I restore only users? => yes by part.
- Will restore change my network? => no.
- How often? => manual; no schedule.

---

## logs-and-monitoring.md

### The Log Center

- App "Log Center" (`org.pithyx.logs`, admin only; all endpoints admin `d/Logs/LogEndpoints.cs:29-94`). Filters: search ("Search messages or a request id"), Levels (Debug, Info, Warning, Error), Time range (Last hour, Last 24 hours, Last 7 days, Last 30 days, All time), Sources (All sources; "Pithyx daemon", "System · {name}", "App · {name}") (`logs.json`). Details pane: Time, Level, Source, User, Request id, Stored, "In the archive".
- Sources: `daemon`, `system/<service>` from journal, `app/<id>` (`LogSources.cs:6-8`). Journal followed by ONE `journalctl --follow --output=json` for the kernel and units smbd, nmbd, winbind, ssh, zfs-zed, zfs-import-\*, zfs-mount, zfs-share, systemd-networkd, NetworkManager, systemd-timesyncd, chrony, docker, containerd, smartmontools, systemd-logind (`JournalParser.cs:11-19,22-28`). SMB audit lines become file-service activity.
- Storage: `logs.db` on system disk `/var/log/pithyx/` (`Logs/LogStore.cs:13`), batches, flush every 1 s (`LogWriter.cs:15`), maintenance every 10 min. Works without any pool. Daemon JSON log files `/var/log/pithyx/daemon.json*` rolling 20 MB x 10 (`Hosting/LoggingSetup.cs:10-11,33`): the per-request log stays in files only (TODO line "Log Center"; request log not in logs.db per backlog note, unverified).
- Defaults: keep 30 days, max 4 GB; allowed 1 to 365 days, size 64 MB to 4 GB ("at most 4 GB. The oldest entries go first.") (`LogContracts.cs:52-57`, `logs.json:settings.*`). Settings via "Retention" button (PUT `/api/logs/settings`); invalid -> check `logs` errors.
- Archive on a pool: off by default; "Entries are copied to the pool once a day, and before the current logs drop any. This wakes the pool's disks about once a day." (`settings.archiveIntro`, `LogArchive.cs:16`: SyncInterval 1 day). Archive days 1 to 3650 (default 365), size 0.25 to 1024 GB (default 10 GB) (`LogContracts.cs:54-57`). Path `<pool>/.pithyx/system/logs/archive.db`. Pool choice "The first pool" or specific; unavailable pool => entries wait on system disk.
- Search: message LIKE, data fields LIKE, or exact request id (`LogDatabase.cs:146`). Page size 200 default, max 1000 (`LogService.cs:25-26`).
- Export: "Export as text" (`.log`) / "Export as JSON lines" (`.jsonl`), file `pithyx-logs-<yyyyMMdd-HHmmss>.<ext>`, max 1 000 000 entries (`LogEndpoints.cs:58-78`, `LogService.cs:31`).
- App logs: apps log through SDK to `POST /api/apps/{app}/logs`; message max 4000 chars, up to 20 data fields of 1000 chars, batch 100; rate limit burst 200, 20 per second per app (`LogService.cs:27-30`, `AppLogLimiter.cs:11-13`); info/warn/error always, debug only when admin switched it on in app detail. Backend container output in `app/<id>`.
- Secrets masked in logs (SecretMaskingEnricher). Request id is in the details only (UI shows it in Log Center details as "Request id": exception to "UI never shows request id": it is the Log Center).

### The Resource Monitor

- App "Resource Monitor" (`org.pithyx.monitor`, admin only; topics `system/resources` and `system/processes` administrators only, `Monitoring/ResourceMonitor.cs:14-15,44`). Views: Performance, Processes. Time range: Minute, 10 minutes, Hour (`monitor.json:range.*`). Sampled every 1 s only while someone watches; history ring 1 hour in 5 s buckets, in memory (`ResourceMonitor.cs:16`, `ResourceRing.cs:9,12`): "The history covers only the time this monitor was open somewhere: nothing is measured while nobody watches." (`monitor.json:history`).
- CPU: total and per core ("Core N"). Process list: "CPU is the share of all cores together."; Name, PID, User, CPU, Memory; filter by name or user (`monitor.json:processes.*`).
- Memory split: Programs, ZFS cache (ARC) ("gives memory back as soon as programs need it"), Page cache, Free; Swap (`memory.*`; `ResourceSampler.cs:92`: used = total - free - cache - arc).
- Network per interface (received/sent per second); Disks per disk (read, written, busy) "Read from the kernel's counters: a sleeping disk stays asleep."; Temperatures from sensors with high/critical marks; no sensors in VMs/some boards; "Disk temperatures are not read here, so sleeping disks stay asleep." (`monitor.json:temperatures.none`).
- Summary line: uptime, load 1/5/15 (`summary.*`). Info Center in Settings shows version, host name, OS, kernel, machine, CPU, logical processors, memory, uptime (`settings.json:info.*`; topic `system/info`, any user).

### Notifications

- Per-user list, stored in SQLite, topic `notifications`; max 200 per user, lifetime 30 days (`d/Notifications/NotificationService.cs:23,26`), title max 200, body max 1000, max 2 actions. Mark read, delete one/all (`NotificationEndpoints.cs:20-47`). Apps may post with permission `notifications` ("Shows notifications to the user who uses it.", `POST /api/notifications/app`).
- Sources in code (`notifications.json`): storage alerts for administrators (pool degraded/faulted/suspended, disk missing, data errors, scrub errors, 80 %/90 % full, SMART failing/wear, "Resolved: {problem}"), job results ("{kind}: done/failed/cancelled/interrupted"), store (App updates, Catalog not fetched), desktop (windows not restored, desktop loaded, layout not saved, note text kept), app messages.
- In desktop only (bell/notification list); no email, push or webhook code (grep in `Notifications/`: none). Not in code: email/push (TODO "Notifications" later; server-side rendered texts need decision `[DECISION]` TODO line 1384).
- Texts exist but no emitter found: `storage.scrub.finished`, `storage.disk.warm`.

### Support bundles

- NOT IMPLEMENTED: no code or UI (grep "support bundle" in daemon/desktop: none). TODO.md only says "there is no support bundle yet" (line 1244) and rules that keys, Samba passdb never in a bundle. CLAUDE.md principle: user shares a bundle manually; memory dumps only opt-in.
- What exists instead: Log Center export (text/JSONL) and `/var/log/pithyx/daemon.json*`.

### Questions users will ask

- How long are logs kept? => 30 days default, up to 365 days, max 4 GB; optional pool archive.
- Does the log archive wake my disks? => yes about once a day.
- Why is the monitor graph empty after opening? => history only while open.
- Why no disk temperature? => would wake disks; SMART page shows it when disk awake.
- Do I get email alerts? => no, desktop notifications only.
- Where do I send a support bundle? => no bundle feature yet.

---

## Open questions (code vs TODO vs CLAUDE.md)

1. `settings.json:nav` lists "Updates" and "About" but no such Settings pages exist (pages: info, network, regional, shares, fileServices, users, backup, groups). Is there an Updates page? D297/D338 plan updater; nothing in daemon.
2. CLAUDE.md "Network": HTTPS on 443 with trust wizard; code is HTTP 7484 + port 80 redirect only. Docs must say HTTPS not yet (plan M5). Basic auth for WebDAV and session cookie travel in clear on LAN.
3. Pool encryption advertised in CLAUDE.md ("Pool encryption: ZFS native") but wizard refuses `key` (`storage.encryption.unavailable`); TODO step 9 moved after v0.2. Error text says "next update".
4. Spindown (CLAUDE.md Operations: "Spindown is a per-pool option") not in code.
5. SSH MOTD with ASCII logo and warning (CLAUDE.md "Users and security") not found in image or daemon.
6. Support bundles (CLAUDE.md Principles, Tracing and logging) not implemented; docs page "Support bundles" heading has nothing but plan.
7. NFS in CLAUDE.md "Protocol access (SMB, NFS, SFTP, WebDAV)": NFS not implemented; grants list SMB/SFTP/WebDAV/FTP (FTP is not in CLAUDE.md list).
8. Recycle bin retention can only be changed via API (`PUT /api/shares/{id}/trash`), no UI; TODO line 955 says "set by administrators".
9. `app.backend.unsupported` error text says "installs apps without a backend only" but constant unused (stale i18n); backends do install since A14. Reference page generated from errors.json will show wrong text.
10. Notification texts `storage.scrub.finished` and `storage.disk.warm` have no emitter in code found.
11. `storage.json` `wizard.layouts.single.fixed` promises "Can be turned into a mirror later by attaching a second disk" and plan `ZfsPlans` has Attach, but I found no UI button/key for attach; verify before documenting.
12. Console menu: `console.sh` says "Alt+F2: log in" yet CLAUDE.md says live/installed system have no login (root locked, SSH off). What can be done on tty2?
13. `change.expand.explain` text was cut in my read (line 497 of storage.json); check full wording before quoting.
14. Volumes dataset path `<pool>/pithyx/volumes`: TODO line 1079 says so; `ZfsPlans.Datasets` does not pre-create it (created on demand?). Verify in `ZfsDatasetPlans`.
15. SMB default ON in fresh DB (`0017:12`) and new users get SMB right; CLAUDE.md/TODO say file services "off by default" only for WebDAV/FTP/SSH. Confirm SMB on at first boot is intended (smbd starts "once a share needs it" per customize.sh).
16. Who may restore from the recycle bin: write access to share? not checked (`FileOperations.StartRestoreAsync`).
17. Store `StoreService.ProjectKeys` and official catalog on stable show "Fetch failed" until owner promotes (CLAUDE.md Current state) - docs should avoid claiming the official catalog works on stable yet.
18. Firewall: no ruleset for 21/50000-50099/445 found; unknown whether the image ships nftables rules beyond Docker's. Docs on ports should not claim a firewall.
19. Regional "Time servers" empty = timesyncd defaults (Debian pool) - an outbound connection not mentioned in CLAUDE.md "100 percent local"; confirm wording for "What leaves the box".
