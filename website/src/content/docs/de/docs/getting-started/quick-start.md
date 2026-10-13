---
title: 'Schnellstart'
description: 'Vom Image bis zum ersten angemeldeten Desktop in so wenigen Schritten wie möglich, mit Links zu den Details.'
status: ready
audience: admin
sidebar:
  order: 3
---

Der kurze Weg: Schreibe das Pithyx-Image auf einen USB-Stick, starte den Computer davon, beende den Installer in einem Browser auf einem anderen Gerät und melde dich dann unter `https://<box-address>:7484` an. Diese Seite listet die Schritte auf und verlinkt auf die Seiten, die jeden davon erklären.

## Das Image herunterladen

Einen öffentlichen Download gibt es noch nicht. Das Image ist eine Datei namens `pithyx-<version>-amd64.iso` mit einer Prüfsummendatei daneben. Siehe [USB-Stick schreiben](/de/docs/install/usb-stick/).

## Den USB-Stick schreiben

Schreibe das Image mit einem beliebigen Programm, das Datenträgerabbilder schreibt, auf einen USB-Stick. Der ganze Stick wird überschrieben. Siehe [USB-Stick schreiben](/de/docs/install/usb-stick/).

## Installieren

1. Prüfe die [Hardware-Anforderungen](/de/docs/getting-started/requirements/). Nutze in der Firmware den UEFI-Modus und schalte Secure Boot aus. Siehe [Firmware und Boot](/de/docs/install/firmware-and-boot/).
2. Starte den Computer vom Stick und wähle **Install Pithyx** (es startet nach 5 Sekunden von selbst).
3. Lies die Adresse auf der Konsole ab, zum Beispiel `http://192.168.1.50:7484`, und öffne sie in einem Browser auf einem anderen Gerät.
4. Gehe die Schritte des [Web-Installers](/de/docs/install/installer/) durch: Name und Region, Netzwerk, Systemfestplatte, Administratorkonto, Zusammenfassung.
5. Wähle **Pithyx installieren** und warte. Notiere dir den Wiederherstellungsschlüssel, der erscheint, entferne dann den Stick und starte neu.

## Erste Einrichtung

Der Installer legt das Owner-Konto schon an, deshalb gibt es keine eigene Einrichtungsseite. Melde dich mit dem Konto an, das du gewählt hast. Siehe [Erste Einrichtung](/de/docs/install/first-setup/).

## Nächste Schritte

1. Öffne **Speicher** und lege einen Pool auf deinen Daten-Festplatten an.
2. Öffne Einstellungen > **Freigegebene Ordner** und lege freigegebene Ordner auf dem Pool an.
3. Lege Benutzer unter Einstellungen > **Benutzer** an und gib ihnen Zugriff auf die freigegebenen Ordner. Siehe [Benutzer und Gruppen](/de/docs/admin/users-and-groups/).
4. Schalte unter Einstellungen > **Dateidienste** die Dateidienste ein, die du brauchst ([Dateidienste](/de/docs/admin/file-services/)), und verbinde dich von deinen Geräten aus ([Von anderen Geräten zugreifen](/de/docs/user/file-access/)).
5. Erstelle unter Einstellungen > **Backup und Wiederherstellung** ein Konfigurationsbackup. Siehe [Konfigurationsbackup](/de/docs/admin/backup/).
