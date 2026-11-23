---
title: 'Backup-Ziele'
description: 'Server, die über SSH erreichbar sind, in der App Backup als Ziele für Backups einrichten, mit eigenem Schlüssel und bestätigtem Host-Schlüssel.'
status: ready
audience: admin
sidebar:
  order: 11
---

Die App **Backup** für Administratoren schickt Kopien deiner Dateien auf andere Rechner. Ihr erster Teil sind die **Ziele**: die Server, auf die Backups gehen. Ein Ziel ist jeder Server, an dem du dich über SSH mit SFTP anmelden kannst, zum Beispiel eine gemietete Storage-Box oder ein anderer Linux-Server. [Box-Backups](/de/docs/admin/backup-jobs/) schicken Ordner nach Zeitplan an ein Ziel.

## Ein Ziel hinzufügen

1. Öffne **Backup** im Pithyx-Menü und wähle **Ziel hinzufügen**.
2. Gib einen Namen ein, den Server (Hostname oder IP-Adresse), den SSH-Port (22, wenn leer; manche Speicheranbieter nutzen 23), den Benutzer und den Ordner auf dem Server. Der Ordner ist relativ zum Anmeldeordner des Benutzers oder beginnt mit `/`; lass ihn leer für den Anmeldeordner.
3. Wähle **Weiter**. Die Box liest die Host-Schlüssel, die der Server anbietet, und zeigt ihre Fingerabdrücke, Ed25519 zuerst.
4. Vergleich den Fingerabdruck mit dem, den dir dein Anbieter oder die Person zeigt, die den Server betreut. Auf dem Server selbst gibt `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` ihn aus. Wähle **Fingerabdruck stimmt, speichern** nur, wenn er gleich ist.
5. Die Box erzeugt den eigenen SSH-Schlüssel des Ziels und zeigt seinen öffentlichen Schlüssel. Trag diese Zeile in die Datei `.ssh/authorized_keys` des Benutzers auf dem Server ein und wähle dann **Verbindung testen**.

Die Box speichert für ein Ziel kein Passwort: Sie meldet sich nur mit dem Schlüssel des Ziels an. Jedes Ziel hat einen eigenen Schlüssel, eine Zeile weniger in den `authorized_keys` eines Servers sperrt also genau ein Ziel aus.

## Die Verbindung testen

**Verbindung testen** meldet sich mit dem Schlüssel des Ziels an, legt den Ordner an, wenn er fehlt, schreibt eine Testdatei und entfernt sie wieder. Die Karte zeigt das Ergebnis als **Verbunden**, **Verbindung fehlgeschlagen** oder **Nicht getestet**, mit der Zeit des letzten Tests. Ein fehlgeschlagener Test nennt den Grund:

- [`backup.target.unreachable`](/de/docs/reference/errors/#backup.target.unreachable): Der Name ist unbekannt, der Port ist zu, es kommt keine Antwort oder es gibt keinen Weg dorthin.
- [`backup.target.auth_failed`](/de/docs/reference/errors/#backup.target.auth_failed): Der Server hat den Schlüssel abgelehnt. Prüf, ob der öffentliche Schlüssel in den `authorized_keys` des Benutzers steht und der Benutzername stimmt.
- [`backup.target.not_writable`](/de/docs/reference/errors/#backup.target.not_writable): Die Box hat sich angemeldet, konnte den Ordner aber nicht anlegen oder dort keine Datei schreiben.
- [`backup.target.host_key_changed`](/de/docs/reference/errors/#backup.target.host_key_changed): siehe unten.

Wenn du Server, Port, Benutzer, Ordner oder Host-Schlüssel änderst, gilt der letzte Test nicht mehr.

## Wenn sich der Host-Schlüssel ändert

Die Box verbindet sich nur mit dem Server, dessen Host-Schlüssel du bestätigt hast. Antwortet der Server mit einem anderen Schlüssel, bricht jede Verbindung mit `backup.target.host_key_changed` ab. Trifft ein Lauf oder eine Prüfung eines Backups darauf, bekommen die Administratoren das Ereignis **Host-Schlüssel eines Backup-Ziels geändert**. Das ist normal, wenn der Server neu installiert oder auf andere Hardware umgezogen wurde; sonst gibt sich vielleicht jemand als er aus. **Neuen Host-Schlüssel prüfen** auf der Karte des Ziels zeigt den bestätigten Fingerabdruck neben denen, die der Server jetzt anbietet. Nimm den neuen erst an, wenn du ihn mit dem Fingerabdruck verglichen hast, den dein Anbieter zeigt.

Auch wenn du Server oder Port eines Ziels änderst, musst du den Host-Schlüssel des neuen Servers bestätigen.

## Ein Ziel löschen

**Löschen** entfernt das Ziel und seinen Schlüssel von der Box. Ein Ziel, das noch Box-Backups nutzen, lässt sich nicht löschen ([`backup.target.in_use`](/de/docs/reference/errors/#backup.target.in_use)); lösche zuerst diese Backups. Entferne seinen öffentlichen Schlüssel auch aus den `authorized_keys` auf dem Server; Backups, die schon dort liegen, bleiben erhalten.

## Konfigurations-Backup

Ziele, ihre Schlüssel und ihre bestätigten Host-Schlüssel sind im Teil **App Backup** des [Konfigurations-Backups](/de/docs/admin/backup/). Die privaten Schlüssel kommen nie in ein [Support-Paket](/de/docs/admin/logs-and-monitoring/).
