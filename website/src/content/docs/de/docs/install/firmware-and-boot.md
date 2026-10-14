---
title: 'Firmware und Boot'
description: 'UEFI-Einstellungen, warum Secure Boot aus sein muss, und das Bootmenü des Sticks und des installierten Systems.'
status: ready
audience: admin
sidebar:
  order: 2
---

Pithyx startet nur im UEFI-Modus, und Secure Boot muss ausgeschaltet sein. Beides änderst du in den Firmware-Einstellungen deines Computers, bevor du den Installer startest. Der Stick zeigt ein kurzes Bootmenü, und der Installer läuft danach in einem Browser auf einem anderen Gerät.

## UEFI-Einstellungen

1. Schalte den Computer ein und öffne seine Firmware-Einstellungen. Die Taste ist bei jedem Hersteller anders; oft ist es **Entf**, **F2**, **F10** oder **F12**. Viele Computer haben außerdem ein einmaliges Bootmenü, in dem du den Stick auswählst.
2. Stelle sicher, dass die Firmware im **UEFI**-Modus bootet. Der Legacy-Modus (BIOS oder CSM) funktioniert nicht. Wenn du im Legacy-Modus bootest, scheitert auf der ersten Seite des Installers die Prüfung **Mit UEFI gestartet**, und du wirst aufgefordert, die Firmware auf UEFI umzustellen und erneut vom Stick zu starten.
3. Schalte Secure Boot aus (nächster Abschnitt).
4. Starte vom USB-Stick.

Das installierte System schreibt seinen Bootloader als Fallback-Eintrag auf die Boot-Partition jeder Systemfestplatte und legt für die erste Festplatte einen benannten Eintrag an. Bei einem Spiegel bekommt die zweite Festplatte einen eigenen Eintrag (**Pithyx (second disk)**). Die Box startet deshalb von jeder der beiden Festplatten, auch wenn der Stick noch steckt.

## Secure Boot

Secure Boot muss aus sein. Pithyx nutzt ein ZFS-Modul, das nicht signiert ist, und ein System, das mit eingeschaltetem Secure Boot startet, lädt keine unsignierten Module. Der Installer prüft das auf seiner ersten Seite. Ist Secure Boot an, siehst du: „Secure Boot ist an. Pithyx nutzt ein ZFS-Modul, das nicht signiert ist: Schalte Secure Boot in den Firmware-Einstellungen aus (oft unter Security oder Boot) und starte dann erneut vom USB-Stick.“

Pithyx unterstützt es nicht, Schlüssel für Secure Boot einzutragen. Bei manchen Computern weigert sich die Firmware möglicherweise ganz, den Stick zu starten, solange Secure Boot an ist. Dann erreichst du die Installer-Seite nicht, und Secure Boot auszuschalten ist die Lösung.

## Das GRUB-Menü

Der Stick startet ein Bootmenü (GRUB), das 5 Sekunden wartet und dann den ersten Eintrag wählt:

- **Install Pithyx** `<version>`: startet den Installer; die Konsole zeigt das Pithyx-Logo statt der Bootmeldungen.
- **UEFI firmware settings**: startet neu in die Firmware-Einstellungen.

Das installierte System hat ein eigenes Bootmenü, das 3 Sekunden wartet. Es hat kein Passwort. Neben dem System listet es **Pithyx recovery** (Taste `r`) und einen Eintrag je aufbewahrtem System-Snapshot; siehe [Wiederherstellen und neu installieren](/de/docs/install/recovery/).

## Vom Stick booten

Nachdem der Stick gestartet ist, zeigt die Konsole des Computers das Pithyx-Logo, die Adresse des Installers (zum Beispiel `http://192.168.1.50:7484`) und einen QR-Code dafür. Wird keine Adresse angezeigt, steht dort, dass auf eine Netzwerkadresse (DHCP) gewartet wird, und du sollst das Netzwerkkabel prüfen.

Öffne die Adresse in einem Browser auf einem anderen Gerät und mach weiter mit dem [Web-Installer](/de/docs/install/installer/).

Der Installer löscht die Festplatten, die du wählst, und sonst nichts. Er installiert nicht neben einem anderen Betriebssystem auf derselben Festplatte.
