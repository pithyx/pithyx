---
title: 'Encrypted pools: when something goes wrong'
description: 'What to do when an encrypted pool stays locked, a mount fails, the key copy is lost, the system disk is gone, or the box itself is broken.'
status: ready
audience: admin
since: '1.2.0'
sidebar:
  order: 4
---

An encrypted pool can only be read with its key ([Encryption](/docs/admin/storage/#encryption)). This page lists what can go wrong and how to get the data back. Every way back needs the key: from the key vault while the box runs, or from the copy you stored when the pool was created or its key changed. Without either, nobody can read the data, Pithyx included.

## The pool stays locked after a start

The pool card shows **Locked**, and administrators get the notification "Pool … is locked" or "Pool … could not be unlocked".

- **Unlocked by hand after every start**: this is expected. Choose **Unlock…** and type the key.
- **Key in the key vault, but it did not unlock the pool**: the vault file is damaged or holds an older key (for example after the key was changed on another box). Nothing on the pool was changed. Choose **Unlock…**, type the key from your copy and keep **Keep the key in the key vault and unlock the pool automatically at every start** ticked: the vault gets the right key, and the pool unlocks by itself again from the next start on.
- **A key that is refused** with [`storage.encryption.key_invalid`](/docs/reference/errors/#storage.encryption.key_invalid) has a typo: the last characters of a key check the others. Dashes, spaces, upper or lower case and O for 0 or I and L for 1 do not matter. [`storage.encryption.key_wrong`](/docs/reference/errors/#storage.encryption.key_wrong) means the key is valid but belongs to another pool or is an older key of this one.

## Unlocking worked, but a folder is missing

When the key opened the pool but a dataset could not be mounted, the dialog says so and the error names the dataset ([`storage.command.failed`](/docs/reference/errors/#storage.command.failed)); the log has ZFS's message. The pool is unlocked anyway. Press **Unlock** again to mount what is missing. If ZFS hangs ([`storage.command.timed_out`](/docs/reference/errors/#storage.command.timed_out)), a disk of the pool is probably not answering: check the disks in Storage first.

Datasets that would hide a system folder are never mounted, as with an [import](/docs/admin/storage/#importing-and-exporting).

## The stored copy of the key is lost

While the key is in the key vault and the box runs, choose **Show key…** on the pool card. It asks for your password and, with two-factor sign-in on, a code from your authenticator app, and shows the key with **Download** and **Print**. Store it again. Every time a key is shown, the log records who saw it (never the key itself).

Better still, give the pool a new key: **Change key…** makes one, shows it, and changes the key only after you typed two of its groups from the copy you stored.

If the key is not in the vault and the copy is lost, the data cannot be read again. Copy everything you need off the pool while it is still unlocked, then create it again.

## The key vault cannot be written

When the system disk is full or broken, storing a key in the vault fails with [`storage.encryption.vault_failed`](/docs/reference/errors/#storage.encryption.vault_failed). The pool itself is fine and stays unlocked; after the next start it waits for the key to be typed. Fix the system disk, then unlock the pool or change its key again with the vault ticked. A key the vault could not take is never left half written, and an older key there is removed so it cannot fail at the next start.

## The system disk was replaced or Pithyx reinstalled

The key vault lives on the system disk and is never part of a [configuration backup](/docs/admin/backup/). After a reinstall, [import](/docs/admin/storage/#importing-and-exporting) the pool; it stays locked. Choose **Unlock…**, type the key from your copy and tick the vault option if the box should unlock it at every start again.

## Opening the pool without Pithyx

If the box is broken and Pithyx cannot run, the disks still open on any Linux with OpenZFS 2.x. The key sheet you downloaded or printed carries the key a second time as 64 hex characters, together with these commands. Connect all disks of the pool and run, as root:

```sh
zpool import -f -N tank
zfs load-key tank/pithyx
zfs mount -a
```

Replace `tank` with the pool's name. `zfs load-key` asks for the key: paste the hex form. `-f` is needed because the pool was last used by another system; `-N` imports without mounting, so you can check the mount points first with `zfs list -o name,mountpoint`.

Only the hex form works there; ZFS does not know the `PXPK-` form. If you only have that form, a box with Pithyx converts it: **Unlock…** takes it as it is.

## Things that do not help

- Pithyx has no master key, no recovery key for pools and no way around a lost key. The recovery key from setup resets the administrator's password, not a pool key.
- A configuration backup never holds pool keys.
- Destroying a pool also deletes its key from the vault.
