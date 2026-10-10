---
title: 'pithyx lock'
description: 'pithyx lock löst die Images der Compose-Datei des Backends bei ihren Registrys auf und schreibt backend/images.lock.json.'
status: ready
audience: developer
sidebar:
  order: 2
---

`pithyx lock` löst jedes Image der Compose-Datei des Backends bei seiner Registry auf und schreibt `backend/images.lock.json`: pro Image den Digest des Plattform-Manifests und die Image-ID (Config-Digest) für amd64 und arm64. Die Box und `pithyx validate` lehnen ein Backend ab, dessen Lock nicht zu seiner Compose-Datei passt. Nutze es für Backends, die nur Images nennen; [`pithyx build`](/de/docs/reference/cli/build/) schreibt den Lock für die Dienste, die es baut, selbst.

## Aufruf

<!-- generated:cli-synopsis lock -->

```sh
pithyx lock [project folder] [--frontend <folder>]
```

<!-- /generated -->

## Optionen

<!-- generated:cli-options lock -->

| Option       | Wert       | Typ    | Standard | Beschreibung       |
| ------------ | ---------- | ------ | -------- | ------------------ |
| `--frontend` | `<folder>` | string | `dist`   | The built frontend |

<!-- /generated -->

`--frontend` wird gelesen, ändert für `lock` aber nichts.

## Wie es auflöst

- Es liest `pithyx.json` und die Compose-Datei aus `backend.compose` und nimmt das `image` jedes Dienstes (jedes Image einmal, sortiert). Images müssen per Digest gepinnt sein (`name@sha256:...`), wie es die [Compose-Regeln](/de/docs/reference/compose-rules/) verlangen.
- Für jedes Image holt es das Manifest über seinen Digest. Bei einem Index für mehrere Plattformen nimmt es die Einträge `linux/amd64` und `linux/arm64` (bei arm64 ohne Variante oder mit `v8`) und liest jeweils den Config-Digest. Bei einem Image für eine Plattform liest es die Config, um die Architektur zu erfahren.
- Ein Image ohne eine der beiden Architekturen lässt den Befehl scheitern. Ein Image mit nur einer wird geschrieben; `validate` warnt dann mit `cli.lock.platform_missing`.
- Registrys werden anonym gefragt oder mit den Zugangsdaten unter `auths` in `~/.docker/config.json` (oder `$DOCKER_CONFIG/config.json`), einschließlich des Token-Ablaufs von Registrys wie Docker Hub und GHCR. Credential-Helper werden nicht genutzt.
- Es schreibt die Datei nur, wenn sich der Inhalt geändert hat: `backend/images.lock.json is up to date.` oder `Wrote backend/images.lock.json for <n> image(s).`

## Dateien

| Datei                                   | Zugriff                                          |
| --------------------------------------- | ------------------------------------------------ |
| `pithyx.json`                           | gelesen                                          |
| die Compose-Datei aus `backend.compose` | gelesen                                          |
| `backend/images.lock.json`              | gelesen, geschrieben, wenn sie sich geändert hat |

## Exit-Codes

| Code | Wann                                                                                                                                                                                            |
| ---- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 0    | Der Lock ist geschrieben oder aktuell. Auch wenn die App kein Backend hat: `The app has no backend; there is nothing to lock.`                                                                  |
| 1    | `pithyx.json` fehlt oder ist ungültig (es gibt dieselbe Zeile "no backend" aus), eine Registry scheitert oder verweigert den Zugriff, oder ein Image hat weder ein amd64- noch ein arm64-Image. |
| 2    | Mehr als ein Projektordner.                                                                                                                                                                     |

## Befunde

`lock` meldet selbst keine Befunde; starte [`pithyx validate`](/de/docs/reference/cli/validate/), um zu sehen, warum ein Manifest ungültig ist.

## Beispiele

```sh
pithyx lock
pithyx validate   # prüft den neuen Lock gegen die Compose-Datei
```
