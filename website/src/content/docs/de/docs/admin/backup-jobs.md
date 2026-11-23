---
title: 'Backup-Aufträge'
description: 'Freigegebene Ordner und Home-Ordner nach Zeitplan mit Versionen, Aufbewahrung und Prüfung auf ein Backup-Ziel sichern, mit restic über SFTP.'
status: draft
audience: admin
since: '1.4.0'
sidebar:
  order: 11
---

Ein **Auftrag** in der App **Backup** sichert Ordner aus freigegebenen Ordnern und Home-Ordnern nach Zeitplan auf ein [Backup-Ziel](/de/docs/admin/backup-targets/). Jeder Lauf nimmt zuerst einen ZFS-Snapshot, damit ein Backup einen einzigen Moment zeigt, auch wenn weitergearbeitet wird, und speichert mit [restic](https://restic.net/) über SFTP eine neue Version in das eigene Repository des Auftrags auf dem Ziel. Ältere Versionen dünnt die Aufbewahrung aus, und eine Prüfung liest das Repository nach einem eigenen Zeitplan zurück. Aufträge sind für Administratoren; die Box meldet ihnen fehlgeschlagene, unvollständige und überfällige Backups.

![Die App Backup, Aufträge: drei Aufträge, einer sichert gerade mit seinem Fortschritt, einer aufgeklappt mit seinen Ordnern und letzten Läufen, einer fehlgeschlagen und überfällig](../../../../../assets/screenshots/backup-jobs.de.webp)

_Jeder Auftrag ist eine Karte mit seinem Zustand, dem Ziel, dem nächsten Lauf und dem letzten Backup; der Pfeil zeigt seine Ordner, das Repository und die letzten Läufe._

## Einen Auftrag hinzufügen

1. Füge zuerst ein [Ziel](/de/docs/admin/backup-targets/) hinzu und teste seine Verbindung.
2. Öffne **Backup**, wähle **Aufträge** und **Auftrag hinzufügen**.
3. Gib einen Namen ein und wähle das Ziel. **Ordner des Repositorys** ist der Ordner unterhalb des Ordners des Ziels, der das Repository dieses Auftrags enthält; er folgt dem Namen, bis du ihn änderst.
4. Wähle unter **Zu sichernde Ordner** einen freigegebenen Ordner oder Home-Ordner und unter **Ordner darin** einen Ordner darin, oder nichts für alles. **Ordner hinzufügen** fügt weitere hinzu, bis zu 32.
5. Wähle, wann er **Sichert**: stündlich, täglich, wöchentlich oder monatlich zu einer Uhrzeit, oder einen Cron-Ausdruck mit fünf Feldern (Minute, Stunde, Tag des Monats, Monat, Wochentag). Die Zeiten gelten in der Zeitzone der Box. Ohne **Nach Zeitplan ausführen** läuft der Auftrag nur von Hand.
6. Lege die **Aufzubewahrenden Versionen** und die **Prüfung** fest (siehe unten) und unter **Weitere Einstellungen** die Grenzen, die Zeit bis überfällig und die Ausnahmen.
7. Wähle **Erstellen**. Die Box legt das Repository auf dem Ziel an und zeigt seinen Schlüssel.

Ein neuer Auftrag sichert jeden Tag um 02:00, behält 7 tägliche, 4 wöchentliche und 12 monatliche Versionen und prüft das Repository jeden Sonntag um 05:00, wobei er 5 Prozent der Daten liest.

Sichern lassen sich nur Ordner auf einem Pool, denn jeder Lauf beginnt mit einem Snapshot ([`backup.source.unsupported`](/de/docs/reference/errors/#backup.source.unsupported)). Ein Ordner, dessen Pool fehlt oder gesperrt ist, lässt den Lauf mit [`backup.source.unavailable`](/de/docs/reference/errors/#backup.source.unavailable) fehlschlagen.

Ziel und Repository eines Auftrags bleiben, wie sie sind; für ein anderes Ziel oder Repository fügst du einen neuen Auftrag hinzu. Alles andere lässt sich mit **Bearbeiten** ändern.

## Der Schlüssel des Repositorys

:::caution[Bewahre den Schlüssel außerhalb der Box auf]
Jedes Repository ist mit einem zufälligen Schlüssel verschlüsselt. Die Box zeigt ihn einmal, direkt nach **Erstellen**: Kopiere ihn oder lade ihn als Textdatei herunter und bewahre ihn an einem anderen Ort als dieser Box auf. Ohne den Schlüssel kann niemand die Backups lesen, auch nicht nach einer Neuinstallation. Der Dialog schließt sich erst, wenn du **Ich habe den Schlüssel außerhalb der Box aufbewahrt** ankreuzt.
:::

Die Box behält den Schlüssel selbst, um den Auftrag auszuführen, und er kommt in den Teil **App Backup** des [Konfigurations-Backups](/de/docs/admin/backup/), nie in ein [Support-Paket](/de/docs/admin/logs-and-monitoring/).

Um ein Repository zu verwenden, das es schon gibt, zum Beispiel nach einer Neuinstallation, kreuze **Vorhandenes Repository verwenden** an und füge seinen Schlüssel ein. Die Box prüft dann, dass in diesem Ordner ein Repository liegt ([`backup.repository.not_found`](/de/docs/reference/errors/#backup.repository.not_found)) und dass der Schlüssel es öffnet ([`backup.repository.wrong_key`](/de/docs/reference/errors/#backup.repository.wrong_key)). Ein neuer Auftrag lehnt einen Ordner ab, in dem schon ein Repository liegt ([`backup.repository.exists`](/de/docs/reference/errors/#backup.repository.exists)).

## Was ein Lauf sichert

Ein Lauf nimmt einen ZFS-Snapshot von jedem Dataset, auf dem die Ordner liegen, und lässt restic den Snapshot an Stelle des laufenden Ordners lesen. Das Repository speichert die Ordner unter ihren Pfaden auf der Box, mit Eigentümern, Rechten, ACLs und Änderungszeiten. Die Snapshots gibt es nur, solange der Lauf dauert; danach werden sie gelöscht. Jeder Lauf sichert als neue Version in das Repository des Auftrags; restic speichert nur, was sich seit der letzten geändert hat.

Läufe und Prüfungen sind Aufgaben der Box: Sie zeigen ihren Fortschritt auf der Karte des Auftrags und in der Aufgabenliste der Box, laufen pro Auftrag einzeln und lassen sich mit **Abbrechen** abbrechen. **Jetzt sichern** und **Jetzt prüfen** starten sofort einen. Eine Zeit, die die Box verpasst hat, weil sie aus war, wird nicht nachgeholt; die Warnung „überfällig“ deckt das ab.

Der Zustand eines Laufs:

- **Erfolgreich**: Jede Datei wurde gesichert.
- **Warnungen**: Eine neue Version wurde gesichert, aber einige Dateien ließen sich nicht lesen (restic-Exit-Code 3), oder das Entfernen alter Versionen schlug fehl. Das Log Center nennt sie.
- **Fehlgeschlagen**: Es wurde keine neue Version gesichert. Die Karte nennt den Grund, zum Beispiel [`backup.target.unreachable`](/de/docs/reference/errors/#backup.target.unreachable), [`backup.repository.locked`](/de/docs/reference/errors/#backup.repository.locked) oder [`backup.run.failed`](/de/docs/reference/errors/#backup.run.failed).
- **Abgebrochen**: Ein Administrator hat ihn abgebrochen.
- **Unterbrochen**: Die Box wurde während des Laufs neu gestartet.

**Letzte Läufe** in der aufgeklappten Karte listet die letzten Läufe und Prüfungen mit ihren Dateien, der Größe, den hinzugekommenen Daten und der Dauer.

## Aufzubewahrende Versionen

Nach jedem erfolgreichen Backup wendet die Box die Aufbewahrung an: Sie behält die neuesten Versionen, dann je eine Version pro Stunde, Tag, Woche, Monat und Jahr, so viele, wie du jeweils einträgst, und entfernt die anderen aus dem Repository, um ihren Platz freizugeben (`restic forget --prune`). Alle Werte auf 0 behalten jede Version.

## Prüfung

**Repository nach Zeitplan prüfen** führt `restic check` nach einem eigenen Zeitplan aus. **Zurückzulesende Daten in Prozent** liest jedes Mal diesen Anteil der Daten vom Ziel, bei jeder Prüfung einen anderen Teil; 0 prüft nur die Struktur des Repositorys. Eine fehlgeschlagene Prüfung ist ein eigenes Ereignis ([`backup.check.failed`](/de/docs/reference/errors/#backup.check.failed)): Starte eine weitere Prüfung, und wenn auch sie fehlschlägt, lege für den Auftrag ein neues Repository an.

## Weitere Einstellungen

- **Upload-Grenze in KiB/s** und **Download-Grenze in KiB/s**: begrenzen die Bandbreite, die restic zum Ziel nutzt; 0 für keine Grenze.
- **Als überfällig melden nach Stunden**: wie lange der Auftrag ohne erfolgreiches Backup bleiben darf, bevor er gemeldet wird. Leer heißt das Dreifache der Zeit zwischen zwei geplanten Läufen, mindestens zwei Tage.
- **Ausnahmen**: ein restic-Muster pro Zeile, zum Beispiel `*.tmp` oder `**/cache`, bis zu 100.

restic hält einen Cache des Index des Repositorys auf dem Pool des ersten Ordners des Auftrags, im Dataset `<pool>/pithyx/backup-cache`, damit nichts davon auf der Systemplatte landet.

## Ereignisse

Diese meldet die Box ihren Administratoren, auf dem Desktop und über die Kanäle, die unter Einstellungen > **Benachrichtigungen** > **Ereignisse** gewählt sind (Gruppe **Backups**):

- **Backup fehlgeschlagen**: Ein Lauf hat nichts gesichert.
- **Backup hat Dateien ausgelassen**: Ein Lauf endete mit Warnungen.
- **Backup überfällig**: Kein erfolgreiches Backup innerhalb der Zeit bis überfällig.
- **Prüfung eines Backups fehlgeschlagen**: Eine Prüfung ist fehlgeschlagen.
- **Host-Schlüssel eines Backup-Ziels geändert**: Der Server des Ziels hat mit einem anderen Host-Schlüssel geantwortet ([Backup-Ziele](/de/docs/admin/backup-targets/#wenn-sich-der-host-schlüssel-ändert)).

## Einen Auftrag löschen

**Löschen** entfernt den Auftrag, seine Läufe und seinen Schlüssel von der Box. Das Repository bleibt auf dem Ziel; ohne den Schlüssel kann niemand es lesen. Ein laufender Auftrag lässt sich nicht löschen ([`backup.job.busy`](/de/docs/reference/errors/#backup.job.busy)); brich den Lauf zuerst ab.

## Wiederherstellen

Wiederherstellen aus der App Backup kommt in einer späteren Version. Bis dahin öffnet restic selbst das Repository mit seinem Schlüssel von jedem Rechner aus, der das Ziel über SFTP mit einem eigenen SSH-Schlüssel erreicht, zum Beispiel `restic -r sftp:<benutzer>@<server>:<ordner>/<repository> restore latest --target <ordner>`.
