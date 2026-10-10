---
question: 'Funktioniert Pithyx mit Secure Boot oder Legacy-BIOS?'
category: install
tags: [uefi, secure boot, bios, csm, legacy, firmware]
order: 20
status: ready
---

Nein. Pithyx startet nur im UEFI-Modus, und Secure Boot muss aus sein, weil das ZFS-Modul nicht signiert ist. Die erste Seite des Installers prüft beides und sagt dir, was du in den Firmware-Einstellungen ändern musst.

Details: [Firmware und Boot](/de/docs/install/firmware-and-boot/).
