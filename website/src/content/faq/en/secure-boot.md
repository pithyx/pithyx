---
question: 'Does Pithyx work with Secure Boot or legacy BIOS?'
category: install
tags: [uefi, secure boot, bios, csm, legacy, firmware]
order: 20
status: ready
---

No. Pithyx starts only in UEFI mode, and Secure Boot must be off because the ZFS module is not signed. The first page of the installer checks both and tells you what to change in the firmware settings.

Details: [Firmware and boot](/docs/install/firmware-and-boot/).
