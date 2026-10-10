---
question: 'Does the USB stick have to stay in after the installation?'
category: install
tags: [usb stick, boot order, remove stick, restart]
order: 50
status: ready
---

No. The installed system writes its boot loader to every system disk and adds a boot entry for it, so the box starts from its disk even with the stick still in. The installer still asks you to remove the stick before the restart.

Details: [UEFI settings](/docs/install/firmware-and-boot/#uefi-settings).
