---
title: 'Das Manifest'
description: 'Jedes Feld von pithyx.json: Identität, Fenster, Berechtigungen, Backend und Lebenszyklus.'
status: ready
audience: developer
sidebar:
  order: 3
---

`pithyx.json` im Wurzelverzeichnis des Pakets beschreibt eine App: wer sie ist, welches Fenster sie öffnet, welche Berechtigungen sie anfordert, welche Ordner sie haben will und, falls sie eines hat, wie ihr Backend läuft. Das JSON-Schema ist `sdk/schema/manifest.v1.schema.json` (veröffentlicht als `@pithyx/sdk/schema/manifest.v1.schema.json`); unbekannte Felder sind Fehler. Das Schema ist ein stabiler Vertrag: Ein neues optionales Feld ist eine Minor-Änderung, alles andere eine Major-Änderung.

Pflichtfelder: `manifestVersion`, `id`, `version`, `name`, `author`, `entry`, `sdk`, `permissions` und `window`. Optional: `$schema`, `description`, `license`, `homepage`, `pithyx`, `dataVersion`, `storage`, `fileHandlers` und `backend`. Siehe auch die [Schema-Referenz](/de/docs/reference/manifest-schema/) mit jedem Feld und die [Compose-Regeln](/de/docs/reference/compose-rules/) für das Backend.

```json
{
  "$schema": "./node_modules/@pithyx/sdk/schema/manifest.v1.schema.json",
  "manifestVersion": 1,
  "id": "com.example.notes",
  "version": "1.0.0",
  "name": { "en": "Notes", "de": "Notizen" },
  "author": { "name": "Example Author" },
  "entry": "frontend/index.html",
  "sdk": "^1.0.0",
  "permissions": ["notifications"],
  "window": {
    "instances": "single",
    "defaultSize": { "width": 800, "height": 600 },
    "minSize": { "width": 400, "height": 300 }
  }
}
```

## Identität

- `manifestVersion`: die Zahl `1`.
- `id`: siehe [App-IDs](/de/docs/developers/apps/overview/#app-ids).
- `version`: SemVer 2.0 ohne Build-Metadaten, höchstens 64 Zeichen (`1.2.0`, `2.0.0-beta.1`).
- `name`: ein Objekt, dessen Schlüssel Sprach-Tags sind (`en`, `de`, `pt-BR`). `en` ist Pflicht; jeder Text hat 1 bis 60 Zeichen in einer Zeile. Der Desktop nimmt die Sprache des Benutzers, dann deren Basissprache, dann `en`.
- `description`: optional, dieselben Schlüssel, 1 bis 500 Zeichen, Zeilenumbrüche erlaubt.
- `author`: `name` (1 bis 100 Zeichen) und eine optionale `url` (http oder https).
- `license`: optional, ein SPDX-Ausdruck. Kataloge verlangen ihn.
- `homepage`: optional, eine http- oder https-URL.
- `entry`: die HTML-Seite, `frontend/<path>.html`, höchstens 200 Zeichen, kein Segment, das mit einem Punkt beginnt.
- `sdk`: Pflicht, ein Versionsbereich für das SDK im npm-Stil, zum Beispiel `^1.0.0`. Die Box prüft die Syntax ([`app.manifest.sdk_invalid`](/de/docs/reference/errors/#app.manifest.sdk_invalid)); mit dem Protokoll des Desktops vergleicht sie ihn heute nicht. Eine App aus der Zeit der Release Candidates kann `^1.0.0-rc.8` behalten: Dieser Bereich umfasst auch 1.0.0 und jede spätere 1.x.
- `pithyx`: optional, ein Bereich der Box-Versionen, mit denen die App funktioniert. Die Box installiert die App nur, wenn ihre eigene Version darin liegt. Kataloge verlangen ihn. Vorabversionen werden nach der einfachen SemVer-Reihenfolge verglichen, also schließt `>=0.1.0` die Version `0.2.0-beta.1` ein, `^0.1.0` aber nicht.
- `dataVersion`: optional, eine ganze Zahl von 1 bis 2147483647. Ein Downgrade aus einem Katalog ist nur zwischen Versionen mit derselben `dataVersion` erlaubt. Ein Rollback auf die vorherige Version nach einem Update hängt nicht davon ab.

## Fenster

`window` ist Pflicht:

- `instances`: `single` fokussiert das vorhandene Fenster des Benutzers auf diesem Gerät und übergibt Startargumente; `multi` öffnet jedes Mal ein neues Fenster.
- `defaultSize` und `minSize`: `width` und `height` als ganze Zahlen von 100 bis 8192 CSS-Pixel. `minSize` darf in keiner Richtung größer sein als `defaultSize`. Die Standardgröße gilt, wenn der Benutzer keine gemerkte Größe hat; das Minimum gilt auf Bildschirmen, auf die es passt.
- `loading` (optional): `text`, die Zeile, die ein neues Fenster zeigt, während das Backend startet, übersetzt wie `name` (bis zu 60 Zeichen; ohne sie schreibt der Desktop "<Name> wird gestartet ..."), und `untilReady`, ob der Platzhalter nach dem Verbinden der App auf `app.window.ready()` wartet. Siehe [Ereignisse und Kontext](/de/docs/sdk/guides/events-and-context/#laden).
- `suggestAtSignIn` (optional, Standard false): die App empfiehlt sich zum Öffnen nach der Anmeldung, für eine App, die man den ganzen Tag offen hat, etwa einen Chat. Der Desktop markiert sie dort als empfohlen, wo Benutzer die Apps nach der Anmeldung wählen (Benutzermenü, **Beim Anmelden öffnen**); von selbst öffnet er sie nie.

`fileHandlers` (optional, bis zu 32) lässt die App bei Dateitypen unter "Öffnen mit …" erscheinen. Jeder Handler hat `extensions` (1 bis 32 Erweiterungen in Kleinbuchstaben ohne Punkt, etwa `png` oder `tar.gz`), und optional `mimeTypes` (1 bis 32) und `isDefault`. Mit `isDefault` öffnet ein Doppelklick in Dateien den Typ in deiner App, solange keine andere installierte App `isDefault` dafür setzt und der Benutzer nicht mit ".x-Dateien immer so öffnen" anders gewählt hat. Eine Erweiterung kann nur zu einem Handler gehören. Die Datei erreicht die App über das Ereignis `launch` oder `openFile`.

## Berechtigungen

`permissions` ist eine Pflichtliste (sie kann leer sein) eindeutiger Werte aus dieser Menge:

| Berechtigung       | Erlaubt der App                                                                                                                                           |
| ------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `files.read`       | Dateien lesen, die der Benutzer mit ihr öffnet oder in einem Dateidialog des Desktops wählt                                                               |
| `files.write`      | diese Dateien schreiben und neue Dateien über den Speichern-Dialog des Desktops speichern                                                                 |
| `files.shares`     | alle freigegebenen Ordner durchsuchen, auf die der Benutzer zugreifen darf, mit dessen eigenen Rechten                                                    |
| `notifications`    | Benachrichtigungen anzeigen                                                                                                                               |
| `directory.read`   | alle Benutzer und Gruppen auflisten                                                                                                                       |
| `network.internet` | ausgehende Verbindungen ins Internet aus ihrem Fenster (`connect-src https:`) und ihrem Backend aufbauen                                                  |
| `network.lan`      | ausgehende Verbindungen aus ihrem Backend zu anderen Geräten im lokalen Netzwerk aufbauen (private, geteilte und Link-local-Adressen); nie zur Box selbst |

Ein Administrator bestätigt die Liste bei der Installation und kann jede Berechtigung später entziehen; die App bekommt dann ein `permissionsChange`-Ereignis. Eine Berechtigung, die in einem Update neu ist, wartet auf den Administrator, und eine entzogene Berechtigung bleibt über Updates hinweg entzogen. Ein Aufruf ohne die nötige Berechtigung schlägt mit [`app.permission.denied`](/de/docs/reference/errors/#app.permission.denied) fehl.

Eine App, die `directory.read` verlangt, einen Container als root betreibt (`backend.root`) oder Sonderrechte verlangt (`backend.elevated`), wird nur für Administratoren vorgeschlagen: Der Installationsdialog wählt **Nur Administratoren** vor und sagt warum. Der Administrator kann sie trotzdem allen erlauben.

`storage` (optional, bis zu 16) fordert Ordner an. Jede Anforderung hat eine `id`, einen `name`, eine optionale `description`, `access` (`read` oder `readWrite`), `required` und, für Backends, `services` (die Services, die den Ordner bekommen). Ein Administrator ordnet jede Anforderung einem Ordner in einem freigegebenen Ordner zu. Das Fenster adressiert ihn in der Datei-API des SDK als `{ storage: id }`; ein Backend-Service sieht ein Compose-Volume mit diesem Namen. Eine App kann erst starten, wenn ihre erforderlichen Anforderungen zugeordnet sind.

## Backend

`backend` ist optional. Darin sind `compose`, `endpoints` und `lifecycle` Pflicht.

- `compose`: Pfad der Compose-Datei im Paket, meist `backend/compose.yaml`.
- `endpoints`: ein Objekt vom Endpunkt-Namen auf `{ "service": "...", "port": 8080 }`, bis zu 16. Der Endpunkt `api` ist Pflicht und der Standard. Namen entsprechen `^[a-z][a-z0-9-]{0,31}$`, haben also höchstens 32 Zeichen; das ist die Grenze, die die Box durchsetzt. Das SDK akzeptiert längere Namen, die Box würde sie aber ablehnen. `port` (1 bis 65535) ist der Port des Containers im App-Netzwerk und wird nie veröffentlicht.
- `lifecycle`: `always` oder `onDemand`.
- `idleTimeout`: Sekunden von 60 bis 86400, Standard 600; bei `always` wird er ignoriert.
- `root`: `true` führt die Services im Container als root aus (auf dem Host ohne Rechte); Standard `false`. Der Installationsdialog warnt davor. Services ohne root verlieren jede Linux-Capability.
- `network`: `{ "services": ["fetcher"] }` nennt die Services, die das Netzwerk aus der Box hinaus bekommen, das `network.internet` und `network.lan` öffnen; die anderen bleiben im internen Netzwerk der App. Ohne die Angabe bekommt es jeder Service. Jeder Name muss ein Service der Compose-Datei sein ([`app.compose.reference_unknown`](/de/docs/reference/errors/#app.compose.reference_unknown)).
- `health`: `{ "endpoint": "api", "path": "/health" }`. Der Daemon ruft dort `GET` auf; eine 2xx-Antwort bedeutet, dass das Backend läuft.
- `schedules`: bis zu 16 Einträge der Form `{ id, cron, endpoint, path }`. Der Daemon startet das Backend und ruft `POST path` mit `X-Pithyx-Schedule` auf (siehe [Wer ruft auf](/de/docs/developers/apps/backend/#wer-ruft-auf)). Der Cron-Ausdruck hat fünf Felder (Minute 0 bis 59, Stunde 0 bis 23, Tag des Monats 1 bis 31, Monat 1 bis 12, Wochentag 0 bis 7) in der Zeitzone der Box, mit `*`, Zahlen, Bereichen, Listen und Schritten; Namen sind nicht erlaubt ([`app.manifest.cron_invalid`](/de/docs/reference/errors/#app.manifest.cron_invalid)).
- `elevated`: eine Liste zusätzlicher Rechte (`host.network`, `host.pid`, `device:/dev/<path>`, `capability:<NAME>`, `docker.socket`, `host.full`), bis zu 32. Erhöhte Rechte werden noch nicht vergeben: Die Box in ihrem ersten Release (Pithyx 1.0.0, nicht die Version der npm-Pakete) lehnt jeden Eintrag mit [`app.elevated.unsupported`](/de/docs/reference/errors/#app.elevated.unsupported) ab.

Über das Schema hinaus prüft die Box, dass Service- und Endpunkt-Namen existieren ([`app.manifest.reference_unknown`](/de/docs/reference/errors/#app.manifest.reference_unknown)) und dass die Speicheranforderungen zu den Compose-Mounts passen.

## Lebenszyklus

`lifecycle` bestimmt, wann das Backend läuft. `always` startet es, sobald es laufen kann (die App ist aktiviert, der Pool verfügbar, erforderliche Ordner sind zugeordnet). `onDemand` läuft, solange ein Fenster offen ist oder eine Anfrage oder ein Zeitplan es hält, und stoppt `idleTimeout` Sekunden nach dem letzten davon. Die Details stehen unter [Backends](/de/docs/developers/apps/backend/#lebenszyklus).

## Versionen

Die Box vergleicht die installierte mit der hochgeladenen Version und zeigt Installation, Update, Neuinstallation oder Downgrade an. Eine Neuinstallation derselben Version ersetzt deren Dateien; bei einer älteren Version warnt die Box, dass die Daten vielleicht nicht passen. Die Box behält die vorherige Version und einen Snapshot der App-Daten bis zum nächsten Update, damit ein Administrator zurückrollen kann. In einem Katalog wird eine veröffentlichte Version nie geändert oder entfernt.
