---
title: 'Befehl pithyx'
description: 'Referenz des Befehls pithyx aus @pithyx/cli: Aufruf, Befehle, globale Optionen, Exit-Codes, Umgebungsvariablen und Ausgabe.'
status: ready
audience: developer
sidebar:
  order: 0
---

`pithyx` ist der Befehl des npm-Pakets `@pithyx/cli` (MIT). Er prüft, sperrt, baut, packt und startet Pithyx-Apps auf deinem Rechner und führt die CI von Katalog-Repositorys aus. Er prüft mit denselben Regeln wie die Box, ein Fehler, den du lokal siehst, ist also der Fehler, den die Box melden würde. Die Aufrufe und Optionstabellen auf diesen Seiten werden bei jedem Build der Website aus dem Hilfetext und dem Argument-Parser der CLI erzeugt; der Text darum ist von Hand geschrieben. Die Optionsbeschreibungen kommen aus der Hilfe der CLI und sind deshalb englisch. Eine Einführung mit Installation findest du unter [@pithyx/cli](/de/docs/sdk/cli/).

## Aufruf

<!-- generated:cli-usage -->

```sh
pithyx <command> [project folder] [options]
```

<!-- /generated -->

Der Projektordner ist das erste Argument nach dem Befehl, standardmäßig das aktuelle Verzeichnis. Ein zweiter scheitert mit Exit-Code 2 (`pithyx validate takes one project folder, not: ...`). Die `catalog`-Unterbefehle laufen im Wurzelordner eines Katalog-Repositorys und nehmen stattdessen einen Branch-Ordner.

Jeder Befehl nimmt jede Option an, die der Parser kennt, aber nur die Optionen auf der Seite eines Befehls ändern, was er tut; die anderen werden ignoriert. Eine Option, die der Parser nicht kennt, scheitert mit Exit-Code 1 (`pithyx: Unknown option '--x'`). Node.js 22.12 oder neuer ist nötig.

## Befehle

<!-- generated:cli-commands -->

| Befehl                                                              | Beschreibung                                                                                                                                                                                                                                                                                                                                                                                                                   |
| ------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| [`pithyx validate`](/de/docs/reference/cli/validate/)               | Check the project as the box checks its package: pithyx.json, icon.svg, the entry in the built frontend, the package layout, backend/compose.yaml and backend/images.lock.json. Warns while icon.svg is still the template icon.                                                                                                                                                                                               |
| [`pithyx lock`](/de/docs/reference/cli/lock/)                       | Resolve the images of backend/compose.yaml from their registries and write backend/images.lock.json (the platform manifest and image id for amd64 and arm64). Needs the network; registries are asked anonymously or with the credentials stored in ~/.docker/config.json (not with credential helpers).                                                                                                                       |
| [`pithyx build`](/de/docs/reference/cli/build/)                     | Build the services of backend/compose.dev.yaml that have build: with the local Docker for this machine, tag them pithyx.local/&lt;id&gt;/&lt;service&gt;:&lt;version&gt; and pin backend/compose.yaml and backend/images.lock.json to them (for pack --images). --oci-out builds reproducibly for amd64 and arm64 with buildx into OCI archives and pushes nothing; --pin then pins to those archives as pushed to a registry. |
| [`pithyx pack`](/de/docs/reference/cli/pack/)                       | Validate, then write &lt;id&gt;-&lt;version&gt;.pithyxapp: a deterministic tar.gz with pithyx.json, icon.svg, frontend/, the backend's compose.yaml and lock, README and LICENSE when they exist, and CHECKSUMS. The same files give the same bytes.                                                                                                                                                                           |
| [`pithyx dev`](/de/docs/reference/cli/dev/)                         | Run the mock desktop with the app in the box's sandbox and a proxy to the backend, which runs with docker compose (backend/compose.dev.yaml instead of the pinned file when it exists). With Vite, use the plugin @pithyx/cli/vite and run vite instead.                                                                                                                                                                       |
| [`pithyx catalog check`](/de/docs/reference/cli/catalog-check/)     | The pull request rules on the changes since the base commit                                                                                                                                                                                                                                                                                                                                                                    |
| [`pithyx catalog pending`](/de/docs/reference/cli/catalog-pending/) | The app versions of apps/ that catalog-next does not list yet, as JSON                                                                                                                                                                                                                                                                                                                                                         |
| [`pithyx catalog index`](/de/docs/reference/cli/catalog-index/)     | Adds built packages or the versions of another branch, writes index.json                                                                                                                                                                                                                                                                                                                                                       |
| [`pithyx catalog sign`](/de/docs/reference/cli/catalog-sign/)       | Signs index.json with the Ed25519 key (PEM) in $PITHYX_SIGNING_KEY                                                                                                                                                                                                                                                                                                                                                             |
| [`pithyx catalog verify`](/de/docs/reference/cli/catalog-verify/)   | Checks the signature (with keys), the index and every file as a box does                                                                                                                                                                                                                                                                                                                                                       |

<!-- /generated -->

`pithyx help` gibt dieselbe Hilfe aus wie `pithyx --help`.

## Globale Optionen

<!-- generated:cli-global-options -->

| Option            | Beschreibung     |
| ----------------- | ---------------- |
| `-h`, `--help`    | Show this help   |
| `-v`, `--version` | Show the version |

<!-- /generated -->

`--version` hat Vorrang: `pithyx validate --version` gibt nur die Version aus. `pithyx` ohne Befehl gibt die Hilfe aus und endet mit 2.

## Exit-Codes

| Code | Bedeutung                                                                                                                                                                                                                                                                                                                                 |
| ---- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 0    | Erfolg. Auch `--help`, `--version` und `pithyx help`. Warnungen allein ändern den Code nicht.                                                                                                                                                                                                                                             |
| 1    | Ein Befund der Stufe Fehler, oder ein Schritt ist gescheitert: eine ungültige Datei, ein Aufruf von Docker oder einer Registry, eine unbekannte Option, ein Optionswert, mit dem der Befehl nichts anfangen kann (zum Beispiel `--channel` außer `stable` oder `beta`, `--endpoint` ohne `=`). Fehlschläge geben `pithyx: <message>` aus. |
| 2    | Falsche Benutzung, die die CLI selbst prüft: kein Befehl, ein unbekannter Befehl oder Katalog-Unterbefehl, mehr als ein Projektordner, eine fehlende Pflichtoption, `--out`, das weder eine `.pithyxapp`-Datei noch ein Ordner ist, `--port`, der kein Port ist, `--images` ohne Image-Lock.                                              |

`pithyx dev` läuft weiter; es endet nach Strg+C oder SIGTERM mit 0, sobald sein Backend gestoppt ist.

## Ausgabe

Ergebnisse gehen auf die Standardausgabe (`<id> <version> is valid.`, `Packed ...`, das JSON von `catalog pending`). Befunde gehen auf die Standardfehlerausgabe, einer pro Zeile, als `<level> <code>: <message>`, zum Beispiel:

```text
error app.manifest.schema_violation: The app's manifest is not valid at /window/minSize (required).
warning cli.icon.template: icon.svg is still the template icon (the Pithyx box). Replace it with the icon of your app.
```

Die Meldung ist der englische Text der Box für ihre Codes, die CLI sagt also, was die Box sagen würde. Jeder Code steht unter [Befunde](/de/docs/reference/cli/findings/).

## Umgebung

| Variable oder Werkzeug | Genutzt von                                                                                                                                                                                                                                  |
| ---------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `PITHYX_SIGNING_KEY`   | [`catalog sign`](/de/docs/reference/cli/catalog-sign/): der private Ed25519-Schlüssel als PEM. Er wird nie als Option angenommen.                                                                                                            |
| `SOURCE_DATE_EPOCH`    | [`build --oci-out`](/de/docs/reference/cli/build/): die Zeit jeder Datei in den Images. Ohne sie gilt die Zeit des letzten Git-Commits des Projekts.                                                                                         |
| `DOCKER_CONFIG`        | [`lock`](/de/docs/reference/cli/lock/) und [`build`](/de/docs/reference/cli/build/): der Ordner von `config.json` mit Zugangsdaten für Registrys (Standard `~/.docker`). Nur Einträge unter `auths` werden gelesen, keine Credential-Helper. |
| `docker`               | [`build`](/de/docs/reference/cli/build/), [`pack --images`](/de/docs/reference/cli/pack/) und [`dev`](/de/docs/reference/cli/dev/); `build --oci-out` braucht `docker buildx`.                                                               |
| `git`                  | [`catalog check`](/de/docs/reference/cli/catalog-check/) und `build --oci-out` ohne `SOURCE_DATE_EPOCH`.                                                                                                                                     |

Die CLI sendet von sich aus nichts irgendwohin. Nur [`lock`](/de/docs/reference/cli/lock/) und [`build`](/de/docs/reference/cli/build/) fragen Registrys, um Image-Digests aufzulösen.

## Vite

Mit Vite startest du nicht `pithyx dev`: Binde das Plugin aus `@pithyx/cli/vite` ein und starte `vite`. Siehe [@pithyx/cli](/de/docs/sdk/cli/).
