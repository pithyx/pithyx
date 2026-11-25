---
title: 'Spiegel'
description: 'Eine einfache, aktuelle Kopie freigegebener Ordner und Home-Ordner auf einem Backup-Ziel halten, mit rsync über SSH, ohne Versionen, und was das für gelöschte und beschädigte Dateien heißt.'
status: draft
audience: admin
since: '1.4.0'
sidebar:
  order: 11
---

Ein **Spiegel** ist ein Box-Backup der Art **Spiegel** in der App **Backup**. Er hält eine einfache Kopie von Ordnern aus freigegebenen Ordnern und Home-Ordnern in einem Ordner auf einem [Backup-Ziel](/de/docs/admin/backup-targets/) und bringt sie nach Zeitplan mit [rsync](https://rsync.samba.org/) über SSH auf den neuesten Stand. Die Dateien auf dem Ziel sind gewöhnliche Dateien, die du dort direkt öffnen kannst, ohne die Box und ohne Schlüssel. Spiegel stehen unter **Box-Backups** neben den versionierten Backups, mit der Art **Spiegel** unter ihrem Namen, und ihre Läufe stehen unter [Aktivität](/de/docs/admin/backup-activity/) und in der [Übersicht](/de/docs/admin/backup-overview/).

:::caution[Ein Spiegel ist kein versioniertes Backup]
Ein Spiegel behält keine Versionen und ist nicht verschlüsselt. Eine Datei, die auf der Box geändert, beschädigt oder von Schadsoftware verschlüsselt wird, ändert sich beim nächsten Lauf auch auf dem Ziel, und mit **Auf dem Ziel löschen, was hier gelöscht wurde** ist eine versehentlich gelöschte Datei auch in der Kopie weg. Nimm einen Spiegel zusätzlich zu einem [versionierten Box-Backup](/de/docs/admin/backup-jobs/), nicht stattdessen.
:::

## Was das Ziel braucht

Ein Spiegel braucht rsync auf dem Ziel und einen Benutzer, der es über SSH starten darf. Der SFTP-Ordner des Ziels allein reicht nicht: Lässt das Ziel die Box nur für SFTP herein oder hat es kein rsync, scheitert das Hinzufügen mit [`backup.target.rsync_missing`](/de/docs/reference/errors/#backup.target.rsync_missing).

- Ein Linux-Server mit installiertem rsync funktioniert mit dem Benutzer des Ziels.
- Nimm auf einer zweiten Pithyx-Box einen Benutzer mit dem Recht **Über SSH mit einer Shell anmelden** und schalte dort unter Einstellungen > **Dateidienste** die **SSH-Anmeldungen** ein. Ein Benutzer nur mit SFTP kann rsync nicht starten.
- Speicherdienste, die rsync über SSH anbieten, funktionieren ebenfalls; manche bieten es auf einem anderen Port als SFTP an, zum Beispiel 23. Trag diesen Port beim Ziel ein.

## Einen Spiegel hinzufügen

1. Lege zuerst ein [Ziel](/de/docs/admin/backup-targets/) an und teste seine Verbindung.
2. Öffne **Backup**, wähle **Box-Backups** und **Box-Backup hinzufügen**.
3. Gib einen Namen ein und wähle unter **Art** den **Spiegel**.
4. Wähle das Ziel. **Ordner auf dem Ziel** ist der Ordner unterhalb des Ordners des Ziels, der die Kopie enthält; er folgt dem Namen, bis du ihn änderst. Die Box legt ihn an, wenn er fehlt. Eine Kopie, die schon dort liegt, zum Beispiel von vor einer Neuinstallation, bringt der erste Lauf auf den neuesten Stand. Zwei Backups der Box können nicht denselben Ordner auf einem Ziel nutzen ([`backup.repository.exists`](/de/docs/reference/errors/#backup.repository.exists)).
5. Wähle unter **Zu sichernde Ordner** freigegebene Ordner oder Home-Ordner und Ordner darin, wie bei einem [Box-Backup](/de/docs/admin/backup-jobs/#ein-box-backup-hinzufügen).
6. Wähle, ob die Box **Auf dem Ziel löschen, was hier gelöscht wurde** soll. Aus, die Vorgabe, behält Dateien auf dem Ziel, die auf der Box weg sind.
7. Wähle, wann er **Spiegelt**, und unter **Weitere Einstellungen** die Upload-Grenze, die Zeit bis überfällig und die Ausnahmen (rsync-Muster, zum Beispiel `*.tmp` oder `cache/`).
8. Wähle **Erstellen**. Die Box legt den Ordner über SFTP an, schreibt und entfernt eine Testdatei und lässt rsync den Ordner auflisten; dann speichert sie den Spiegel. Ein Spiegel hat keinen Schlüssel.

**Bearbeiten** ändert alles außer der Art, dem Ziel und dem Ordner auf dem Ziel; für andere legst du einen neuen Spiegel an. Ein Spiegel hat keine **Aufzubewahrenden Versionen**, keine Prüfung und keine Download-Grenze, und der Daemon lehnt sie ab ([`backup.job.invalid`](/de/docs/reference/errors/#backup.job.invalid)).

## Was ein Lauf macht

Ein Lauf nimmt zuerst einen ZFS-Snapshot jeder Quelle, wie ein Box-Backup, damit die Kopie einen einzigen Moment zeigt. Dann schickt rsync die Ordner in den Ordner des Spiegels auf dem Ziel, jeden unter dem Namen seines freigegebenen Ordners oder Home-Ordners: Der Ordner `Photos/2026` des freigegebenen Ordners Media landet in `<Ordner>/Media/Photos/2026`, ein Home-Ordner in `<Ordner>/homes/<Benutzer>`. rsync schickt nur geänderte Dateien und davon nur die geänderten Teile und behält Änderungszeiten, symbolische Links und harte Links. Besitzer, Rechte und ACLs bleiben nicht erhalten: Die Dateien gehören dem Benutzer des Ziels, der sie immer lesen und ändern kann.

Die **Größe** eines Laufs ist die Größe der Kopie, die hinzugekommenen Daten sind, was rsync übertragen hat. Eine Datei, die die Box nicht lesen konnte oder die während des Laufs verschwand, macht den Lauf zu **Mit Warnungen gesichert** und steht in seinen Details. Ein Ziel, das voll ist oder das Schreiben verweigert, lässt den Lauf mit [`backup.target.not_writable`](/de/docs/reference/errors/#backup.target.not_writable) scheitern; ein Ziel, das nicht erreichbar ist oder den Schlüssel ablehnt, wie bei jedem Ziel.

## Aus einem Spiegel wiederherstellen

Die Kopie besteht aus einfachen Dateien auf dem Ziel, also holst du sie mit jedem SFTP-Programm zurück, mit rsync oder über die Dateidienste des Ziels, zum Beispiel SMB auf einer zweiten Box. Ein Spiegel hat keinen Reiter **Versionen**, keinen **Schlüssel** und kein **Jetzt prüfen** und erscheint nicht unter [Wiederherstellen](/de/docs/admin/backup-restore/) ([`backup.job.unsupported`](/de/docs/reference/errors/#backup.job.unsupported)).

## Einen Spiegel löschen

**Löschen** entfernt den Spiegel und seine Läufe von der Box. Die Kopie bleibt auf dem Ziel.
