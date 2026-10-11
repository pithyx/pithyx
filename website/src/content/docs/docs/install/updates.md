---
title: 'System updates'
description: 'Update channels, how an update is checked and applied, and what happens when one fails.'
status: ready
audience: admin
sidebar:
  order: 5
---

Pithyx updates itself from signed update files: once a day it checks whether its channel has a newer version and notifies the administrators, but it never installs by itself. Before every update the box takes a snapshot of the system, and after the restart it checks the new version for up to five minutes; if that check fails, it goes back to the snapshot on its own. Updates are in Settings > **Updates** (administrators only). Pools and their files are never touched by an update or a rollback.

Updates apply only to a box installed with the Pithyx installer. On a machine laid out differently, Settings > **Updates** can check for updates but not install them or roll back ([`update.system.unsupported`](/docs/reference/errors/#update.system.unsupported)).

## Channels

Under **Settings** on the Updates page you choose the **Channel**:

- **Stable** gets releases only.
- **Beta** gets new versions earlier, for testing. A beta box also installs stable versions.

**Check for updates daily** is on by default. The check reads the channel's small `channel.json` from the update source and Debian's package lists, then notifies the administrators ("Pithyx {version} is available", and a separate notice when Debian has security fixes). Each box picks its own random time of day for the check; **Next check** shows it. **Check now** runs it at once. Switching the daily check off stops the automatic check, not **Check now**.

By default the source is the Pithyx project's releases on GitHub. Only the recovery menu at the console can switch to another source, for example a fork; see [Switching the update source](/docs/install/update-source/).

## Checking and installing

When the channel has a newer version, the page shows "Pithyx {version} is available" with **What changes** (the changelog) and the number of Debian updates. **Install {version}** opens a dialog:

1. The box takes a snapshot of the system, so you can always go back to the current version.
2. It installs the update and, if you tick it, the Debian updates in the same snapshot.
3. It writes the boot loader to every system disk and restarts. The box is not reachable during the restart.
4. After the restart it checks the new version and either keeps it or goes back to the snapshot.

Confirm with **Install and restart**. The progress shows the steps Download, Snapshot, Install, Debian updates, Boot loader and Restart and check; the page reconnects by itself after the restart and the **History** shows the result.

What an update file carries:

- The Pithyx packages, the kernel and the ZFS module. The kernel and ZFS change only together through a Pithyx update, never from Debian alone.
- Every other package of the image that changed since the file's minimum version. One file updates every box from that minimum version on, without the versions in between.
- Debian security updates are not part of the file. The box gets them from Debian; the page counts them and the install dialog offers them.

**Without internet**, use **Install from a file**: **Upload update file** takes a `.pithyxupdate` file of at most 4 GB. The box checks the whole file before anything changes, then shows the version, channel, number of packages and kernel. Install it with **Install {version}**, or **Discard** it. The file is kept until another upload or a restart of the daemon. The recovery menu can also install a file from a USB stick (below).

Rules every update follows:

- A file older than or equal to the installed version is refused ([`update.file.older`](/docs/reference/errors/#update.file.older)). An older version comes back only through a rollback.
- A version that needs a newer minimum than the installed one says "This version needs at least Pithyx {minimum}". Install a version in between from an update file first ([`update.file.minimum`](/docs/reference/errors/#update.file.minimum)).
- A stable box installs only stable files ([`update.file.channel`](/docs/reference/errors/#update.file.channel)).
- The system disk must have enough free space ([`update.space.insufficient`](/docs/reference/errors/#update.space.insufficient)).
- Only one update or rollback runs at a time ([`update.apply.running`](/docs/reference/errors/#update.apply.running)).

## Signatures

Every update file and every `channel.json` is signed with Ed25519. The box checks:

- the signature of the channel file and of the update file's manifest, against the public keys built into Pithyx: the release key is trusted on both channels, the beta key only on beta;
- the size and SHA-256 of every package in the file, and that the file holds nothing its manifest does not name;
- that a channel file is not older than one it saw before, so an old `channel.json` cannot be replayed ([`update.channel.sequence_old`](/docs/reference/errors/#update.channel.sequence_old));
- that a downloaded file is exactly the one the channel names ([`update.download.mismatch`](/docs/reference/errors/#update.download.mismatch)).

A file or channel with a missing, damaged or untrusted signature is refused and nothing changes ([`update.file.signature_invalid`](/docs/reference/errors/#update.file.signature_invalid), [`update.channel.signature_invalid`](/docs/reference/errors/#update.channel.signature_invalid)). Unsigned files are development files that only a development machine accepts.

## When an update fails

- **Before the snapshot** (download, checks, disk space): nothing changed. The history says "Failed, nothing changed".
- **After the snapshot**: the box goes back to the snapshot by itself. After the restart a health check waits up to five minutes for the daemon, its database migrations, the API, the desktop and the pools that were imported before. If one of them fails, the box restores the snapshot and restarts. If the new system does not even get that far, the next start falls back to the previous version through the boot menu. Either way the administrators get "Update to {version} failed", and the history says "Failed, previous version restored".

The error [`update.apply.failed`](/docs/reference/errors/#update.apply.failed) names the step and command that failed. The details are in the Log Center.

## Snapshots and rollback

A snapshot of the system is taken before every update; the last three are kept. **Snapshots** on the Updates page lists them, and **Back to this version** restores one: type the version to confirm and choose **Go back and restart**. Settings changed since the snapshot are lost (users, shared folders, app settings). Pools and their files stay as they are.

The boot menu has an entry for every kept snapshot (keys 1 to 3, newest first). It starts that snapshot read-only into the recovery menu, which offers the rollback to it.

## The recovery menu

The boot menu entry **Pithyx recovery** (key `r`) starts a menu on the box's screen:

- **[1] Roll back to a snapshot**, confirmed by typing the version.
- **[2] Install an update file from a USB stick**: the file must lie in the stick's top folder. The menu checks it like an upload and installs after you type `install`.
- **[3] Reset the network to DHCP**: every network card back to DHCP, bonds, VLANs and name servers removed, applied at the next start.
- **[5] Change the update source**, see [Switching the update source](/docs/install/update-source/).
- **[4] Restart**.

The menu has no password. Whoever can use the box's console, in person or through remote management (BMC, IPMI), controls the box anyway; see [Security](/docs/admin/security/#physical-access). Secure the console and any remote management of the machine. Every action of the menu is logged.
