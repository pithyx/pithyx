---
title: 'Write the USB stick'
description: 'The image file, how to check it and how to write it to a USB stick on Windows, macOS and Linux.'
status: ready
audience: admin
sidebar:
  order: 1
---

The Pithyx installer comes as one image file (`.iso`) that you write to a USB stick and start your computer from. The image is for 64-bit x86 computers with UEFI. Download it from the [releases on GitHub](https://github.com/pithyx/pithyx/releases).

## Download

The image is called `pithyx-<version>-amd64.iso`. Next to it is a checksum file, `pithyx-<version>-amd64.iso.sha256`. The volume label is `PITHYX`. The installer only needs the stick for the installation and needs no internet connection.

You need a USB stick that holds the image. Everything on it is erased when you write it.

## Check the download

The checksum file contains the SHA-256 hash of the image. Compare the hash of your downloaded file with it. The image itself carries no signature, so the checksum only tells you the download is complete and unchanged, not who made it.

On Linux, in the folder with both files:

```sh
sha256sum -c pithyx-<version>-amd64.iso.sha256
```

On macOS:

```sh
shasum -a 256 pithyx-<version>-amd64.iso
```

On Windows (PowerShell):

```powershell
Get-FileHash pithyx-<version>-amd64.iso -Algorithm SHA256
```

On macOS and Windows, compare the printed hash with the one in the `.sha256` file by eye.

## Write on Windows, macOS and Linux

The image is a hybrid image: write it to the stick as a whole disk image, not as files you copy onto the stick. Any program that writes disk images works. Choose the stick as the target and the `.iso` file as the source, in raw (DD) mode if the program asks.

:::caution
Writing erases everything on the stick. Check that you picked the right drive. A wrong target can erase another disk.
:::

On Linux you can use the `dd` command. Replace `/dev/sdX` with the device of the stick (check with `lsblk`):

```sh
sudo dd if=pithyx-<version>-amd64.iso of=/dev/sdX bs=4M status=progress conv=fsync
```

On Windows and macOS, use a graphical image writer of your choice, or `dd` on macOS. The stick starts in UEFI mode only; it has no legacy BIOS boot.

The installer is tested in virtual machines (QEMU with KVM and UEFI firmware), with the image attached as a USB stick. Starting it as a virtual CD or on a range of real hardware has not been tested.

When the stick is ready, continue with [Firmware and boot](/docs/install/firmware-and-boot/).
