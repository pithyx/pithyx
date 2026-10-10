---
title: 'pithyx build'
description: 'pithyx build baut die Backend-Dienste aus compose.dev.yaml mit Docker und pinnt compose.yaml und den Image-Lock darauf.'
status: ready
audience: developer
sidebar:
  order: 3
---

`pithyx build` baut die Dienste aus `backend/compose.dev.yaml`, die `build:` haben, und schreibt daraus die gepinnte `backend/compose.yaml` und `backend/images.lock.json`. `compose.dev.yaml` ist die Quellform des Backends: dieselben [Compose-Regeln](/de/docs/reference/compose-rules/) plus `build`. Die Box liest sie nie und baut nie. Kataloge bauen in ihrer CI daraus mit `--oci-out` und `--pin`.

## Aufruf

<!-- generated:cli-synopsis build -->

```sh
pithyx build [project folder] [--oci-out <folder>] [--pin <prefix>]
```

<!-- /generated -->

## Optionen

<!-- generated:cli-options build -->

| Option      | Wert       | Typ    | Standard | Beschreibung                                                                                  |
| ----------- | ---------- | ------ | -------- | --------------------------------------------------------------------------------------------- |
| `--oci-out` | `<folder>` | string | –        | Write OCI archives &lt;service&gt;.tar for amd64 and arm64 there                              |
| `--pin`     | `<prefix>` | string | –        | Pin to the archives of --oci-out as &lt;prefix&gt;/&lt;id&gt;/&lt;service&gt;:&lt;version&gt; |

<!-- /generated -->

## Modi

**Lokal (ohne Optionen).** Für jeden Dienst mit `build`, in der Reihenfolge der Datei: `docker build` für die Architektur dieses Rechners (amd64 oder arm64, andere scheitern), getaggt als `pithyx.local/<id>/<service>:<version>`, danach `docker save`, um den Manifest-Digest zu lesen (Docker 25 oder neuer, das ein OCI-Layout speichert). Die gepinnte Datei nennt `pithyx.local/<id>/<service>:<version>@sha256:...`. Solche Images gibt es nur in deinem Docker: Bring sie mit [`pithyx pack --images`](/de/docs/reference/cli/pack/) ins Paket.

**Reproduzierbar (`--oci-out <folder>`).** Für jeden Dienst `docker buildx build --platform linux/amd64,linux/arm64` ohne Provenance und SBOM in das OCI-Archiv `<folder>/<service>.tar`, mit Zeitstempeln auf `SOURCE_DATE_EPOCH` umgeschrieben (aus der Umgebung, sonst die Zeit des letzten Git-Commits des Projekts; ohne beides scheitert es). Es wird nichts gepusht und keine `compose.yaml` geschrieben.

**Pinnen (`--oci-out <folder> --pin <prefix>`).** Baut nichts. Es liest die Archive aus `--oci-out` und pinnt jeden Dienst auf `<prefix>/<id>/<service>:<version>@<digest>`, so wie die Images nach dem Push der Archive dort liegen werden. `--pin` ohne `--oci-out` scheitert.

Im lokalen Modus und beim Pinnen behalten Dienste, die nur ein `image` nennen, dieses und werden wie bei [`pithyx lock`](/de/docs/reference/cli/lock/) bei ihrer Registry aufgelöst. Die geschriebene `compose.yaml` beginnt mit dem Kommentar `Written by pithyx build from compose.dev.yaml; edit that file and build again.` und behält die übrigen Kommentare der Quelle. Vor dem Schreiben muss die gepinnte Datei die Prüfung der Box bestehen.

## Dateien

| Datei                                                                                  | Zugriff                                                         |
| -------------------------------------------------------------------------------------- | --------------------------------------------------------------- |
| `pithyx.json`                                                                          | gelesen; muss gültig sein und ein Backend haben                 |
| `backend/compose.dev.yaml` (neben `backend.compose`, mit `.dev` vor der Endung)        | gelesen und mit den Regeln der Quellform geprüft                |
| die Build-Kontexte und Dockerfiles, die sie nennt                                      | von Docker gelesen                                              |
| `backend/compose.yaml` (der Pfad aus `backend.compose`) und `backend/images.lock.json` | geschrieben im lokalen Modus und beim Pinnen                    |
| `<folder>/<service>.tar`                                                               | geschrieben mit `--oci-out`, gelesen mit `--pin`                |
| ein temporärer Ordner `pithyx-build-*`                                                 | hält im lokalen Modus die gespeicherten Images, danach entfernt |

## Exit-Codes

| Code | Wann                                                                                                                                                                                                                                                                                                         |
| ---- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| 0    | Gebaut (eine Zeile pro Dienst) oder gepinnt.                                                                                                                                                                                                                                                                 |
| 1    | `pithyx.json` ist ungültig, die App hat kein Backend, `compose.dev.yaml` fehlt oder verletzt die Regeln (die Meldungen folgen), Docker oder eine Registry scheitert, der Rechner ist weder amd64 noch arm64, `--pin` ohne `--oci-out`, oder weder `SOURCE_DATE_EPOCH` noch ein Git-Checkout für `--oci-out`. |
| 2    | Mehr als ein Projektordner.                                                                                                                                                                                                                                                                                  |

## Befunde

Probleme von `compose.dev.yaml` und der gepinnten Datei werden als Meldungen der Box nach `pithyx: <file>:` ausgegeben, eine pro Zeile; ihre Codes sind `app.compose.*` und `app.elevated.unsupported` ([Befunde](/de/docs/reference/cli/findings/)).

## Beispiele

```sh
# Für diesen Rechner bauen und die Images für eine Offline-Installation packen
pithyx build && pithyx pack --images

# Was die CI eines Katalogs tut: beide Architekturen bauen, pushen, pinnen
pithyx build --oci-out out
# ... out/<service>.tar nach ghcr.io/example/apps/<id>/<service>:<version> pushen
pithyx build --oci-out out --pin ghcr.io/example/apps
```
