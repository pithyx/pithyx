---
question: 'Sind Snapshots und Wiederherstellungspunkte ein Backup?'
category: storage
tags: [backup, sicherung, snapshot, wiederherstellungspunkte, ransomware, konfigurationsbackup]
order: 70
status: ready
---

Nein. Wiederherstellungspunkte und Snapshots liegen auf denselben Platten wie deine Dateien; sie helfen gegen versehentliches Löschen und Ransomware, aber nicht gegen einen verlorenen Pool. Das Konfigurations-Backup in den Einstellungen enthält Einstellungen und Benutzer, nicht deine Dateien. Halte eine Kopie deiner Dateien auf einem anderen Gerät: Die App **Backup** richtet Server, die über SSH erreichbar sind, etwa eine gemietete Storage-Box, als [Backup-Ziele](/de/docs/admin/backup-targets/) ein; Backups nach Zeitplan dorthin folgen in einer späteren Version.

Details: [Freigegebene Ordner](/de/docs/admin/shared-folders/).
