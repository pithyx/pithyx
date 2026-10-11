---
title: 'Systemupdates'
description: 'Update-Kanäle, wie ein Update geprüft und eingespielt wird und was passiert, wenn eines scheitert.'
status: ready
audience: admin
sidebar:
  order: 5
---

Pithyx aktualisiert sich aus signierten Update-Dateien: Einmal am Tag prüft es, ob sein Kanal eine neuere Version hat, und benachrichtigt die Administratoren, installiert aber nie von selbst. Vor jedem Update macht die Box einen Snapshot des Systems, und nach dem Neustart prüft sie die neue Version bis zu fünf Minuten lang; schlägt diese Prüfung fehl, kehrt sie von selbst zum Snapshot zurück. Updates findest du unter Einstellungen > **Updates** (nur Administratoren). Pools und ihre Dateien fasst weder ein Update noch eine Rückkehr an.

Updates gelten nur für eine Box, die mit dem Pithyx-Installer installiert wurde. Auf einer anders aufgebauten Maschine kann Einstellungen > **Updates** nach Updates suchen, sie aber nicht installieren oder zurückkehren ([`update.system.unsupported`](/de/docs/reference/errors/#update.system.unsupported)).

## Kanäle

Unter **Einstellungen** auf der Update-Seite wählst du den **Kanal**:

- **Stabil** bekommt nur Releases.
- **Beta** bekommt neue Versionen früher, zum Testen. Eine Beta-Box installiert auch stabile Versionen.

**Täglich nach Updates suchen** ist standardmäßig an. Die Prüfung liest die kleine `channel.json` des Kanals von der Update-Quelle und die Paketlisten von Debian und benachrichtigt dann die Administratoren („Pithyx {version} ist verfügbar“ und eine eigene Meldung, wenn Debian Sicherheitskorrekturen hat). Jede Box wählt ihre eigene zufällige Tageszeit für die Prüfung; **Nächste Prüfung** zeigt sie. **Jetzt prüfen** führt sie sofort aus. Schaltest du die tägliche Prüfung aus, entfällt nur die automatische Prüfung, nicht **Jetzt prüfen**.

Standardmäßig ist die Quelle die Releases des Pithyx-Projekts auf GitHub. Nur das Recovery-Menü an der Konsole kann auf eine andere Quelle wechseln, zum Beispiel auf einen Fork; siehe [Die Update-Quelle wechseln](/de/docs/install/update-source/).

## Prüfen und installieren

Hat der Kanal eine neuere Version, zeigt die Seite „Pithyx {version} ist verfügbar“ mit **Was sich ändert** (dem Changelog) und der Zahl der Debian-Updates. **{version} installieren** öffnet einen Dialog:

1. Die Box macht einen Snapshot des Systems, damit du immer zur jetzigen Version zurückkannst.
2. Sie installiert das Update und, wenn du es ankreuzt, die Debian-Updates im selben Snapshot.
3. Sie schreibt den Bootloader auf jede Systemplatte und startet neu. Während des Neustarts ist die Box nicht erreichbar.
4. Nach dem Neustart prüft sie die neue Version und behält sie oder kehrt zum Snapshot zurück.

Bestätige mit **Installieren und neu starten**. Der Fortschritt zeigt die Schritte Herunterladen, Snapshot, Installieren, Debian-Updates, Bootloader sowie Neustart und Prüfung; die Seite verbindet sich nach dem Neustart von selbst neu, und der **Verlauf** zeigt das Ergebnis.

Was eine Update-Datei mitbringt:

- Die Pithyx-Pakete, den Kernel und das ZFS-Modul. Kernel und ZFS ändern sich nur zusammen durch ein Pithyx-Update, nie durch Debian allein.
- Jedes andere Paket des Images, das sich seit der Mindestversion der Datei geändert hat. Eine Datei aktualisiert jede Box ab dieser Mindestversion, ohne die Versionen dazwischen.
- Debians Sicherheitsupdates sind nicht Teil der Datei. Die Box holt sie von Debian; die Seite zählt sie, und der Installationsdialog bietet sie an.

**Ohne Internet** nimmst du **Aus einer Datei installieren**: **Update-Datei hochladen** nimmt eine `.pithyxupdate`-Datei von höchstens 4 GB. Die Box prüft die ganze Datei, bevor sich etwas ändert, und zeigt dann Version, Kanal, Zahl der Pakete und Kernel. Installiere sie mit **{version} installieren** oder wirf sie mit **Verwerfen** weg. Die Datei bleibt bis zum nächsten Hochladen oder einem Neustart des Daemons. Das Recovery-Menü kann eine Datei auch von einem USB-Stick installieren (unten).

Regeln, die jedes Update befolgt:

- Eine Datei, die älter als die installierte Version oder gleich alt ist, wird abgelehnt ([`update.file.older`](/de/docs/reference/errors/#update.file.older)). Eine ältere Version kommt nur durch eine Rückkehr zurück.
- Braucht eine Version eine neuere Mindestversion als die installierte, heißt es „Diese Version braucht mindestens Pithyx {minimum}“. Installiere dann zuerst eine Version dazwischen aus einer Update-Datei ([`update.file.minimum`](/de/docs/reference/errors/#update.file.minimum)).
- Eine stabile Box installiert nur stabile Dateien ([`update.file.channel`](/de/docs/reference/errors/#update.file.channel)).
- Die Systemplatte braucht genug freien Platz ([`update.space.insufficient`](/de/docs/reference/errors/#update.space.insufficient)).
- Es läuft immer nur ein Update oder eine Rückkehr gleichzeitig ([`update.apply.running`](/de/docs/reference/errors/#update.apply.running)).

## Signaturen

Jede Update-Datei und jede `channel.json` ist mit Ed25519 signiert. Die Box prüft:

- die Signatur der Kanaldatei und des Manifests der Update-Datei gegen die öffentlichen Schlüssel, die in Pithyx eingebaut sind: Dem Release-Schlüssel vertraut sie auf beiden Kanälen, dem Beta-Schlüssel nur auf Beta;
- Größe und SHA-256 jedes Pakets in der Datei, und dass die Datei nichts enthält, was ihr Manifest nicht nennt;
- dass eine Kanaldatei nicht älter ist als eine, die sie schon gesehen hat, damit sich keine alte `channel.json` wieder einspielen lässt ([`update.channel.sequence_old`](/de/docs/reference/errors/#update.channel.sequence_old));
- dass eine heruntergeladene Datei genau die ist, die der Kanal nennt ([`update.download.mismatch`](/de/docs/reference/errors/#update.download.mismatch)).

Eine Datei oder ein Kanal mit fehlender, beschädigter oder nicht vertrauenswürdiger Signatur wird abgelehnt, und nichts ändert sich ([`update.file.signature_invalid`](/de/docs/reference/errors/#update.file.signature_invalid), [`update.channel.signature_invalid`](/de/docs/reference/errors/#update.channel.signature_invalid)). Unsignierte Dateien sind Entwicklungsdateien, die nur eine Entwicklungsmaschine annimmt.

## Wenn ein Update scheitert

- **Vor dem Snapshot** (Herunterladen, Prüfungen, Platz): Nichts hat sich geändert. Der Verlauf zeigt „Fehlgeschlagen, nichts geändert“.
- **Nach dem Snapshot**: Die Box kehrt von selbst zum Snapshot zurück. Nach dem Neustart wartet eine Prüfung bis zu fünf Minuten auf den Daemon, seine Datenbank-Migrationen, die API, den Desktop und die Pools, die vorher importiert waren. Schlägt eines davon fehl, stellt die Box den Snapshot wieder her und startet neu. Kommt das neue System nicht einmal so weit, fällt der nächste Start über das Bootmenü auf die vorherige Version zurück. In beiden Fällen bekommen die Administratoren „Update auf {version} fehlgeschlagen“, und der Verlauf zeigt „Fehlgeschlagen, vorherige Version zurück“.

Der Fehler [`update.apply.failed`](/de/docs/reference/errors/#update.apply.failed) nennt den Schritt und den Befehl, der gescheitert ist. Die Details stehen im Log Center.

## Snapshots und Rückkehr

Vor jedem Update wird ein Snapshot des Systems gemacht; die letzten drei bleiben. **Snapshots** auf der Update-Seite listet sie auf, und **Zu dieser Version zurück** stellt einen wieder her: Gib die Version zur Bestätigung ein und wähle **Zurück und neu starten**. Seit dem Snapshot geänderte Einstellungen gehen verloren (Benutzer, freigegebene Ordner, App-Einstellungen). Pools und ihre Dateien bleiben, wie sie sind.

Das Bootmenü hat für jeden behaltenen Snapshot einen Eintrag (Tasten 1 bis 3, der neueste zuerst). Er startet diesen Snapshot schreibgeschützt in das Recovery-Menü, das die Rückkehr zu ihm anbietet.

## Das Recovery-Menü

Der Bootmenü-Eintrag **Pithyx recovery** (Taste `r`) startet ein Menü auf dem Bildschirm der Box. Es ist auf Englisch:

- **[1] Roll back to a snapshot**: zu einem Snapshot zurück, bestätigt durch Eingabe der Version.
- **[2] Install an update file from a USB stick**: Die Datei muss im obersten Ordner des Sticks liegen. Das Menü prüft sie wie ein Hochladen und installiert, nachdem du `install` eingibst.
- **[3] Reset the network to DHCP**: jede Netzwerkkarte zurück auf DHCP, Bonds, VLANs und Nameserver entfernt, wirksam beim nächsten Start.
- **[5] Change the update source**: siehe [Die Update-Quelle wechseln](/de/docs/install/update-source/).
- **[4] Restart**: Neustart.

Das Menü hat kein Passwort. Wer die Konsole der Box bedienen kann, vor Ort oder über die Fernwartung (BMC, IPMI), hat die Box ohnehin in der Hand; siehe [Sicherheit](/de/docs/admin/security/#physischer-zugriff). Sichere die Konsole und jede Fernwartung der Maschine. Jede Aktion des Menüs wird protokolliert.
