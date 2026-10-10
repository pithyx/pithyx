---
title: 'pithyx validate'
description: 'pithyx validate prüft ein App-Projekt so, wie die Box das daraus gebaute Paket prüft, und schreibt nichts.'
status: ready
audience: developer
sidebar:
  order: 1
---

`pithyx validate` prüft ein App-Projekt so, wie die Box das daraus gebaute Paket prüfen würde: `pithyx.json`, `icon.svg`, das gebaute Frontend, den Aufbau des Pakets, die Compose-Datei des Backends und ihren Image-Lock. Es schreibt nichts und braucht kein Netz. [`pithyx pack`](/de/docs/reference/cli/pack/) führt dieselben Prüfungen aus, bevor es ein Paket schreibt.

## Aufruf

<!-- generated:cli-synopsis validate -->

```sh
pithyx validate [project folder] [--frontend <folder>]
```

<!-- /generated -->

## Optionen

<!-- generated:cli-options validate -->

| Option       | Wert       | Typ    | Standard | Beschreibung       |
| ------------ | ---------- | ------ | -------- | ------------------ |
| `--frontend` | `<folder>` | string | `dist`   | The built frontend |

<!-- /generated -->

## Was es prüft

1. `pithyx.json` gegen das [Manifest-Schema](/de/docs/reference/manifest-schema/) und die Regeln darüber hinaus (ID, Versionsbereiche, Fenstergrößen, Verweise, Zeitpläne, erweiterte Rechte).
2. `icon.svg` mit den Icon-Regeln der Box. Eine Warnung `cli.icon.template`, solange es noch das Vorlagen-Icon von `@pithyx/cli` ist.
3. Den Ordner des gebauten Frontends. Versteckte Dateien und Ordner (Namen, die mit `.` beginnen) bleiben mit der Warnung `cli.frontend.skipped` aus dem Paket; Links und Spezialdateien sind Fehler (`app.package.invalid`, Grund `entry_type`); ein fehlender Ordner ist der Fehler `cli.frontend.missing`.
4. Den Aufbau des Pakets, wie `pack` es schreiben würde, zum Beispiel ob der `entry` des Manifests unter `frontend/` existiert.
5. Mit Backend: die Compose-Datei aus `backend.compose` gegen die [Compose-Regeln](/de/docs/reference/compose-rules/) und das Manifest, danach `backend/images.lock.json`: Die Datei muss existieren (`cli.lock.missing`), lesbar sein (`cli.lock.invalid`) und genau die Images der Compose-Datei nennen (`cli.lock.stale`). Ein Image ohne Eintrag für amd64 oder arm64 ist eine Warnung (`cli.lock.platform_missing`): Die App lässt sich auf Boxen dieser Architektur nicht installieren.

Der Lock wird nur geprüft, wenn die Compose-Datei besteht. Warnungen ändern den Exit-Code nie.

## Dateien

| Datei                                                                          | Zugriff                                  |
| ------------------------------------------------------------------------------ | ---------------------------------------- |
| `pithyx.json`, `icon.svg`                                                      | gelesen                                  |
| der Frontend-Ordner (`--frontend`, Standard `dist`), jede Datei darunter       | gelesen                                  |
| `README.md`, `README`, `LICENSE`, `LICENSE.md`, `LICENSE.txt` im Projektordner | für das Paket vorgemerkt, wenn vorhanden |
| die Compose-Datei aus `backend.compose`, `backend/images.lock.json`            | gelesen                                  |

## Exit-Codes

| Code | Wann                                                                  |
| ---- | --------------------------------------------------------------------- |
| 0    | Kein Befund der Stufe Fehler. Es gibt `<id> <version> is valid.` aus. |
| 1    | Mindestens ein Fehler.                                                |
| 2    | Mehr als ein Projektordner.                                           |

:::caution
Ein Ordner ohne `pithyx.json`, aber mit gebautem Frontend, besteht derzeit mit leerem Namen (` is valid.`). `pack` lehnt denselben Ordner ab.
:::

## Befunde

Fehler mit den Codes der Box `app.manifest.*`, `app.icon.invalid`, `app.package.invalid`, `app.package.layout_invalid`, `app.compose.*` und `app.elevated.unsupported`, dazu die eigenen der CLI `cli.frontend.*`, `cli.icon.template` und `cli.lock.*`. Jeder mit seiner Meldung steht unter [Befunde](/de/docs/reference/cli/findings/).

## Beispiele

```sh
# Das Projekt im aktuellen Ordner, Frontend nach dist/ gebaut
pithyx validate

# Ein anderes Projekt, dessen Build nach build/ geht
pithyx validate ../photo-app --frontend build
```
