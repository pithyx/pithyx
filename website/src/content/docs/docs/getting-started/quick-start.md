---
title: 'Quick start'
description: 'From the image to the first signed-in desktop in the fewest steps, with links to the details.'
status: ready
audience: admin
sidebar:
  order: 3
---

The short path: write the Pithyx image to a USB stick, start the computer from it, finish the installer in a browser on another device, then sign in at `https://<box-address>:7484`. This page lists the steps and links to the pages that explain each one.

## Download the image

Download the image from the [releases on GitHub](https://github.com/pithyx/pithyx/releases): a file named `pithyx-<version>-amd64.iso`, with a checksum file next to it. See [Write the USB stick](/docs/install/usb-stick/).

## Write the USB stick

Write the image to a USB stick with any tool that writes disk images. The whole stick is overwritten. See [Write the USB stick](/docs/install/usb-stick/).

## Install

1. Check the [hardware requirements](/docs/getting-started/requirements/). In the firmware, use UEFI mode and switch Secure Boot off. See [Firmware and boot](/docs/install/firmware-and-boot/).
2. Start the computer from the stick and choose **Install Pithyx** (it starts by itself after 5 seconds).
3. Read the address on the console, for example `http://192.168.1.50:7484`, and open it in a browser on another device.
4. Go through the steps of [the web installer](/docs/install/installer/): name and region, network, system disk, administrator account, summary.
5. Choose **Install Pithyx** and wait. Write down the recovery key that appears, then remove the stick and restart.

## First setup

The installer already creates the owner account, so there is no separate setup page. Sign in with the account you chose. See [First setup](/docs/install/first-setup/).

## Next steps

1. Open Settings > **Pools** and create a pool on your data disks.
2. Open Settings > **Shared folders** and create shared folders on the pool.
3. Create users in Settings > **Users and groups** and give them access to the shared folders. See [Users and groups](/docs/admin/users-and-groups/).
4. Switch on the file services you need in Settings > **File services** ([File services](/docs/admin/file-services/)) and connect from your devices ([Access files from other devices](/docs/user/file-access/)).
5. Make a configuration backup in Settings > **Backup and restore**. See [Backup and restore](/docs/admin/backup/).
