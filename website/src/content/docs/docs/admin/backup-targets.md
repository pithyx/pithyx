---
title: 'Backup targets'
description: 'Set up servers reached over SSH as targets for backups in the Backup app, with their own key and a confirmed host key.'
status: ready
audience: admin
sidebar:
  order: 11
---

The **Backup** app, for administrators, sends copies of your files to other machines. Its first part is the **targets**: the servers backups go to. A target is any server you can sign in to over SSH with SFTP, for example a storage box you rent or another Linux server. [Box backups](/docs/admin/backup-jobs/) send folders to a target on a schedule.

## Adding a target

1. Open **Backup** from the Pithyx menu and choose **Add target**.
2. Enter a name, the server (host name or IP address), the SSH port (22 when left empty; some storage providers use 23), the user and the folder on the server. The folder is relative to the user's login folder, or starts with `/`; leave it empty for the login folder.
3. Choose **Next**. The box reads the host keys the server offers and shows their fingerprints, Ed25519 first.
4. Compare the fingerprint with the one your provider or the server's administrator shows you. On the server itself, `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` prints it. Choose **Fingerprint matches, save** only if it is the same.
5. The box makes the target's own SSH key and shows its public key. Add that line to the file `.ssh/authorized_keys` of the user on the server, then choose **Test connection**.

The box stores no password for a target: it signs in only with the target's key. Each target has a key of its own, so removing one line from a server's `authorized_keys` locks out exactly one target.

## Testing the connection

**Test connection** signs in with the target's key, creates the folder if it is missing, writes a test file and removes it. The card shows the outcome as **Connected**, **Connection failed** or **Not tested**, with the time of the last test. A failed test names the reason:

- [`backup.target.unreachable`](/docs/reference/errors/#backup.target.unreachable): the name is unknown, the port is closed, there is no answer or no route.
- [`backup.target.auth_failed`](/docs/reference/errors/#backup.target.auth_failed): the server refused the key. Check that the public key is in the user's `authorized_keys` and that the user name is right.
- [`backup.target.not_writable`](/docs/reference/errors/#backup.target.not_writable): the box signed in, but the folder could not be made or no file could be written there.
- [`backup.target.host_key_changed`](/docs/reference/errors/#backup.target.host_key_changed): see below.

Changing the server, port, user, folder or host key clears the last test.

## When the host key changes

The box connects only to the server whose host key you confirmed. If the server answers with another key, every connection stops with `backup.target.host_key_changed`. When a backup's run or check meets it, the administrators get the event **Backup target's host key changed**. That is expected after the server was reinstalled or moved to other hardware; otherwise someone may be posing as it. **Check new host key** on the target's card shows the confirmed fingerprint next to the ones the server offers now. Accept the new one only after you compared it with the fingerprint your provider shows.

Changing the server or the port of a target also asks you to confirm the new server's host key.

## Deleting a target

**Delete** removes the target and its key from the box. A target that box backups still use cannot be deleted ([`backup.target.in_use`](/docs/reference/errors/#backup.target.in_use)); delete those backups first. Remove its public key from the server's `authorized_keys` as well; backups already on the server stay there.

## Configuration backup

Targets, their keys and their confirmed host keys are in the part **Backup app** of the [configuration backup](/docs/admin/backup/). The private keys never go into a [support bundle](/docs/admin/logs-and-monitoring/).
