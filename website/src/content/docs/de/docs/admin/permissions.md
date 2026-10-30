---
title: 'Berechtigungen'
description: 'Wie Rechte an Benutzer und Gruppen vergeben werden, wie sich der Zugriff auf freigegebene Ordner, Protokolle und Apps verbindet und wann eine Änderung wirkt.'
status: ready
audience: admin
sidebar:
  order: 6
---

Pithyx vergibt Rechte an Benutzer und an Gruppen. Die wirksamen Rechte eines Benutzers sind die Vereinigung seiner eigenen Rechte und der Rechte aller seiner Gruppen, einschließlich der eingebauten Gruppe users. Es gibt keine Verbotsregeln, und Administratoren haben jedes Recht. Ein deaktivierter Benutzer hat keines.

## Das Modell

Ein Recht ist eines von:

- ein Sonderrecht: Administrator, am Desktop anmelden, über SSH mit einer Shell anmelden, neu starten und herunterfahren;
- ein Protokoll: SMB, SFTP, WebDAV oder FTP;
- Zugriff auf einen freigegebenen Ordner: Kein Zugriff, Lesen oder Lesen und schreiben (das höchste gewinnt);
- Zugriff auf eine App.

Der Rechte-Dialog eines Benutzers oder einer Gruppe sagt es deutlich: Der Benutzer hat diese Rechte und alles, was seine Gruppen ihm geben; es gibt keine Regeln, die ein Recht wegnehmen. Eine Gruppe überstimmt nie einen Benutzer, und was eine Gruppe gewährt, kannst du nicht verbieten.

## Zugriff auf freigegebene Ordner

Lege die Stufen **Kein Zugriff**, **Lesen** und **Lesen und schreiben** pro Benutzer oder Gruppe im Rechte-Dialog fest oder pro Ordner im Bearbeiten-Dialog des Ordners, der auch zeigt, was ein Benutzer am Ende „bekommt“. Administratoren lesen und schreiben immer jeden Ordner. Pithyx bringt das Dateisystem damit in Einklang: Es legt pro Ordner Linux-Gruppen an und setzt POSIX-ACLs, sodass SMB, SFTP, WebDAV, FTP und die App „Dateien“ dieselben Rechte sehen. ACL-Änderungen von einem SMB-Client werden ignoriert. Ein Ordner ohne Zugriff ist versteckt, nicht verweigert ([`fs.share.not_found`](/de/docs/reference/errors/#fs.share.not_found)); Schreiben mit Leserecht ergibt [`fs.share.read_only`](/de/docs/reference/errors/#fs.share.read_only).

## Zugriff über Protokolle

Ein Benutzer erreicht einen Ordner nur dann über ein Protokoll, wenn beide es erlauben: Der Benutzer (oder eine Gruppe) hat das Protokollrecht, und der Ordner hat den passenden Schalter **Über ... anbieten**. Ein neuer Benutzer hat nur SMB. Die serverseitigen Schalter stehen unter [Dateidienste](/de/docs/admin/file-services/). SSH mit einer Shell ist ein Recht, kein Protokoll: Es braucht das Recht und den Schalter **SSH-Anmeldungen**.

Anmeldungen über Protokolle verwenden nur das Passwort; die Zwei-Faktor-Anmeldung gilt für den Web-Desktop.

## Zugriff auf Apps

Dateien und Betrachter werden per Recht vergeben (neue Benutzer bekommen beide), Info steht allen offen, und Einstellungen, Ressourcenmonitor, Protokoll-Center und Store sind nur für Administratoren. Bei Apps aus dem Store ist **Wer die App nutzen darf** entweder **Alle**, **Nur Administratoren** oder **Ausgewählte Gruppen und Nutzer**; Administratoren dürfen jede App nutzen. Pro App können Administratoren Berechtigungen ausschalten, um sie der App zu nehmen. Mehr dazu unter [Apps und der Store](/de/docs/admin/apps-and-store/).

## Wann eine Änderung wirkt

Änderungen gelten sofort. Offene Sitzungen werden über den WebSocket neu autorisiert, und ein Benutzer, der die Desktop-Anmeldung verliert, wird getrennt. Bei WebDAV und FTP wird das Recht bei jeder Anfrage geprüft. Ein korrektes Protokollpasswort wird 5 Minuten gemerkt, und ein neues Passwort beendet das sofort. Offene Fenster einer App bekommen eine Änderung der Berechtigungen innerhalb weniger Minuten. Websitzungen enden nach 7 Tagen ohne Nutzung oder nach insgesamt 30 Tagen; Deaktivieren, Löschen oder Zurücksetzen des Passworts beendet sie sofort.
