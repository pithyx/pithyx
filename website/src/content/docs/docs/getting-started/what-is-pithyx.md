---
title: 'What is Pithyx'
description: 'Pithyx in a few minutes: a NAS and server OS with a web desktop, running only on your own hardware.'
status: ready
audience: user
sidebar:
  order: 1
---

Pithyx is a free, self-hosted operating system for a NAS or home server. You install it on your own computer, and from then on you manage storage, users, file sharing and apps in a web desktop that you open in any browser. Everything runs on your box: there is no cloud account, no telemetry and no service run by the project that your box depends on.

:::caution
Pithyx 1.0 is marked beta: we consider it stable, but it has not yet proven itself on real boxes in everyday use. Treat it as beta and keep a copy of data you care about on another device.
:::

## What you get

- **A web desktop.** Windows, a taskbar, desktop shortcuts and notes, in a browser on a PC, tablet or phone. See [The desktop](/docs/user/desktop/).
- **Storage with ZFS.** You build pools from your disks in Settings and create shared folders on them. New pools use compression and can be encrypted with a key. See [Storage](/docs/admin/storage/).
- **File sharing.** Shared folders are available over SMB, SFTP, WebDAV and FTP. SMB is on by default; the others you switch on in Settings. See [Access files from other devices](/docs/user/file-access/).
- **Users and groups.** Every user is a real account on the box. Rights are given to users and groups; a user gets the union of all of them. Administrators can do everything. See [Users and groups](/docs/admin/users-and-groups/).
- **Apps.** Apps open in windows on the desktop. An app is a frontend with an optional backend in containers. Administrators install apps from a file or from a catalog in the Store; the official and the community catalog are on by default. See [Using apps](/docs/user/apps/).
- **Backup of the configuration.** Settings, users and shared folder definitions can be saved to an encrypted file and restored after a reinstall. See [Backup and restore](/docs/admin/backup/).
- **Network settings in the browser**, including bonds and VLANs, with a confirmation timer that rolls back a change that cuts you off. See [Network](/docs/admin/network/).

The core apps are Files, Viewer, Settings, Resource Monitor, Log Center, About Pithyx and the Store.

## How it is built

- The box runs Debian 13. The Pithyx daemon (C# on .NET 10) is the only privileged process and serves the web desktop and the API.
- The desktop is a React web application. Core apps and third-party apps use the same SDK.
- The system disk holds the system on btrfs, in a RAID 1 of one or two disks. Your data lives on ZFS pools on other disks.
- Settings are kept in a small SQLite database on the system disk, so the system disk can be rebuilt from a configuration backup.
- Pithyx is licensed under AGPL-3.0. The npm packages for app developers are MIT.

For details see [Architecture](/docs/developers/daemon/architecture/).

## What Pithyx is not

- It is not for data you cannot afford to lose yet (see the warning above).
- It is a fresh-install system. There is no import from other NAS systems.
- The installer does not restore a backup or import pools. Both happen after the first sign-in.
- It does not offer NFS.
- It needs a 64-bit x86 computer with UEFI and Secure Boot switched off. There is no installer for 32-bit machines, legacy BIOS or Raspberry Pi. See [Hardware requirements](/docs/getting-started/requirements/).
- It supports wired networks only. Wi-Fi is not supported.
- It does not announce itself on the network, so you find the box by its address.

## Where to go next

- [Hardware requirements](/docs/getting-started/requirements/)
- [Quick start](/docs/getting-started/quick-start/)
- [The web installer](/docs/install/installer/)
- [The desktop](/docs/user/desktop/)
- [Frequently asked questions](/docs/faq/)
