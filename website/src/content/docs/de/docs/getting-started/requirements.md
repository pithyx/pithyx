---
title: 'Hardware-Anforderungen'
description: 'Die Hardware, die Pithyx braucht, die Referenzmaschine und was nicht unterstützt wird.'
status: ready
audience: admin
sidebar:
  order: 2
---

Pithyx lässt sich auf einem 64-Bit-x86-Computer mit UEFI-Firmware, mindestens 4 GB Arbeitsspeicher und einer internen Systemfestplatte von mindestens 40 GB installieren. Der Installer prüft das alles, bevor du anfangen darfst. Deine Daten kommen auf zusätzliche Festplatten, aus denen du nach der Installation Pools machst.

## Unterstützte Plattformen

Der Installer prüft auf seiner ersten Seite fünf Dinge und startet nicht, wenn eine davon fehlschlägt:

| Prüfung         | Voraussetzung                                                                                                    |
| --------------- | ---------------------------------------------------------------------------------------------------------------- |
| Prozessor       | 64-Bit-x86 mit x86-64-v2 (braucht unter anderem SSE4.2 und POPCNT)                                               |
| Firmware        | Im UEFI-Modus gestartet. Legacy-BIOS und CSM-Modus funktionieren nicht.                                          |
| Arbeitsspeicher | Mindestens 4 GB (die Prüfung lässt etwa 400 MB Toleranz für Speicher zu, den die Firmware reserviert)            |
| Secure Boot     | Ausgeschaltet. Das ZFS-Modul ist nicht signiert. Siehe [Firmware und Boot](/de/docs/install/firmware-and-boot/). |
| Systemplatte    | Mindestens eine passende interne Festplatte mit 40 GB oder mehr                                                  |

Das Basissystem ist Debian 13. Es gibt keine 32-Bit-Version.

## Referenzhardware

Die Referenzmaschine ist ein Intel N100 oder N305 mit 8 GB Arbeitsspeicher. Der Daemon bleibt im Leerlauf unter 200 MB Speicher und unter 1 Prozent CPU. Diese Zahlen sind Budgets für die eigenen Tests des Projekts, keine Voraussetzungen. Der Installer erzwingt nur das Minimum von 4 GB.

## Festplatten und Pools

**Systemplatte.** Der Installer schreibt das System auf eine Festplatte oder auf zwei Festplatten als Spiegel (btrfs auf einem mdadm-RAID-1). Er nutzt nie mehr als zwei.

- Die Festplatte muss mindestens 40 GB groß sein. Davon belegt Pithyx 36,5 GiB (eine Boot-Partition mit 512 MiB, 32 GiB für das System und 4 GiB Swap). Der Rest bleibt ungenutzt.
- USB-Festplatten, Wechseldatenträger, SD-Karten, schreibgeschützte Festplatten, Festplatten in Benutzung und der Installer-Stick selbst werden nicht angeboten.
- Eine Festplatte mit Magnetscheiben ist erlaubt, aber der Installer weist darauf hin, dass eine SSD schneller und leiser ist und weniger Strom braucht.
- Eine Festplatte, auf der schon Daten liegen, wird mit einer Warnung angezeigt. Die Installation löscht sie.
- Mit einer Festplatte wird das System trotzdem als Spiegel mit einem Mitglied gebaut. Die zweite Festplatte später hinzuzufügen ist in der Oberfläche noch nicht möglich.

**Daten-Pools.** Nach der Installation legst du Pools in der Speicher-App an, auf anderen Festplatten als der Systemplatte.

| Aufbau          | Mindestanzahl Festplatten |
| --------------- | ------------------------- |
| Einzelne Platte | 1                         |
| Stripe          | 2                         |
| Spiegel         | 2                         |
| Spiegelpaare    | 4 (eine gerade Anzahl)    |
| RAID-Z1         | 3                         |
| RAID-Z2         | 4                         |
| RAID-Z3         | 5                         |

Neue Pools werden mit 4-KiB-Sektoren (`ashift=12`), `lz4`-Kompression und `atime=off` angelegt. Ein Pool-Name beginnt mit einem Kleinbuchstaben und darf Kleinbuchstaben, Ziffern, Bindestriche und Unterstriche enthalten, bis zu 32 Zeichen. Pools sind nicht verschlüsselt. Siehe [Speicher und Pools](/de/docs/admin/storage/).

## Netzwerk

- Jede kabelgebundene Netzwerkkarte fragt beim ersten Start per DHCP nach einer Adresse (IPv4 und IPv6). Im Installer kannst du eine feste Adresse einstellen.
- Der Web-Desktop wird über HTTPS auf Port 7484 ausgeliefert; die Ports 80 und 443 leiten dorthin weiter. Browser warnen vor dem eigenen Zertifikat der Box, bis ein Gerät ihrem Root-Zertifikat vertraut.
- Die Box hat eine Firewall, die nur die Weboberfläche und die Dateidienste durchlässt, die du einschaltest. Siehe [Sicherheit](/de/docs/admin/security/#offene-ports).
- WLAN wird nicht unterstützt.
- Die Box kündigt keinen Namen im Netzwerk an. Nutze ihre IP-Adresse, die die Konsole nach dem Start anzeigt.

## Plattformen der Community

Es gibt kein Image für arm64 (zum Beispiel den Raspberry Pi 5): Image und Installer gibt es nur für x86-64. Der Installer wird in virtuellen Maschinen mit QEMU und KVM (UEFI-Firmware) getestet; andere Hypervisoren und echte Hardware wurden nicht systematisch getestet.
