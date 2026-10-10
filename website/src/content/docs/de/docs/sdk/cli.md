---
title: '@pithyx/cli'
description: 'Der Befehl pithyx: validate, lock, build, pack, dev und die Katalog-Befehle.'
status: draft
audience: developer
sidebar:
  order: 4
---

Der Befehl `pithyx` prüft, entwickelt und paketiert Pithyx-Apps und führt die CI von Katalog-Repositorys aus. Er hat sechs Befehle: `validate`, `lock`, `build`, `pack`, `dev` und `catalog`. Die Befehle nutzen dieselben Validatoren wie die Box, ein Fehler, den du lokal siehst, ist also der Fehler, den die Box melden würde.

```sh
npm install --save-dev @pithyx/cli@next
npx pithyx --help
```

Aufruf: `pithyx <command> [project folder] [options]`. Der Ordner ist standardmäßig das aktuelle Verzeichnis. Exit-Codes: 0 bei Erfolg, 1 bei Befunden oder einem Fehlschlag, 2 bei falscher Benutzung (ein unbekannter Befehl, eine fehlende Option). `pithyx --version` gibt die Version aus. Node.js 22.12 oder neuer ist erforderlich.

Jeder Befehl mit allen Optionen, Exit-Codes, Dateien und Befunden steht in der [Referenz des Befehls pithyx](/de/docs/reference/cli/).

Die Option `--frontend <folder>` benennt das gebaute Frontend (Standard `dist`) für `validate`, `lock`, `pack` und `dev`.

## pithyx dev

```sh
pithyx dev [folder] [--port 7400] [--host 127.0.0.1] [--endpoint name=url ...] [--no-docker]
```

Startet den Mock-Desktop mit deinem gebauten Frontend in der Sandbox der Box und einen Proxy zum Backend. Der Port muss eine ganze Zahl von 1 bis 65535 sein. Ein Backend läuft mit `docker compose` aus `backend/compose.dev.yaml`, wenn es existiert, sonst aus der festgelegten `backend/compose.yaml`. `--endpoint name=url` (wiederholbar) richtet einen Endpunkt auf einen Server aus, den du selbst betreibst, und `--no-docker` lässt das Backend in Ruhe (Endpunkte ohne `--endpoint` antworten dann mit 503). Es braucht eine gültige `pithyx.json`. Beende es mit Strg+C. Details: [Deine erste App](/de/docs/developers/apps/first-app/#pithyx-dev).

Mit Vite nutzt du stattdessen das Plugin: `import pithyx from '@pithyx/cli/vite'` und füge `pithyx()` zu `plugins` hinzu, dann starte `vite`. Optionen: `project`, `validate` (Standard true; validiert nach `vite build`), `endpoints` und `docker`.

## pithyx build

```sh
pithyx build [folder] [--oci-out <dir>] [--pin <prefix>]
```

Baut die Services aus `backend/compose.dev.yaml`, die `build:` haben. Es braucht eine App mit Backend und diese Datei. Standardmäßig nutzt es das lokale Docker: Es baut für die Architektur dieser Maschine (amd64 oder arm64), versieht die Images mit dem Tag `pithyx.local/<id>/<service>:<version>`, speichert sie und schreibt die festgelegte `backend/compose.yaml` und `backend/images.lock.json`. Services, die nur ein `image` nennen, werden aus ihrer Registry aufgelöst.

- `--oci-out <dir>` baut reproduzierbar für amd64 und arm64 mit `docker buildx` in OCI-Archive `<dir>/<service>.tar` und pusht nichts. Es braucht `SOURCE_DATE_EPOCH` in der Umgebung oder einen Git-Checkout.
- `--pin <prefix>` (nur zusammen mit `--oci-out`) legt die Compose-Datei auf `<prefix>/<id>/<service>:<version>@<digest>` fest, so wie die Images sein werden, nachdem du die Archive gepusht hast.

## Weitere App-Befehle

- `pithyx validate [folder]` prüft `pithyx.json`, `icon.svg`, den Einstieg im gebauten Frontend, das Paketlayout, `backend/compose.yaml` und `backend/images.lock.json`. Es warnt, solange `icon.svg` das Vorlagen-Icon ist. Bei Erfolg gibt es `<id> <version> is valid.` aus.
- `pithyx lock [folder]` löst die Images aus `backend/compose.yaml` auf und schreibt `backend/images.lock.json`. Es braucht das Netzwerk.
- `pithyx pack [folder] [--out <file or folder>] [--images]` validiert und schreibt dann `<id>-<version>.pithyxapp`. `--out` muss auf `.pithyxapp` enden oder ein Ordner sein. `--images` fügt die Images für Offline-Installationen hinzu und braucht Docker.

## pithyx catalog

Führe diese im Wurzelverzeichnis eines Katalog-Repositorys aus. Das erste Argument ist der Unterbefehl. `--catalog <file>` benennt die Katalogdatei (Standard `catalog.json`).

| Unterbefehl                                                                                                                                             | Zweck                                                                                                                                                         |
| ------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `catalog check --base <commit> --author <login> [--index <file>]`                                                                                       | Prüft die Regeln für Pull Requests auf die Änderungen seit dem Basis-Commit.                                                                                  |
| `catalog pending [--index <file>]`                                                                                                                      | Gibt als JSON die App-Versionen in `apps/` aus, die `catalog-next` noch nicht aufführt.                                                                       |
| `catalog index <branch folder> --channel stable\|beta (--add <package>... --source <checkout> --repository <url> --commit <sha> \| --promote <folder>)` | Fügt gebaute Pakete oder die Versionen eines anderen Branches hinzu und schreibt `index.json` mit der nächsten Sequenznummer.                                 |
| `catalog sign <branch folder> --key-id <id> [--append]`                                                                                                 | Signiert `index.json` mit dem privaten Ed25519-Schlüssel (PEM) in der Umgebungsvariable `PITHYX_SIGNING_KEY`. `--append` behält Signaturen anderer Schlüssel. |
| `catalog verify <branch folder> --channel stable\|beta [--key <id>=<base64>]...`                                                                        | Prüft den Index, jede Datei und, mit Schlüsseln, die Signatur, wie es eine Box tut.                                                                           |

Der Signaturschlüssel wird nie als Flag akzeptiert. Siehe [In einem Katalog veröffentlichen](/de/docs/developers/apps/publishing/).

## JavaScript-API

`@pithyx/cli` exportiert die Validatoren auch zur Verwendung in Skripten: `validateManifest`, `validateCompose`, `validateIcon`, `checkLayout`, `isSafePath`, `pack`, `packageName`, `readProject`, `resolveLock`, `formatLock`, `isValidCron` und `describe`, sowie die Konstanten `MANIFEST_FILE`, `ICON_FILE`, `LOCK_FILE`, `CHECKSUMS_FILE`, `ARCHITECTURES`, `SUPPORTED_MANIFEST_VERSION` und `RESERVED_ID_PREFIX`.
