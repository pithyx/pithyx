---
title: 'Firmware and boot'
description: 'UEFI settings, why Secure Boot must be off, and the boot menu of the stick and the installed system.'
status: draft
audience: admin
sidebar:
  order: 2
---

Pithyx starts only in UEFI mode, and Secure Boot must be switched off. You change both in the firmware settings of your computer, before you start the installer. The stick shows a short boot menu, and the installer then runs in a browser on another device.

## UEFI settings

1. Switch on the computer and open its firmware settings. The key is different for each maker; often it is **Delete**, **F2**, **F10** or **F12**. Many computers also have a one-time boot menu for choosing the stick.
2. Make sure the firmware boots in **UEFI** mode. Legacy (BIOS or CSM) mode does not work. If you boot in legacy mode, the first page of the installer fails the check **Started with UEFI** and tells you to switch the firmware to UEFI and start from the stick again.
3. Switch Secure Boot off (next section).
4. Start from the USB stick.

The installed system writes its boot loader to the boot partition of each system disk as a fallback entry, and adds a named entry for the first disk. With a mirror, the second disk gets its own entry (**Pithyx (second disk)**). The box therefore starts from either disk, even with the stick still in.

## Secure Boot

Secure Boot must be off. Pithyx uses a ZFS module that is not signed, and a system started with Secure Boot on does not load unsigned modules. The installer checks this on its first page. If Secure Boot is on, you see: "Secure Boot is on. Pithyx uses a ZFS module that is not signed: switch Secure Boot off in the firmware settings (often under Security or Boot), then start from the USB stick again."

Pithyx does not support enrolling keys for Secure Boot. On some computers, the firmware may refuse to start the stick at all while Secure Boot is on. In that case you do not reach the installer page, and switching Secure Boot off is the fix.

## The GRUB menu

The stick starts a boot menu (GRUB) that waits 5 seconds and then picks the first entry:

- **Install Pithyx** `<version>`: starts the installer quietly.
- **Install Pithyx** `<version>` **(show boot messages)**: the same with boot messages, useful when something does not start.
- **UEFI firmware settings**: restarts into the firmware settings.

The installed system has a boot menu of its own that waits 3 seconds. It has no password.

## Booting from the stick

After the stick has started, the console of the computer shows the Pithyx logo, the address of the installer (for example `http://192.168.1.50:7484`) and a QR code for it. If no address is shown, it says that it is waiting for a network address (DHCP) and asks you to check the network cable.

Open the address in a browser on another device to continue with [the web installer](/docs/install/installer/).

The installer erases the disks you choose and nothing else. It does not install next to another operating system on the same disk.
