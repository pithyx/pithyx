---
title: 'Das Manifest'
description: 'Jedes Feld von pithyx.json: Identität, Fenster, Berechtigungen, Backend und Lebenszyklus.'
status: ready
audience: developer
sidebar:
  order: 3
---

`pithyx.json` im Wurzelverzeichnis des Pakets beschreibt eine App: wer sie ist, welches Fenster sie öffnet, welche Berechtigungen sie anfordert, welche Ordner sie haben will und, falls sie eines hat, wie ihr Backend läuft. Das JSON-Schema ist `sdk/schema/manifest.v2.schema.json` (veröffentlicht als `@pithyx/sdk/schema/manifest.v2.schema.json`); unbekannte Felder sind Fehler. Das Schema ist ein stabiler Vertrag: Ein neues optionales Feld ist eine Minor-Änderung, alles andere eine Major-Änderung. Die Manifest-Version 2 (Pithyx 1.2, D388) hat Version 1 abgelöst: Eine Box lehnt ein Manifest der Version 1 mit [`app.manifest.version_unsupported`](/de/docs/reference/errors/#app.manifest.version_unsupported) ab, baue eine ältere App also mit `@pithyx/cli` 2 neu. Schon installierte Apps laufen weiter; die Box hat ihre gespeicherten Manifeste umgewandelt.

Pflichtfelder: `manifestVersion`, `id`, `version`, `name`, `author`, `entry`, `sdk`, `permissions`, `arch`, `source` und `window`. Optional: `$schema`, `description`, `license`, `homepage`, `pithyx`, `dataVersion`, `storage`, `fileHandlers`, `fileActions`, `backend`, [`notifications`](#benachrichtigungskategorien) und die [Angaben für den Store](#angaben-für-den-store) (`privacy`, `security`, `maturity`, `categories`, `keywords`, `locales`, `screenshots`, `releaseNotes`, `requirements`, `audience`). Siehe auch die [Schema-Referenz](/de/docs/reference/manifest-schema/) mit jedem Feld und die [Compose-Regeln](/de/docs/reference/compose-rules/) für das Backend.

```json
{
  "$schema": "./node_modules/@pithyx/sdk/schema/manifest.v2.schema.json",
  "manifestVersion": 2,
  "id": "com.example.notes",
  "version": "1.0.0",
  "name": { "en": "Notes", "de": "Notizen" },
  "author": { "name": "Example Author" },
  "entry": "frontend/index.html",
  "sdk": "^1.0.0",
  "arch": ["amd64", "arm64"],
  "source": "https://github.com/example/notes",
  "permissions": {
    "notifications": {
      "reason": {
        "en": "Reminds you of notes with a due date.",
        "de": "Erinnert dich an fällige Notizen."
      }
    }
  },
  "window": {
    "instances": "single",
    "defaultSize": { "width": 800, "height": 600 },
    "minSize": { "width": 400, "height": 300 }
  }
}
```

## Identität

- `manifestVersion`: die Zahl `2`.
- `id`: siehe [App-IDs](/de/docs/developers/apps/overview/#app-ids).
- `version`: SemVer 2.0 ohne Build-Metadaten, höchstens 64 Zeichen (`1.2.0`, `2.0.0-beta.1`).
- `name`: ein Objekt, dessen Schlüssel Sprach-Tags sind (`en`, `de`, `pt-BR`). `en` ist Pflicht; jeder Text hat 1 bis 60 Zeichen in einer Zeile. Der Desktop nimmt die Sprache des Benutzers, dann deren Basissprache, dann `en`.
- `description`: optional, dieselben Schlüssel, 1 bis 500 Zeichen, Zeilenumbrüche erlaubt.
- `author`: `name` (1 bis 100 Zeichen) und eine optionale `url` (http oder https).
- `license`: optional, ein SPDX-Ausdruck. Kataloge verlangen ihn.
- `homepage`: optional, eine http- oder https-URL.
- `source`: Pflicht. Die URL des Quellcode-Repositorys der App, oder `private`, wenn der Quellcode nicht öffentlich ist. Der Store zeigt bei öffentlichem Quellcode "Open Source" mit der `license` und sonst "Quellcode nicht öffentlich".
- `arch`: Pflichtliste der Prozessorarchitekturen, auf denen die App läuft, `amd64` und `arm64`. Jedes Image des Backends muss es für jede davon geben; eine App ohne Backend nennt meist beide. Eine Box mit anderer Architektur lehnt die Installation mit [`app.arch.unsupported`](/de/docs/reference/errors/#app.arch.unsupported) ab, weil die App dort nicht laufen kann. Das ist die einzige Art Ablehnung: Was eine App nur empfiehlt, ist ein Hinweis.
- `entry`: die HTML-Seite, `frontend/<path>.html`, höchstens 200 Zeichen, kein Segment, das mit einem Punkt beginnt.
- `sdk`: Pflicht, ein Versionsbereich für das SDK im npm-Stil, zum Beispiel `^1.0.0`. Die Box prüft die Syntax ([`app.manifest.sdk_invalid`](/de/docs/reference/errors/#app.manifest.sdk_invalid)); mit dem Protokoll des Desktops vergleicht sie ihn heute nicht. Eine App aus der Zeit der Release Candidates kann `^1.0.0-rc.8` behalten: Dieser Bereich umfasst auch 1.0.0 und jede spätere 1.x.
- `pithyx`: optional, ein Bereich der Box-Versionen, mit denen die App funktioniert. Die Box installiert die App nur, wenn ihre eigene Version darin liegt. Kataloge verlangen ihn. Vorabversionen werden nach der einfachen SemVer-Reihenfolge verglichen, also schließt `>=0.1.0` die Version `0.2.0-beta.1` ein, `^0.1.0` aber nicht.
- `dataVersion`: optional, eine ganze Zahl von 1 bis 2147483647. Ein Downgrade aus einem Katalog ist nur zwischen Versionen mit derselben `dataVersion` erlaubt. Ein Rollback auf die vorherige Version nach einem Update hängt nicht davon ab.

## Angaben für den Store

Optionale Felder (D389), die der Store Administratoren vor und nach der Installation zeigt. Keines ändert, was die App darf, und keines verhindert eine Installation: Was eine App empfiehlt, ist ein Hinweis.

- `privacy`: was die App aus der Box hinaus sendet, `{ "sends": ..., "note": {...} }`. `sends` ist `none` (nichts), `optional` (nur was ein Benutzer einschaltet), `telemetry` (Nutzungs- oder Absturzdaten an dich) oder `vendorAccount` (sie braucht ein Konto bei einem Dienst außerhalb der Box); `note` sagt mehr dazu, mehrsprachig wie `description`. Der Store zeigt es in einfachen Worten; ohne das Feld sagt er, dass der Autor nichts dazu sagt. Kataloge verlangen es.
- `security`: wohin man eine Sicherheitslücke meldet, eine `https`-URL oder eine `mailto:`-Adresse. `pithyx validate` warnt ohne sie, wenn die App ein Recht mit hohem Risiko oder Root-Zugriff verlangt (`cli.manifest.security_missing`).
- `maturity`: `stable` (auch ohne das Feld), `beta` oder `experimental`. Der Store zeigt die beiden letzten als Abzeichen.
- `categories`: eine bis drei aus `productivity`, `office`, `communication`, `files`, `backup`, `photos`, `media`, `home-automation`, `network`, `security`, `monitoring`, `development`, `education`, `finance`, `games` und `utilities`. Die Katalog-Tabs filtern danach.
- `keywords`: bis zu 10 Wörter in beliebiger Sprache (1 bis 30 Zeichen), über die die Suche des Stores die App findet, neben ihrer ID, ihrem Namen, ihrer Beschreibung und ihrem Autor.
- `locales`: die Sprachen der eigenen Oberfläche der App als BCP-47-Tags (`en`, `pt-BR`). Der Store sagt "Verfügbar auf Deutsch", wenn eine zur Sprache des Benutzers oder ihrer Grundsprache passt.
- `screenshots`: bis zu 8 `{ "file": "frontend/<Pfad>.png", "caption": {...} }`. Die Dateien gehören zum Frontend (PNG, JPEG oder WebP, höchstens 1 MiB je Datei; die Box und `pithyx validate` prüfen beides mit [`app.package.layout_invalid`](/de/docs/reference/errors/#app.package.layout_invalid)), eine installierte App liefert sie also selbst aus, und ihre Seite im Store zeigt sie. Ein Katalog behält seinen eigenen Ordner `screenshots/` für Apps, die noch nicht installiert sind.
- `releaseNotes`: was in dieser Version neu ist, mehrsprachig wie `description`. Der Update-Dialog und der Tab **Updates** zeigen es; das zählt vor allem bei Apps, die sich nie von selbst aktualisieren.
- `requirements`: `memory` und `disk` in MB, ungefähr was die App braucht. Der Installationsdialog warnt, wenn die Box weniger Arbeitsspeicher oder der App-Pool weniger freien Platz hat, und installiert trotzdem.
- `audience`: `everyone` oder `admins`, die Vorgabe für "Wer sie nutzen darf" bei der Installation. Eine App, die jeden Benutzer sehen kann, als root läuft oder ein Sonderrecht verlangt, wird ohnehin nur Administratoren empfohlen; der Administrator kann es ändern.

```json
"privacy": { "sends": "none" },
"security": "mailto:security@example.com",
"maturity": "beta",
"categories": ["productivity"],
"keywords": ["todo", "Aufgaben"],
"locales": ["en", "de"],
"screenshots": [{ "file": "frontend/screenshots/main.png", "caption": { "en": "Your notes", "de": "Deine Notizen" } }],
"releaseNotes": { "en": "Notes can have a due date.", "de": "Notizen können ein Fälligkeitsdatum haben." },
"requirements": { "memory": 128, "disk": 50 },
"audience": "everyone"
```

## Benachrichtigungskategorien

`notifications.categories` (optional, D390) nennt bis zu 16 Arten von Benachrichtigungen, die die App sendet, jede `{ "id", "name", "description"? }` mit einer `id` aus Kleinbuchstaben, Ziffern und Bindestrichen (höchstens 32 Zeichen, eindeutig in der App) und einem mehrsprachigen `name`. Das braucht die Berechtigung `notifications` ([`app.manifest.permission_missing`](/de/docs/reference/errors/#app.manifest.permission_missing)). `app.notify({ ..., category: "mentions" })` nennt die Art; eine Kategorie, die das Manifest nicht angibt, wird mit [`notifications.message.invalid`](/de/docs/reference/errors/#notifications.message.invalid) abgelehnt. Jeder Benutzer kann eine Art unter **Benachrichtigungskanäle** ausschalten: Ihre Benachrichtigungen kommen dann als gelesen an, ohne Toast, E-Mail oder Webhook. Eine Benachrichtigung ohne Kategorie wird nie stummgeschaltet.

```json
"notifications": {
  "categories": [
    { "id": "messages", "name": { "en": "New messages", "de": "Neue Nachrichten" } },
    { "id": "mentions", "name": { "en": "Mentions", "de": "Erwähnungen" } }
  ]
}
```

## Fenster

`window` ist Pflicht:

- `instances`: `single` fokussiert das vorhandene Fenster des Benutzers auf diesem Gerät und übergibt Startargumente; `multi` öffnet jedes Mal ein neues Fenster.
- `defaultSize` und `minSize`: `width` und `height` als ganze Zahlen von 100 bis 8192 CSS-Pixel. `minSize` darf in keiner Richtung größer sein als `defaultSize`. Die Standardgröße gilt, wenn der Benutzer keine gemerkte Größe hat; das Minimum gilt auf Bildschirmen, auf die es passt.
- `loading` (optional): `text`, die Zeile, die ein neues Fenster zeigt, während das Backend startet, übersetzt wie `name` (bis zu 60 Zeichen; ohne sie schreibt der Desktop "<Name> wird gestartet ..."), und `untilReady`, ob der Platzhalter nach dem Verbinden der App auf `app.window.ready()` wartet. Siehe [Ereignisse und Kontext](/de/docs/sdk/guides/events-and-context/#laden).
- `suggestAtSignIn` (optional, Standard false): die App empfiehlt sich zum Öffnen nach der Anmeldung, für eine App, die man den ganzen Tag offen hat, etwa einen Chat. Der Desktop markiert sie dort als empfohlen, wo Benutzer die Apps nach der Anmeldung wählen (Benutzermenü, **Beim Anmelden öffnen**); von selbst öffnet er sie nie.

`fileHandlers` (optional, bis zu 32) lässt die App bei Dateitypen unter "Öffnen mit …" erscheinen. Jeder Handler hat `extensions` (1 bis 32 Erweiterungen in Kleinbuchstaben ohne Punkt, etwa `png` oder `tar.gz`), und optional `mimeTypes` (1 bis 32) und `isDefault`. Mit `isDefault` öffnet ein Doppelklick in Dateien den Typ in deiner App, solange keine andere installierte App `isDefault` dafür setzt und der Benutzer nicht mit ".x-Dateien immer so öffnen" anders gewählt hat. Eine Erweiterung kann nur zu einem Handler gehören. Die Datei erreicht die App über das Ereignis `launch` oder `openFile`.

`fileActions` (optional, bis zu 16, D375) fügt dem Kontextmenü von Dateien und Ordnern in Dateien Einträge hinzu, etwa "Hier entpacken" bei Archiven oder "Als ZIP komprimieren" für eine Auswahl. Jede Aktion hat eine `id` (eindeutig in der App), ein `label` wie `name` und sagt, wofür sie gilt: `extensions` (Dateien mit diesen Erweiterungen), `folders: true` oder beides. Mit `multiple: true` wird sie für mehrere ausgewählte Elemente zugleich angeboten (sonst nur für genau eines), mit `writes: true` nur dort, wo der Benutzer Dateien ändern darf. Der Desktop zeigt eine Aktion nur, wenn jedes ausgewählte Element passt. Wählt man sie, öffnet sich deine App mit `launch.action`: der `id` der Aktion, der Freigabe als `location`, dem `folder` und den `names` der ausgewählten Elemente. Deine App liest und schreibt sie mit der Datei-API in dieser Freigabe, mit den Rechten des Benutzers; deshalb braucht `fileActions` die Berechtigung `files.shares` (ohne sie lehnt die Box das Manifest mit [`app.manifest.permission_missing`](/de/docs/reference/errors/#app.manifest.permission_missing) ab).

## Berechtigungen

`permissions` ist eine Pflicht-Map (sie kann leer sein, `{}`) vom Namen einer Berechtigung oder eines Sonderrechts zu dem, was die App dazu sagt. Jeder Eintrag braucht einen `reason`: warum die App ihn braucht, in deinen Worten, übersetzt wie `name` (`en` Pflicht, eine Zeile, höchstens 200 Zeichen). Installationsdialog und Store-Seite zeigen ihn neben dem Recht; `pithyx validate` und die Box lehnen ein Manifest ohne ihn ab.

```json
"permissions": {
  "network.internet": {
    "reason": { "en": "Fetches the weather for your city.", "de": "Lädt das Wetter für deine Stadt." },
    "hosts": ["api.open-meteo.com"]
  },
  "device:/dev/dri": {
    "reason": { "en": "Converts videos faster with the graphics card." },
    "optional": true
  }
}
```

`hosts` ist nur bei `network.internet` erlaubt: die Domains, mit denen die App spricht (`api.example.org`, oder `*.example.org` für ihre Subdomains), angezeigt im Store; die Box setzt sie noch nicht durch. `optional` ist nur bei einem Geräterecht erlaubt (siehe [Sonderrechte](#sonderrechte)). Die Berechtigungen sind:

| Berechtigung             | Erlaubt der App                                                                                                                                           |
| ------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `files.read`             | Dateien lesen, die der Benutzer mit ihr öffnet oder in einem Dateidialog des Desktops wählt                                                               |
| `files.write`            | diese Dateien schreiben und neue Dateien über den Speichern-Dialog des Desktops speichern                                                                 |
| `files.shares`           | alle freigegebenen Ordner durchsuchen, auf die der Benutzer zugreifen darf, mit dessen eigenen Rechten                                                    |
| `notifications`          | Benachrichtigungen anzeigen                                                                                                                               |
| `notifications.external` | lässt sie mit `notifications` auch die E-Mail- und Webhook-Kanäle der Benutzer erreichen                                                                  |
| `directory.read`         | alle Benutzer und Gruppen auflisten                                                                                                                       |
| `network.internet`       | ausgehende Verbindungen ins Internet aus ihrem Fenster (`connect-src https:`) und ihrem Backend aufbauen                                                  |
| `network.lan`            | ausgehende Verbindungen aus ihrem Backend zu anderen Geräten im lokalen Netzwerk aufbauen (private, geteilte und Link-local-Adressen); nie zur Box selbst |

Ein Administrator bestätigt die Liste bei der Installation und kann jede Berechtigung später entziehen; die App bekommt dann ein `permissionsChange`-Ereignis. Eine Berechtigung, die in einem Update neu ist, wartet auf den Administrator, und eine entzogene Berechtigung bleibt über Updates hinweg entzogen. Ein Aufruf ohne die nötige Berechtigung schlägt mit [`app.permission.denied`](/de/docs/reference/errors/#app.permission.denied) fehl.

Eine App, die `directory.read` verlangt, einen Container als root betreibt (`backend.root`) oder ein Sonderrecht verlangt, wird nur für Administratoren vorgeschlagen: Der Installationsdialog wählt **Nur Administratoren** vor und sagt warum. Ebenso bei `"audience": "admins"` (siehe [Angaben für den Store](#angaben-für-den-store)). Der Administrator kann sie trotzdem allen erlauben.

### Sonderrechte

Dieselbe Map enthält die Rechte über die Sandbox hinaus, nur für das Backend (D311, D387): `host.network`, `host.pid`, `device:/dev/<pfad>`, `capability:<NAME>`, `docker.socket`, `docker.socket.raw` und `host.full`. Jedes braucht ein `backend` ([`app.elevated.backend_missing`](/de/docs/reference/errors/#app.elevated.backend_missing)). Die Box ordnet jedes einer Risikostufe zu (D388, `elevated.tiers` in den [Compose-Regeln](/de/docs/reference/compose-rules/#erhöhte-rechte)), und die Stufe entscheidet, was ein Administrator zum Erteilen tut:

| Stufe         | Rechte                                                                                 | Erteilen                                                                                           |
| ------------- | -------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------- |
| Gerätezugriff | `device:/dev/net/tun`, `device:/dev/dri`, `device:/dev/serial` und die Knoten darunter | ein Hinweis und ein Häkchen                                                                        |
| Hohes Risiko  | `host.network`, `host.pid`, `capability:<NAME>`, `docker.socket`, jedes andere Gerät   | ein Risikotext, ein Häkchen und das Passwort (und TOTP)                                            |
| Root-Zugriff  | `docker.socket.raw`, später `host.full`                                                | ein eigener roter Schritt mit den Folgen, der eingetippte Name der App und das Passwort (und TOTP) |

Eine App mit einem Recht der Stufe Root-Zugriff aktualisiert sich nie von selbst, egal was die Update-Regel der Box sagt, und jeder andere Administrator bekommt eine Benachrichtigung, wenn eines erteilt wird. `docker.socket.raw` ist aus einer Datei und aus jedem Katalog erlaubt; verwaltet deine App nur Container, die sie selbst startet, verlange stattdessen `docker.socket` (`pithyx validate` warnt, `cli.rights.raw_socket`). `host.full` und gesperrte Geräte und Capabilities werden mit [`app.elevated.unsupported`](/de/docs/reference/errors/#app.elevated.unsupported) abgelehnt. Mit `root: true` kann eine App weder `host.network`, `host.pid` noch ein Gerät außer `/dev/net/tun` verlangen ([`app.elevated.root_conflict`](/de/docs/reference/errors/#app.elevated.root_conflict)), und eine Capability braucht `root: true` ([`app.elevated.root_required`](/de/docs/reference/errors/#app.elevated.root_required)). Die Compose-Datei muss genau die Rechte nutzen, die die Map angibt; Einzelheiten stehen unter [Erhöhte Rechte](/de/docs/reference/compose-rules/#erhöhte-rechte).

Ein Geräterecht mit `optional: true` ist eines, ohne das das Backend läuft (etwa `/dev/dri` für Hardwarebeschleunigung). Jedes andere Gerät ist Pflicht: Ohne es lässt sich die App nicht installieren ([`app.device.missing`](/de/docs/reference/errors/#app.device.missing)), und ihr Backend startet erst, wenn das Gerät wieder da ist.

### Ordner

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
- `backup`: Hinweise für ein Backup der App-Daten, `{ "exclude": ["cache", "data/thumbnails"], "stop": true }`. `exclude` nennt bis zu 32 Ordner, die ein Backup auslassen kann, weil die App sie neu aufbaut: ein benanntes Volume der Compose-Datei, optional gefolgt von einem Pfad darin; das Volume muss deklariert sein ([`app.compose.reference_unknown`](/de/docs/reference/errors/#app.compose.reference_unknown)). `stop: true` bittet darum, das Backend anzuhalten, während der Snapshot entsteht, für Daten, die sich nicht sicher kopieren lassen, während sie sich ändern; ohne das entsteht der Snapshot bei laufendem Backend, konsistent wie nach einem Stromausfall. Zugeordnete Speicherordner gehören nie zum Backup der App. Die Box hat noch kein Backup von App-Daten: Sie prüft das Feld, nutzt es aber noch nicht.

Die Sonderrechte, die das Backend nutzt, stehen in `permissions`, siehe [Sonderrechte](#sonderrechte).

Über das Schema hinaus prüft die Box, dass Service- und Endpunkt-Namen existieren ([`app.manifest.reference_unknown`](/de/docs/reference/errors/#app.manifest.reference_unknown)) und dass die Speicheranforderungen zu den Compose-Mounts passen.

## Lebenszyklus

`lifecycle` bestimmt, wann das Backend läuft. `always` startet es, sobald es laufen kann (die App ist aktiviert, der Pool verfügbar, erforderliche Ordner sind zugeordnet). `onDemand` läuft, solange ein Fenster offen ist oder eine Anfrage oder ein Zeitplan es hält, und stoppt `idleTimeout` Sekunden nach dem letzten davon. Die Details stehen unter [Backends](/de/docs/developers/apps/backend/#lebenszyklus).

## Versionen

Die Box vergleicht die installierte mit der hochgeladenen Version und zeigt Installation, Update, Neuinstallation oder Downgrade an. Eine Neuinstallation derselben Version ersetzt deren Dateien; bei einer älteren Version warnt die Box, dass die Daten vielleicht nicht passen. Die Box behält die vorherige Version und einen Snapshot der App-Daten bis zum nächsten Update, damit ein Administrator zurückrollen kann. In einem Katalog wird eine veröffentlichte Version nie geändert oder entfernt.
