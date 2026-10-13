---
title: 'Benutzer und Gruppen'
description: 'Benutzer und Gruppen anlegen, ihnen Rechte geben, Konten deaktivieren oder löschen, und was mit Home-Ordnern passiert.'
status: ready
audience: admin
sidebar:
  order: 5
---

Administratoren verwalten Konten unter Einstellungen > **Benutzer** und Einstellungen > **Gruppen**. Jeder Pithyx-Benutzer ist zugleich ein Linux-Konto mit eigener Benutzer-ID, sodass Dateien, die er anlegt, ihm gehören. Passwörter haben mindestens 8 Zeichen, und nur ein Administrator kann für einen anderen Benutzer ein neues Passwort setzen.

## Einen Benutzer anlegen

Wähle **Benutzer anlegen** und fülle aus:

- **Benutzername**: Kleinbuchstaben, Ziffern, Bindestriche und Unterstriche, beginnend mit einem Buchstaben oder Unterstrich, höchstens 32 Zeichen. Systemnamen wie `root`, `daemon`, `sshd`, `pithyx`, `docker` und `users` sowie Namen, die mit `systemd-` beginnen, werden abgelehnt ([`auth.username.reserved`](/de/docs/reference/errors/#auth.username.reserved)). Ein Name, den ein Systemkonto schon benutzen darf, wird mit [`users.account.refused`](/de/docs/reference/errors/#users.account.refused) abgelehnt.
- **Anzeigename (optional)**: höchstens 64 Zeichen.
- **Passwort**: 8 bis 1024 Zeichen. Allgemein bekannte Passwörter werden abgelehnt ([`auth.password.too_common`](/de/docs/reference/errors/#auth.password.too_common)); weitere Regeln für den Aufbau gibt es nicht.
- **Gruppen**. Jeder Benutzer ist außerdem in der Gruppe users.

Benutzer-IDs stammen aus dem Bereich 20000 bis 29999 und werden nie wieder vergeben. Gruppen-IDs stammen aus dem Bereich 30000 bis 39999. Die Shell ist `/usr/sbin/nologin`, es sei denn, der Benutzer hat das Recht zur SSH-Anmeldung und SSH-Anmeldungen sind eingeschaltet. Ein neuer Benutzer kann sich am Desktop anmelden, SMB nutzen und die Apps Dateien und Betrachter verwenden; das kannst du im Rechte-Dialog ändern.

Das Passwort wird auch für die Linux- und Samba-Konten gesetzt. Ein Benutzer, der noch keines hat, ist markiert mit „Noch nicht bereit für SMB und SFTP: Der Benutzer meldet sich einmal im Web an, oder du setzt ein neues Passwort.“ Abzeichen in der Liste: Administrator, Zwei-Faktor, Deaktiviert.

## Gruppen

Wähle **Gruppe anlegen** unter Einstellungen > **Gruppen**. Eine Gruppe hat einen Namen (dieselben Regeln wie bei Benutzernamen, und er darf nicht mit `pithyx-` beginnen), eine **Beschreibung (optional)** mit bis zu 200 Zeichen und Mitglieder. Rechte, die einer Gruppe gegeben werden, gelten für alle ihre Mitglieder. Das Löschen einer Gruppe lässt ihre Mitglieder bestehen; Dateien behalten die Gruppen-ID, und sie wird nie an eine andere Gruppe vergeben. Die eingebaute Gruppe users enthält jeden Benutzer und lässt sich nicht umbenennen, löschen oder von Hand mit Mitgliedern versehen ([`users.group.builtin`](/de/docs/reference/errors/#users.group.builtin)).

## Sonderrechte

Der Rechte-Dialog eines Benutzers oder einer Gruppe hat vier Sonderrechte: „Administrator (darf alles ändern)“, „Am Desktop anmelden“, „Über SSH mit einer Shell anmelden“ und „Neu starten und herunterfahren“. Weitere Abschnitte betreffen Dateidienste, Apps und freigegebene Ordner. Wie sich Rechte verbinden, steht unter [Berechtigungen](/de/docs/admin/permissions/). Ohne „Am Desktop anmelden“ kann sich ein Benutzer nicht am Web-Desktop anmelden; wer das Recht verliert, wird mit der nächsten Anfrage abgemeldet.

## Deaktivieren und löschen

**Deaktivieren** nimmt dem Benutzer alle Rechte und meldet ihn ab; **Aktivieren** gibt die Rechte zurück. Du kannst dich nicht selbst deaktivieren ([`users.user.self`](/de/docs/reference/errors/#users.user.self)), und der letzte Administrator kann weder deaktiviert noch gelöscht werden noch das Recht verlieren ([`users.user.last_admin`](/de/docs/reference/errors/#users.user.last_admin)).

**Löschen** verlangt die Eingabe des Benutzernamens und fragt, was mit dem Home-Ordner des Benutzers passiert: **Behalten** (die Vorgabe), **Dateien in einen freigegebenen Ordner verschieben** oder **Mit allen Dateien löschen**, siehe [Home-Ordner](/de/docs/admin/home-folders/#wenn-ein-benutzer-gelöscht-wird). Das Konto verschwindet, Dateien, die der Benutzer in freigegebenen Ordnern angelegt hat, bleiben, und die Benutzer-ID wird nie an jemand anderen vergeben. Rechte, Gruppenmitgliedschaften sowie die Jobs und Benachrichtigungen des Benutzers werden entfernt.

Um ein Passwort zurückzusetzen, nutze **Neues Passwort** beim Benutzer: Der Benutzer wird überall abgemeldet, und SMB und SFTP verwenden ebenfalls das neue Passwort. Pithyx hat kein Zurücksetzen per E-Mail. Hat der einzige Administrator sein Passwort vergessen, ist der Wiederherstellungsschlüssel aus der Ersteinrichtung der einzige Weg, siehe [Sicherheit](/de/docs/admin/security/#der-wiederherstellungsschlüssel).

## Home-Ordner

Jeder Benutzer bekommt einen privaten Home-Ordner, ein Dataset `<Home-Pool>/pithyx/homes/<benutzer>` unter `/<pool>/homes/<benutzer>`, im Besitz des Benutzers. Die Liste zeigt seinen Pfad oder „Noch kein Pool“, bis es einen Pool mit dem Pithyx-Layout gibt. Sein Pool, die Quotas und die Homes gelöschter Benutzer stehen unter [Home-Ordner](/de/docs/admin/home-folders/).
