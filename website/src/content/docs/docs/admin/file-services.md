---
title: 'File services'
description: 'Switch SMB, SFTP, WebDAV and FTP on or off, with their ports, defaults and limits.'
status: draft
audience: admin
sidebar:
  order: 7
---

File services let other computers open shared folders over the network. Settings > **File services** has one switch per service: SMB, SFTP, WebDAV and FTP. Users sign in with their Pithyx user name and password. Pithyx has no NFS and no guest access. In a fresh installation SMB is on and the other services are off.

Which user may use which service is set per user or group (see [Permissions](/docs/admin/permissions/)); which folders a service offers is set per folder ([Shared folders](/docs/admin/shared-folders/)). A user whose password Pithyx does not know yet for SMB and SFTP has to sign in once on the web, or an administrator sets a new password.

## SMB

Switch **Offer shared folders over SMB**; the default workgroup is `WORKGROUP` (1 to 15 characters: letters, digits, dot, dash, underscore, starting with a letter or digit). SMB serves network drives to Windows, macOS and Linux on port 445. Only encrypted, signed connections of current systems (SMB 3) are accepted; guests are refused and NetBIOS is off. Pithyx writes `/etc/samba/smb.conf` itself on every change; put your own Samba settings into `/etc/samba/pithyx.local.conf`, which Pithyx includes and never writes. The file is checked with `testparm` before Samba reloads.

Connect from Windows File Explorer with `\\<address>\<share>`, from macOS or Linux with `smb://<address>`. The box does not announce itself on the network (no WS-Discovery, no mDNS), so clients connect by address. Deletes go to the recycle bin, and the daily restore points show as "Previous versions" in Windows.

## SFTP

Switch **Offer shared folders over SFTP**. It is encrypted file transfer over SSH, for example with FileZilla, WinSCP or the `sftp` command: `sftp://<address>`. Users see only their shared folders and get no shell. It uses the system's `sshd` through a drop-in file, with password sign-in only. Switching SFTP off only stops letting Pithyx users in; it does not stop SSH for other accounts. Turning it on runs `systemctl enable --now ssh.service`. The sshd configuration is checked with `sshd -t` and reverted if refused. SSH with a shell is a separate switch, see [Security](/docs/admin/security/#ssh).

## WebDAV

Served by the daemon itself at `/webdav/` on the box's web address (`http://<box-address>:7484/webdav/`), with Basic authentication using the Pithyx name and password. The root lists the shared folders the user may use; deletes go to the recycle bin. A user needs the WebDAV right and the folder switch **Offer over WebDAV**.

:::caution[No encryption yet]
The box serves plain HTTP today, so the WebDAV password travels unencrypted. Use WebDAV only on a network you trust. See [Security](/docs/admin/security/#https-and-certificates).
:::

## FTP

For devices and programs that know neither SFTP nor WebDAV, such as scanners. **Offer shared folders over FTP with TLS** uses explicit FTPS (`AUTH TLS`); the box makes a self-signed certificate and the page shows its SHA-256 fingerprint. **Also allow plain FTP without encryption** is off by default and carries a warning; switching it off again ends plain sessions.

- Control port 21, passive data ports 50000 to 50099. Only passive mode works (`PORT` and `EPRT` are answered with "502 Only passive mode").
- Idle timeout 10 minutes, data timeout 30 seconds.
- Failed sign-ins are limited to 5 per user and 30 per address in 5 minutes.

## Limits

- SMB 3 only; older clients cannot connect.
- SFTP accepts passwords only.
- Rights cannot be changed from a client (SMB ACL edits are ignored).
- Protocol sign-ins do not use two-factor sign-in.
- The box is not announced on the network (no WS-Discovery, no mDNS).
