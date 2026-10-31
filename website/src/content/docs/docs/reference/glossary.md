---
title: 'Glossary'
description: 'The words Pithyx uses and what each one means.'
status: ready
sidebar:
  order: 4
---

The words Pithyx uses in its interface and documentation, in alphabetical order.

## Terms

**Administrator.** A user with every right on the box. Administrators manage storage, users, shared folders, file services and apps.

**App.** A static frontend plus an optional backend, described by a manifest and shipped as a package. See [Apps overview](/docs/developers/apps/overview/).

**App id.** The reversed domain of the author plus the app name, three to five parts, such as `com.mueller.chat`. System apps use `org.pithyx.<name>`.

**App pool.** The pool an administrator picks at the first install of an app. Each app has a dataset there, and Docker's data lives there too.

**Backend.** The containers of an app, described by a Docker Compose file. One instance serves all users and is reached only through the daemon's proxy.

**Box.** The machine that runs Pithyx.

**Catalog.** A Git repository of apps that the Store reads. There is an official one, a community one and any number of own ones.

**Channel.** `stable` or `beta`: which branch of a catalog the box reads.

**Configuration backup.** A password-encrypted file with the box's database, Samba password database, certificates and SSH host keys, which can be restored on a fresh install.

**Core app.** An app that ships with the system, such as Files, Settings, Resource Monitor, Log Center, Viewer, About Pithyx and Store. Its id starts with `org.pithyx.`.

**Daemon.** `pithyxd`, the only privileged process on the box. It serves the desktop and the API.

**Dataset.** A ZFS file system inside a pool with its own space limit and settings. Each shared folder and each app has one.

**Deploy key.** A read-only SSH key the box creates so it can fetch a private catalog.

**Desktop.** The web interface of the box, opened in a browser.

**Disk serial confirmation.** Destructive storage actions on a disk that holds data need the disk's serial number to be typed as confirmation.

**Error code.** A stable identifier of the form `area.object.reason`. See the [error reference](/docs/reference/errors/).

**Group.** A set of users that can be given the same rights. The built-in group `users` contains everyone.

**Hot spare.** A disk that waits in a pool and takes over when another disk fails.

**Job.** A long operation that runs in the background with progress. Jobs are not resumed after a restart.

**Key vault.** A folder on the system disk, readable by root only, where the box keeps the keys of encrypted pools it unlocks at every start. It protects against stolen disks, not a stolen box, and is never part of a configuration backup. See [Encryption](/docs/admin/storage/#encryption).

**Lifecycle.** How long a backend runs: `always`, or `onDemand` (while a window is open, plus an idle time).

**Live system.** The environment on the installation USB stick that runs the web installer.

**Log Center.** The app that searches the logs of the daemon, the system and the apps.

**Manifest.** The file `pithyx.json` that describes an app. See [The manifest](/docs/developers/apps/manifest/).

**Owner account.** The first administrator account, created in the installer.

**Package.** A `.pithyxapp` file: an app, built and ready to install.

**Permission.** Something an app may do, such as read shared folders or reach the internet. An administrator confirms and can revoke each one. Not to be confused with a user's rights.

**Pool.** A ZFS pool: disks combined into one storage area, as a mirror, RAID-Z or stripe.

**Pool key.** The key of an encrypted pool (it starts with `PXPK`), shown once when the pool is created or its key changes. Without it the pool's data cannot be read. See [Encryption](/docs/admin/storage/#encryption).

**Protocol.** A way to reach shared folders from other devices: SMB, SFTP, WebDAV or FTP. Access is set per user.

**Quota.** A limit on the space a dataset may use.

**Recovery codes.** Ten one-time codes that replace the authenticator app for two-factor sign-in.

**Recovery key.** A key shown once at setup (it starts with `PTHX`). It is the only way to reset an administrator's password when no other administrator can; without it, reinstall.

**Recycle bin.** A hidden folder in each shared folder that keeps deleted files for a set time.

**Request id.** A 32-character identifier that ties one request to its log entries. The desktop never shows it.

**Resilver.** Rebuilding data onto a replaced or returned disk.

**Restore point.** An automatic daily snapshot of a shared folder, kept for 7 days.

**Rollback.** Returning an app to its previous version together with its data.

**Sandbox.** The restricted iframe in which third-party apps run.

**Scrub.** A check that reads every block of a pool and repairs errors from redundancy.

**SDK.** `@pithyx/sdk`, the API between an app and the desktop. See [@pithyx/sdk](/docs/sdk/sdk/).

**Shared folder.** A folder on a pool, its own dataset, that users reach with chosen access (none, read or read-write) from the desktop or over a protocol.

**Sideload.** Installing an app from a package file instead of a catalog. The Store marks it as not verified.

**Simulation mode.** The daemon running on fixtures instead of real hardware, for development and tests.

**Snapshot.** A read-only point-in-time copy of a dataset.

**Store.** The app where administrators install, update and remove apps and manage catalogs.

**System disk.** The disk that holds the operating system and the database. Pools and app data are never on it.

**Token.** A short-lived secret that identifies an app window to the daemon or to a backend. Tickets are one-time tokens for WebSockets.

**Topic.** A named channel on the WebSocket that delivers live data, such as `storage/pools`.

**Two-factor sign-in.** Sign-in with a password and a code from an authenticator app (TOTP). Optional for each user.

**Update policy.** How the box handles app updates: automatic, notify or none.

**USB key.** A USB stick or memory card holding the key file of an encrypted pool (`pithyx-keys/<pool GUID>.key`). While it is plugged in, the box unlocks the pool; it can be pulled afterwards, and the pool stays unlocked until the next start. See [USB stick](/docs/admin/storage/#usb-stick).

**Vdev.** A group of disks inside a pool, such as one mirror.

**Window lease.** The counting of open windows of an app that keeps an `onDemand` backend running.
