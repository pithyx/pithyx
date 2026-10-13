---
title: 'Hardware requirements'
description: 'The hardware Pithyx needs, the reference machine and what is not supported.'
status: ready
audience: admin
sidebar:
  order: 2
---

Pithyx installs on a 64-bit x86 computer with UEFI firmware, at least 4 GB of memory and an internal system disk of at least 40 GB. The installer checks all of this before it lets you begin. Your data goes on additional disks, which you turn into pools after the installation.

## Supported platforms

The installer checks five things on its first page and refuses to start if one fails:

| Check       | Requirement                                                                                            |
| ----------- | ------------------------------------------------------------------------------------------------------ |
| Processor   | 64-bit x86 with x86-64-v2 (needs among others SSE4.2 and POPCNT)                                       |
| Firmware    | Started in UEFI mode. Legacy BIOS and CSM mode do not work.                                            |
| Memory      | At least 4 GB (the check allows about 400 MB of tolerance for memory the firmware reserves)            |
| Secure Boot | Switched off. The ZFS module is not signed. See [Firmware and boot](/docs/install/firmware-and-boot/). |
| System disk | At least one suitable internal disk of 40 GB or more                                                   |

The base system is Debian 13. There is no 32-bit version.

## Reference hardware

The reference machine is an Intel N100 or N305 with 8 GB of memory. The daemon is held to below 200 MB of memory and below 1 percent of CPU when idle. These numbers are budgets for the project's own tests, not requirements. The installer only enforces the 4 GB minimum.

## Disks and pools

**System disk.** The installer writes the system to one disk, or to two disks as a mirror (btrfs on an mdadm RAID 1). It never uses more than two.

- The disk must be at least 40 GB. Of it, Pithyx uses 36.5 GiB (a 512 MiB boot partition, 32 GiB for the system and 4 GiB of swap). The rest stays unused.
- USB disks, removable media, SD cards, read-only disks, disks in use and the installer stick itself are not offered.
- A hard disk is allowed, but the installer hints that an SSD is faster, quieter and uses less power.
- A disk that already holds data is shown with a warning. Installing erases it.
- With one disk the system is still built as a mirror with one member. Adding the second disk later is not possible in the interface yet.

**Data pools.** After the installation you create pools in the Storage app, on disks other than the system disk.

| Layout       | Minimum disks      |
| ------------ | ------------------ |
| Single disk  | 1                  |
| Stripe       | 2                  |
| Mirror       | 2                  |
| Mirror pairs | 4 (an even number) |
| RAID-Z1      | 3                  |
| RAID-Z2      | 4                  |
| RAID-Z3      | 5                  |

New pools are created with 4 KiB sectors (`ashift=12`), `lz4` compression and `atime=off`. A pool name starts with a lowercase letter and may contain lowercase letters, digits, hyphens and underscores, up to 32 characters. Pools are not encrypted. See [Storage](/docs/admin/storage/).

## Network

- Every wired network card asks for an address by DHCP (IPv4 and IPv6) on first boot. You can set a fixed address in the installer.
- The web desktop is served over HTTPS on port 7484; ports 80 and 443 redirect to it, except for domains you set up in the reverse proxy. Browsers warn about the box's own certificate until a device trusts its root.
- The box has a firewall that lets in only the web interface and the file services you switch on. See [Security](/docs/admin/security/#open-ports).
- Wi-Fi is not supported.
- The box does not announce a name on the network. Use its IP address, which the console shows after boot.

## Community platforms

There is no image for arm64 (for example the Raspberry Pi 5): the image and the installer are x86-64 only. The installer is tested in virtual machines with QEMU and KVM (UEFI firmware); other hypervisors and real hardware have not been tested systematically.
