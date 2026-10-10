---
title: 'Glossar'
description: 'Die Wörter, die Pithyx verwendet, und was sie jeweils bedeuten.'
status: draft
sidebar:
  order: 4
---

Die Wörter, die Pithyx in seiner Oberfläche und Dokumentation verwendet, in der Reihenfolge der englischen Begriffe, also alphabetisch nach dem englischen Namen.

## Begriffe

**Administrator.** Ein Benutzer mit allen Rechten auf der Box. Administratoren verwalten Speicher, Benutzer, freigegebene Ordner, Dateidienste und Apps.

**App.** Ein statisches Frontend plus ein optionales Backend, beschrieben durch ein Manifest und ausgeliefert als Paket. Siehe [Übersicht über Apps](/de/docs/developers/apps/overview/).

**App-ID (App id).** Die umgekehrte Domain des Autors plus der App-Name, drei bis fünf Teile, etwa `com.mueller.chat`. System-Apps nutzen `org.pithyx.<name>`.

**App-Pool (App pool).** Der Pool, den ein Administrator bei der ersten Installation einer App wählt. Jede App hat dort ein Dataset, und auch die Daten von Docker liegen dort.

**Backend.** Die Container einer App, beschrieben durch eine Docker-Compose-Datei. Eine Instanz bedient alle Benutzer und ist nur über den Proxy des Daemons erreichbar.

**Box.** Die Maschine, auf der Pithyx läuft.

**Katalog (Catalog).** Ein Git-Repository mit Apps, das der Store liest. Es gibt einen offiziellen, einen Community-Katalog und beliebig viele eigene.

**Kanal (Channel).** `stable` oder `beta`: welchen Branch eines Katalogs die Box liest.

**Konfigurationsbackup (Configuration backup).** Eine mit Passwort verschlüsselte Datei mit der Datenbank der Box, der Samba-Passwortdatenbank, Zertifikaten und SSH-Host-Schlüsseln, die bei einer frischen Installation wiederhergestellt werden kann.

**Core-App (Core app).** Eine App, die mit dem System ausgeliefert wird, wie Dateien, Speicher, Einstellungen, Ressourcenmonitor, Protokoll-Center, Betrachter, Info und Store. Ihre ID beginnt mit `org.pithyx.`.

**Daemon.** `pithyxd`, der einzige privilegierte Prozess auf der Box. Er liefert den Desktop und die API aus.

**Dataset.** Ein ZFS-Dateisystem innerhalb eines Pools mit eigenem Platzlimit und eigenen Einstellungen. Jeder freigegebene Ordner und jede App hat eines.

**Deploy-Key (Deploy key).** Ein schreibgeschützter SSH-Schlüssel, den die Box erzeugt, um einen privaten Katalog abzurufen.

**Desktop.** Die Weboberfläche der Box, geöffnet in einem Browser.

**Bestätigung mit der Seriennummer (Disk serial confirmation).** Zerstörerische Speicheraktionen auf einer Platte mit Daten verlangen die Eingabe der Seriennummer der Platte als Bestätigung.

**Fehlercode (Error code).** Eine stabile Kennung der Form `area.object.reason`. Siehe die [Fehlerreferenz](/de/docs/reference/errors/).

**Gruppe (Group).** Eine Menge von Benutzern, denen dieselben Rechte gegeben werden können. Die eingebaute Gruppe `users` enthält alle.

**Hot Spare.** Eine Platte, die in einem Pool wartet und übernimmt, wenn eine andere Platte ausfällt.

**Job.** Ein lang laufender Vorgang, der im Hintergrund mit Fortschritt läuft. Jobs werden nach einem Neustart nicht fortgesetzt.

**Lifecycle.** Wie lange ein Backend läuft: `always` oder `onDemand` (solange ein Fenster offen ist, plus eine Leerlaufzeit).

**Live-System (Live system).** Die Umgebung auf dem Installations-USB-Stick, die den Web-Installer ausführt.

**Protokoll-Center (Log Center).** Die App, die die Logs des Daemons, des Systems und der Apps durchsucht.

**Manifest.** Die Datei `pithyx.json`, die eine App beschreibt. Siehe [Das Manifest](/de/docs/developers/apps/manifest/).

**Besitzerkonto (Owner account).** Das erste Administratorkonto, das im Installer angelegt wird.

**Paket (Package).** Eine `.pithyxapp`-Datei: eine App, gebaut und bereit zur Installation.

**Berechtigung (Permission).** Etwas, das eine App tun darf, etwa freigegebene Ordner lesen oder das Internet erreichen. Ein Administrator bestätigt und kann jede widerrufen. Nicht zu verwechseln mit den Rechten eines Benutzers.

**Pool.** Ein ZFS-Pool: Platten, zu einem Speicherbereich zusammengefasst, als Spiegel, RAID-Z oder Stripe.

**Protokoll (Protocol).** Ein Weg, freigegebene Ordner von anderen Geräten zu erreichen: SMB, SFTP, WebDAV oder FTP. Der Zugriff wird pro Benutzer festgelegt.

**Kontingent (Quota).** Eine Grenze für den Platz, den ein Dataset belegen darf.

**Wiederherstellungscodes (Recovery codes).** Zehn Einmalcodes, die bei der Zwei-Faktor-Anmeldung die Authenticator-App ersetzen.

**Wiederherstellungsschlüssel (Recovery key).** Ein Schlüssel, der bei der Einrichtung einmal angezeigt wird (er beginnt mit `PTHX`). Er ist der einzige Weg, das Passwort eines Administrators zurückzusetzen, wenn das kein anderer Administrator kann; ohne ihn bleibt nur eine Neuinstallation.

**Papierkorb (Recycle bin).** Ein versteckter Ordner in jedem freigegebenen Ordner, der gelöschte Dateien für eine eingestellte Zeit aufbewahrt.

**Request-ID (Request id).** Eine 32 Zeichen lange Kennung, die eine Anfrage mit ihren Log-Einträgen verbindet. Der Desktop zeigt sie nie.

**Resilver.** Daten auf einer ersetzten oder zurückgekehrten Platte neu aufbauen.

**Wiederherstellungspunkt (Restore point).** Ein automatischer täglicher Snapshot eines freigegebenen Ordners, 7 Tage aufbewahrt.

**Rollback.** Eine App samt ihren Daten auf die vorige Version zurücksetzen.

**Sandbox.** Das eingeschränkte iframe, in dem Apps von Dritten laufen.

**Scrub.** Eine Prüfung, die jeden Block eines Pools liest und Fehler aus der Redundanz repariert.

**SDK.** `@pithyx/sdk`, die API zwischen einer App und dem Desktop. Siehe [@pithyx/sdk](/de/docs/sdk/sdk/).

**Freigegebener Ordner (Shared folder).** Ein Ordner auf einem Pool, ein eigenes Dataset, den Benutzer mit gewähltem Zugriff (keiner, Lesen oder Lesen und schreiben) vom Desktop oder über ein Protokoll erreichen.

**Sideload.** Eine App aus einer Paketdatei statt aus einem Katalog installieren. Der Store kennzeichnet sie als nicht geprüft.

**Simulationsmodus (Simulation mode).** Der Daemon läuft auf Testdaten statt auf echter Hardware, für Entwicklung und Tests.

**Snapshot.** Eine schreibgeschützte Momentaufnahme eines Datasets.

**Store.** Die App, in der Administratoren Apps installieren, aktualisieren und entfernen und Kataloge verwalten.

**Systemplatte (System disk).** Die Platte, die das Betriebssystem und die Datenbank enthält. Pools und App-Daten liegen nie darauf.

**Token.** Ein kurzlebiges Geheimnis, das ein App-Fenster gegenüber dem Daemon oder einem Backend ausweist. Tickets sind Einmal-Tokens für WebSockets.

**Topic.** Ein benannter Kanal am WebSocket, der Live-Daten liefert, etwa `storage/pools`.

**Zwei-Faktor-Anmeldung (Two-factor sign-in).** Anmeldung mit einem Passwort und einem Code aus einer Authenticator-App (TOTP). Für jeden Benutzer optional.

**Update-Regel (Update policy).** Wie die Box mit App-Updates umgeht: automatisch, benachrichtigen oder nichts.

**Vdev.** Eine Gruppe von Platten innerhalb eines Pools, etwa ein Spiegel.

**Fenster-Lease (Window lease).** Das Zählen offener Fenster einer App, das ein `onDemand`-Backend am Laufen hält.
