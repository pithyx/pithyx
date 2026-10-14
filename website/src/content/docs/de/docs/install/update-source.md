---
title: 'Die Update-Quelle wechseln (Forks)'
description: 'Wie eine Box den Updates eines Forks statt dem Pithyx-Projekt folgt, was das bedeutet, der Weg zurück und was ein Fork veröffentlichen muss.'
status: ready
audience: admin
sidebar:
  order: 6
---

Eine Pithyx-Box bezieht ihre Updates aus genau einer Update-Quelle, standardmäßig vom Pithyx-Projekt. Pithyx ist freie Software, deshalb darf eine Box stattdessen einem Fork folgen, aber nur durch eine bewusste Handlung an der Konsole der Box: Das Recovery-Menü ist der einzige Ort, der die Update-Quelle und die Schlüssel wechselt, denen die Box vertraut, nie die Einstellungen, die API oder eine hochgeladene Datei. Ein übernommenes Administratorkonto kann die Updates einer Box also nicht umleiten. Nach einem Wechsel vertraut die Box nur noch den Schlüsseln der neuen Quelle, bis jemand im selben Menü zurückwechselt.

## Für Administratoren

### Was ein Wechsel bedeutet

- Wer die Updates der Quelle signiert, kann alles auf der Box ändern: Ein Update läuft als root. Wechsle nur zu einer Quelle, der du vertraust.
- Solange die Box einer anderen Quelle folgt, lehnt sie Updates des Pithyx-Projekts ab.
- Der Fork kann das System und seine Datenbank so ändern, dass der Weg zurück zum Pithyx-Projekt eine Neuinstallation braucht. Ein Pithyx-Daemon lehnt eine Datenbank mit Schemaversionen ab, die er nicht kennt.
- Pools und ihre Dateien fasst der Wechsel nie an.
- Der Wechsel liegt auf der Systemplatte. Eine Rückkehr zu einem Snapshot von vor dem Wechsel kehrt auch zur Quelle dieses Snapshots zurück. Er ist nicht Teil der Konfigurationssicherung.

### Wechseln

Du brauchst die `pithyx-source.json` der Quelle, die der Fork zusammen mit seinen öffentlichen Schlüsseln veröffentlicht. Das Menü nimmt sie auf einem von drei Wegen:

- **Aus den Einstellungen**: Einstellungen > **Updates**, **Update-Quelle**, **Quelldatei für das Recovery-Menü speichern**. Die Box prüft die Datei und behält sie, aber es ändert sich nichts, bis sie an der Konsole gewählt wird. Das hilft, wenn du die Konsole nur über die Fernwartung erreichst. **Entfernen** vergisst die gespeicherte Datei.
- **Von einem USB-Stick**: Leg die Datei (`pithyx-source.json` oder ein Name, der mit `pithyx-source` beginnt und auf `.json` endet) in den obersten Ordner des Sticks. Die virtuellen Medien eines BMC gehen auch.
- **Getippt**: Name, Kanal-URL und jeder Schlüssel mit seiner Id und seinen Kanälen.

Dann:

1. Starte die Box neu und wähle im Bootmenü **Pithyx recovery** (Taste `r`).
2. Wähle **[4] Change the update source**, dann **[1] Read pithyx-source.json from a USB stick**, **[2] Use the file saved in Settings** oder **[3] Type the source**.
3. Das Menü zeigt den Namen, die Kanal-URL und jeden öffentlichen Schlüssel vollständig. Vergleiche jeden Schlüssel mit dem, den die Quelle veröffentlicht.
4. Tippe den Namen der Quelle, um zu wechseln. Alles andere ändert nichts.
5. Starte mit **[0] Restart** neu. Ab diesem Start folgt die Box der neuen Quelle.

Nach dem Neustart bekommen die Administratoren die Benachrichtigung „Die Update-Quelle ist jetzt {name}“, und Einstellungen > **Updates** zeigt die Quelle mit dem Hinweis „Diese Box folgt {name}, nicht dem Pithyx-Projekt.“ Wenn bei dir niemand gewechselt hat, wechsle sofort zurück.

Eine Datei, die die Prüfung nicht besteht, wird mit [`update.source.invalid`](/de/docs/reference/errors/#update.source.invalid) abgelehnt.

### Der Weg zurück

Wähle im Recovery-Menü **[4] Change the update source**, dann **[4] Back to the Pithyx project**, und tippe `Pithyx`. Nach dem Neustart vertraut die Box wieder nur den eingebauten Schlüsseln des Projekts und liest seine Releases. Updates installieren sich nur, wenn sie neuer als die installierte Version sind: Eine Box, die einen Fork mit höherer Versionsnummer hatte, wartet also auf ein neueres Pithyx-Release, und ein Fork, der die Datenbank geändert hat, kann eine Neuinstallation nötig machen (siehe [Wiederherstellung und Neuinstallation](/de/docs/install/recovery/)).

## Für Betreiber eines Forks

Eine Box, die zu deinem Fork wechselt, liest deine Kanäle genau so wie die des Projekts: dieselben Dateiformate, dieselben Prüfungen und dieselben Regeln. Du veröffentlichst drei Dinge.

### 1. Einen Signaturschlüssel

`tools/release/create-key.sh <key id> <file>` erzeugt einen Ed25519-Schlüssel, schreibt den privaten Teil als PEM mit Modus 600 und gibt den öffentlichen Teil aus. Halte den privaten Schlüssel aus dem Repository und aus Logs heraus. Eine Schlüssel-Id besteht aus Kleinbuchstaben, Ziffern, `.` und `-`, höchstens 64 Zeichen. Eine Quelle darf bis zu vier Schlüssel nennen, jeden für `stable`, `beta` oder beide, so kannst du einen Reserveschlüssel halten oder wechseln: `tools/release/sign-update.sh --append` signiert mit einem zweiten Schlüssel und behält die erste Signatur.

### 2. Update-Dateien und Kanäle

Baue und signiere sie mit den Release-Skripten des Repositorys:

1. `image/build.sh --version <version>` baut das Image und die Liste seiner Pakete.
2. `tools/release/build-update.sh --version <version> --channel stable|beta [--minimum <version>] [--since <packages file> | --full]` baut `pithyx-<version>-amd64.pithyxupdate` (unsigniert). Sie enthält jedes Paket, dessen Version von der Paketliste der Mindestversion abweicht (`image/build.sh` schreibt sie als `image/out/pithyx-<version>-amd64.packages`; das Skript sucht die Liste der Mindestversion in `image/baseline/pithyx-<minimum>-amd64.packages`, einem Ordner, den es im Repository noch nicht gibt, also nennt `--since` eine direkt), bewahre also die Paketliste jedes Releases auf, das du veröffentlichst. `--full` nimmt jedes Paket auf.
3. `tools/release/sign-update.sh <file> --key <pem file> --key-id <id>` signiert das Manifest und prüft die Datei so, wie eine Box es tut.
4. `tools/release/build-channel.sh <file> --key <pem file> --key-id <id> --sequence <n> [--url <url>]` schreibt und signiert `channel.json` und `channel.json.sig`.

Veröffentliche `channel.json` und `channel.json.sig` unter deiner Kanal-URL, mit `stable` oder `beta` anstelle von `{channel}`, und die Update-Datei dort, wohin `channel.json` zeigt (ihre URL ist relativ zu `channel.json`). Mit GitHub-Releases passen die Vorgaben von `build-channel.sh` zum Aufbau des Projekts: `channel.json` auf einem Release `channel-<channel>`, die Datei auf `beta-<version>` oder `v<version>`. Alles muss über https ohne Zugangsdaten erreichbar sein.

Die Regeln, die die Box anwendet:

- **Versionen** sind SemVer. Eine Box installiert nur eine Version, die neuer als ihre installierte ist, also muss dein erstes Release neuer sein als die Pithyx-Version auf den Boxen, die zu dir wechseln.
- **Mindestversion**: Eine Box, die älter als die Mindestversion einer Datei ist, lehnt sie ab und braucht zuerst eine Version dazwischen.
- **Sequenz**: Jede Veröffentlichung eines Kanals erhöht `--sequence` um mindestens eins. Eine Box lehnt eine niedrigere Sequenz ab, als sie gesehen hat. Nach einem Wechsel vergisst die Box die Sequenzen der alten Quelle, deine Kanäle dürfen also bei 1 beginnen.
- **Kanäle**: Eine stabile Box installiert nur stabile Dateien, eine Beta-Box beide. Ein Schlüssel signiert nur für die Kanäle, die die Quelle für ihn nennt.
- **Datenbank**: Bringt deine Version Datenbank-Migrationen mit, kann eine Box, die später zum Pithyx-Projekt zurückgeht, eine Neuinstallation brauchen. Sag das deinen Nutzern.

### 3. `pithyx-source.json`

```sh
node tools/release/update-file.ts source --name "<name>" --url "https://<host>/<path>/channel-{channel}/channel.json" \
  --key <key id>:<public key>:stable,beta > pithyx-source.json
node tools/release/update-file.ts verify-source pithyx-source.json
```

Die Datei hat `sourceVersion` 1, einen `name` aus 1 bis 64 druckbaren ASCII-Zeichen (er wird an der Konsole zur Bestätigung getippt), eine https-`url` von höchstens 512 Zeichen mit `{channel}` und ein bis vier `keys`, jeder mit `keyId`, `publicKey` (32 Bytes in Base64) und `channels`. Die Box lehnt unbekannte Felder, Dateien über 64 KB und eine doppelte Schlüssel-Id ab. Das Schema ist `tools/release/schema/update-source.v1.schema.json`.

Veröffentliche die Datei zusammen mit den öffentlichen Schlüsseln an einem Ort, den deine Nutzer prüfen können, damit sie die Schlüssel vergleichen können, die die Konsole zeigt.
