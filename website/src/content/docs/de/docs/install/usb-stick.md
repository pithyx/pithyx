---
title: 'USB-Stick schreiben'
description: 'Die Image-Datei, wie du sie prüfst und wie du sie unter Windows, macOS und Linux auf einen USB-Stick schreibst.'
status: ready
audience: admin
sidebar:
  order: 1
---

Der Pithyx-Installer kommt als eine Image-Datei (`.iso`), die du auf einen USB-Stick schreibst und von der du deinen Computer startest. Das Image ist für 64-Bit-x86-Computer mit UEFI gedacht. Du lädst es von den [Releases auf GitHub](https://github.com/pithyx/pithyx/releases) herunter.

## Herunterladen

Das Image heißt `pithyx-<version>-amd64.iso`. Daneben liegt eine Prüfsummendatei, `pithyx-<version>-amd64.iso.sha256`. Das Volume-Label ist `PITHYX`. Der Installer braucht den Stick nur für die Installation und keine Internetverbindung.

Du brauchst einen USB-Stick, auf den das Image passt. Beim Schreiben wird alles darauf gelöscht.

## Den Download prüfen

Die Prüfsummendatei enthält den SHA-256-Hash des Images. Vergleiche den Hash deiner heruntergeladenen Datei damit. Das Image selbst trägt keine Signatur, die Prüfsumme sagt dir also nur, dass der Download vollständig und unverändert ist, nicht, wer ihn erstellt hat.

Unter Linux, im Ordner mit beiden Dateien:

```sh
sha256sum -c pithyx-<version>-amd64.iso.sha256
```

Unter macOS:

```sh
shasum -a 256 pithyx-<version>-amd64.iso
```

Unter Windows (PowerShell):

```powershell
Get-FileHash pithyx-<version>-amd64.iso -Algorithm SHA256
```

Unter macOS und Windows vergleichst du den ausgegebenen Hash mit dem aus der `.sha256`-Datei mit bloßem Auge.

## Unter Windows, macOS und Linux schreiben

Das Image ist ein Hybrid-Image: Schreibe es als ganzes Datenträgerabbild auf den Stick, nicht als Dateien, die du auf den Stick kopierst. Jedes Programm, das Datenträgerabbilder schreibt, funktioniert. Wähle den Stick als Ziel und die `.iso`-Datei als Quelle, im Raw-Modus (DD), falls das Programm danach fragt.

:::caution
Beim Schreiben wird alles auf dem Stick gelöscht. Prüfe, dass du das richtige Laufwerk gewählt hast. Ein falsches Ziel kann eine andere Festplatte löschen.
:::

Unter Linux kannst du den Befehl `dd` nutzen. Ersetze `/dev/sdX` durch das Gerät des Sticks (prüfe es mit `lsblk`):

```sh
sudo dd if=pithyx-<version>-amd64.iso of=/dev/sdX bs=4M status=progress conv=fsync
```

Unter Windows und macOS nimmst du ein grafisches Schreibprogramm deiner Wahl, unter macOS auch `dd`. Der Stick startet nur im UEFI-Modus; einen Legacy-BIOS-Start gibt es nicht.

Der Installer wird in virtuellen Maschinen getestet (QEMU mit KVM und UEFI-Firmware), mit dem Image als USB-Stick. Den Start als virtuelle CD oder auf einer Auswahl echter Hardware hat niemand getestet.

Wenn der Stick fertig ist, mach weiter mit [Firmware und Boot](/de/docs/install/firmware-and-boot/).
