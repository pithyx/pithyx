---
title: 'Was ist Pithyx'
description: 'Pithyx in wenigen Minuten: ein NAS- und Server-Betriebssystem mit Web-Desktop, das nur auf deiner eigenen Hardware läuft.'
status: ready
audience: user
sidebar:
  order: 1
---

Pithyx ist ein freies, selbst gehostetes Betriebssystem für ein NAS oder einen Heimserver. Du installierst es auf deinem eigenen Computer und verwaltest danach Speicher, Benutzer, Dateifreigaben und Apps in einem Web-Desktop, den du in jedem Browser öffnest. Alles läuft auf deiner Box: Es gibt kein Cloud-Konto, keine Telemetrie und keinen vom Projekt betriebenen Dienst, von dem deine Box abhängt.

:::caution
Pithyx 1.0 ist als Beta gekennzeichnet: Wir halten es für stabil, aber es hat sich noch nicht im Alltag auf echten Boxen bewährt. Behandle es als Beta und halte von Daten, die dir wichtig sind, eine Kopie auf einem anderen Gerät.
:::

## Was du bekommst

- **Ein Web-Desktop.** Fenster, eine Taskleiste, Desktop-Verknüpfungen und Notizen, im Browser auf PC, Tablet oder Handy. Siehe [Der Desktop](/de/docs/user/desktop/).
- **Speicher mit ZFS.** Du baust in der App „Speicher“ aus deinen Festplatten Pools und legst darauf freigegebene Ordner an. Neue Pools nutzen Kompression und sind nicht verschlüsselt. Siehe [Speicher und Pools](/de/docs/admin/storage/).
- **Dateifreigabe.** Freigegebene Ordner sind über SMB, SFTP, WebDAV und FTP erreichbar. SMB ist standardmäßig an; die anderen schaltest du in den Einstellungen ein. Siehe [Von anderen Geräten zugreifen](/de/docs/user/file-access/).
- **Benutzer und Gruppen.** Jeder Benutzer ist ein echtes Konto auf der Box. Rechte werden an Benutzer und Gruppen vergeben; ein Benutzer erhält die Vereinigung aller seiner Rechte. Administratoren dürfen alles. Siehe [Benutzer und Gruppen](/de/docs/admin/users-and-groups/).
- **Apps.** Apps öffnen sich in Fenstern auf dem Desktop. Eine App ist ein Frontend mit einem optionalen Backend in Containern. Administratoren installieren Apps aus einer Datei oder aus einem Katalog im Store; der offizielle und der Community-Katalog sind standardmäßig an. Siehe [Apps benutzen](/de/docs/user/apps/).
- **Backup der Konfiguration.** Einstellungen, Benutzer und die Definitionen der freigegebenen Ordner lassen sich in einer verschlüsselten Datei sichern und nach einer Neuinstallation wiederherstellen. Siehe [Konfigurationsbackup](/de/docs/admin/backup/).
- **Netzwerkeinstellungen im Browser**, auch Bonds und VLANs, mit einem Bestätigungs-Timer, der eine Änderung zurücknimmt, die dich aussperren würde. Siehe [Netzwerk](/de/docs/admin/network/).

Die Kern-Apps sind Dateien, Betrachter, Speicher, Einstellungen, Ressourcenmonitor, Protokoll-Center, Über Pithyx und der Store.

## Wie es gebaut ist

- Die Box läuft mit Debian 13. Der Pithyx-Daemon (C# auf .NET 10) ist der einzige privilegierte Prozess und liefert den Web-Desktop und die API aus.
- Der Desktop ist eine React-Webanwendung. Kern-Apps und Apps von Drittanbietern nutzen dasselbe SDK.
- Die Systemplatte enthält das System auf btrfs, in einem RAID 1 aus einer oder zwei Platten. Deine Daten liegen auf ZFS-Pools auf anderen Platten.
- Die Einstellungen liegen in einer kleinen SQLite-Datenbank auf der Systemplatte, sodass sich die Systemplatte aus einem Konfigurationsbackup neu aufbauen lässt.
- Pithyx steht unter der AGPL-3.0. Die npm-Pakete für App-Entwickler stehen unter der MIT-Lizenz.

Details findest du unter [Architektur des Daemons](/de/docs/developers/daemon/architecture/).

## Was Pithyx nicht ist

- Es ist noch nichts für Daten, die du dir nicht leisten kannst zu verlieren (siehe die Warnung oben).
- Es ist ein System für frische Installationen. Es gibt keinen Import aus anderen NAS-Systemen.
- Der Installer stellt kein Backup wieder her und importiert keine Pools. Beides passiert nach der ersten Anmeldung.
- Es bietet kein NFS. Pools sind noch nicht verschlüsselt.
- Es braucht einen 64-Bit-x86-Computer mit UEFI und ausgeschaltetem Secure Boot. Es gibt keinen Installer für 32-Bit-Rechner, Legacy-BIOS oder Raspberry Pi. Siehe [Hardware-Anforderungen](/de/docs/getting-started/requirements/).
- Es unterstützt nur kabelgebundene Netzwerke. WLAN wird nicht unterstützt.
- Es kündigt sich nicht im Netzwerk an, du findest die Box also über ihre Adresse.

## Wie es weitergeht

- [Hardware-Anforderungen](/de/docs/getting-started/requirements/)
- [Schnellstart](/de/docs/getting-started/quick-start/)
- [Der Web-Installer](/de/docs/install/installer/)
- [Der Desktop](/de/docs/user/desktop/)
- [Häufige Fragen](/de/docs/faq/)
