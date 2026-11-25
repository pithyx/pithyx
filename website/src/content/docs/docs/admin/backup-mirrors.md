---
title: 'Mirrors'
description: 'Keep one plain, current copy of shared folders and homes on a backup target with rsync over SSH, without versions, and what that means for deleted and damaged files.'
status: draft
audience: admin
since: '1.4.0'
sidebar:
  order: 11
---

A **mirror** is a box backup of the kind **Mirror** in the **Backup** app. It keeps one plain copy of folders of shared folders and homes in a folder on a [backup target](/docs/admin/backup-targets/) and brings it up to date on a schedule with [rsync](https://rsync.samba.org/) over SSH. The files on the target are ordinary files you can open there directly, without the box and without a key. Mirrors are listed under **Box backups** next to the versioned backups, with the kind **Mirror** below their name, and their runs are in [Activity](/docs/admin/backup-activity/) and on the [Overview](/docs/admin/backup-overview/).

:::caution[A mirror is not a versioned backup]
A mirror keeps no versions and is not encrypted. A file that is changed, damaged or encrypted by malware on the box is changed on the target as well at the next run, and with **Delete on the target what was deleted here** a file deleted by mistake is gone from the copy too. Use a mirror next to a [versioned box backup](/docs/admin/backup-jobs/), not instead of one.
:::

## What the target needs

A mirror needs rsync on the target and a user who may run it over SSH. The target's SFTP folder alone is not enough: when the target lets the box in for SFTP only, or has no rsync, adding the mirror fails with [`backup.target.rsync_missing`](/docs/reference/errors/#backup.target.rsync_missing).

- A Linux server with rsync installed works with the user of the target.
- On a second Pithyx box, use a user with the right **Sign in over SSH with a shell**, and switch on **SSH logins** under Settings > **File services** there. A user with SFTP only cannot run rsync.
- Storage services that offer rsync over SSH work as well; some offer it on another port than SFTP, for example 23. Set that port on the target.

## Adding a mirror

1. Add a [target](/docs/admin/backup-targets/) first and test its connection.
2. Open **Backup**, choose **Box backups** and **Add box backup**.
3. Enter a name and choose **Mirror** under **Kind**.
4. Choose the target. **Folder on the target** is the folder below the target's folder that holds the copy; it follows the name until you change it. The box creates it when it is missing. A copy that is already there, for example from before a reinstall, is brought up to date by the first run. Two backups of the box cannot use the same folder on one target ([`backup.repository.exists`](/docs/reference/errors/#backup.repository.exists)).
5. Under **Folders to back up**, choose shared folders or homes and folders inside them, as for a [box backup](/docs/admin/backup-jobs/#adding-a-box-backup).
6. Choose whether to **Delete on the target what was deleted here**. Off, the default, keeps files on the target that are gone on the box.
7. Choose when it **Mirrors**, and under **More settings** the upload limit, the overdue time and the excludes (rsync patterns, for example `*.tmp` or `cache/`).
8. Choose **Create**. The box makes the folder over SFTP, writes and removes a test file and lets rsync list the folder; then it saves the mirror. A mirror has no key.

**Edit** changes everything except the kind, the target and the folder on the target; for others, add a new mirror. A mirror has no **Versions to keep**, no integrity check and no download limit, and the daemon refuses them ([`backup.job.invalid`](/docs/reference/errors/#backup.job.invalid)).

## What a run does

A run first takes a ZFS snapshot of each source, as a box backup does, so the copy shows one moment. Then rsync sends the folders into the mirror's folder on the target, each below the name of its shared folder or home: the folder `Photos/2026` of the shared folder Media lands in `<folder>/Media/Photos/2026`, a home in `<folder>/homes/<user>`. rsync sends only changed files and only their changed parts, and keeps modification times, symbolic links and hard links. Owners, permissions and ACLs are not kept: the files belong to the target's user, who can always read and change them.

The run's **Size** is the size of the copy, and the added data is what rsync transferred. A file the box could not read, or that vanished during the run, makes the run **Backed up with warnings** and is listed in its details. A target that is full or refuses to write fails the run with [`backup.target.not_writable`](/docs/reference/errors/#backup.target.not_writable); a target that is unreachable or refuses the key fails it as for every target.

## Restoring from a mirror

The copy is plain files on the target, so you get them back with any SFTP client, with rsync, or over the target's own file services, for example SMB on a second box. A mirror has no **Versions** tab, no **Key** and no **Check now**, and does not appear in [Restore](/docs/admin/backup-restore/) ([`backup.job.unsupported`](/docs/reference/errors/#backup.job.unsupported)).

## Deleting a mirror

**Delete** removes the mirror and its runs from the box. The copy stays on the target.
