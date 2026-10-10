---
title: 'pithyx pack'
description: 'pithyx pack prüft ein App-Projekt und schreibt sein deterministisches Paket <id>-<version>.pithyxapp.'
status: ready
audience: developer
sidebar:
  order: 4
---

`pithyx pack` führt die Prüfungen von [`pithyx validate`](/de/docs/reference/cli/validate/) aus und schreibt, wenn es keinen Fehler gibt, das App-Paket `<id>-<version>.pithyxapp`: ein gzip-komprimiertes tar-Archiv, das du im Store installierst oder in einem Katalog veröffentlichst. Dieselben Dateien ergeben immer dieselben Bytes.

## Aufruf

<!-- generated:cli-synopsis pack -->

```sh
pithyx pack [project folder] [--frontend <folder>] [--out <path>] [--images]
```

<!-- /generated -->

## Optionen

<!-- generated:cli-options pack -->

| Option       | Wert       | Typ     | Standard    | Beschreibung                                                                                                                                                                                                                                                                  |
| ------------ | ---------- | ------- | ----------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `--frontend` | `<folder>` | string  | `dist`      | The built frontend                                                                                                                                                                                                                                                            |
| `--out`      | `<path>`   | string  | the project | The package file, or a folder for it                                                                                                                                                                                                                                          |
| `--images`   | –          | boolean | aus         | Add backend/images/&lt;arch&gt;/&lt;id&gt;.tar for offline installs, saved with the local docker (pull and save per architecture of the lock). Needs Docker with access to the registries; the images make the package large, and their bytes depend on Docker's save format. |

<!-- /generated -->

Ein `--out`, das auf `.pithyxapp` endet, ist die Paketdatei selbst (ihr Ordner muss existieren); alles andere muss ein vorhandener Ordner sein, der `<id>-<version>.pithyxapp` bekommt.

## Das Paket

- Zuerst `pithyx.json` und `icon.svg`, dann jeder andere Pfad nach Code-Einheiten sortiert.
- `frontend/` mit jeder Datei des gebauten Frontends außer versteckten.
- `README.md`, `README`, `LICENSE`, `LICENSE.md`, `LICENSE.txt`, wenn sie im Projektordner liegen.
- Mit Backend: seine Compose-Datei und `backend/images.lock.json`.
- `CHECKSUMS`: der SHA-256 jeder anderen Datei im Format von `sha256sum`, sodass `sha256sum -c CHECKSUMS` ein entpacktes Paket prüft.
- Mit `--images`: `backend/images/<arch>/<image id>.tar` für jedes Image und jede Architektur des Locks. Jedes Image kommt aus deinem Docker, wenn es dort liegt (Images von `pithyx build`), sonst wird es mit `docker pull --platform` über seinen Digest geholt und dann mit `docker save` gespeichert; die Image-ID nach dem Pull muss zum Lock passen. Die Archive machen das Paket groß, und ihre Bytes hängen vom Speicherformat von Docker ab.

Feste Modi, Besitzer und Zeiten und keine Zeit im gzip-Header machen das Archiv deterministisch.

## Dateien

| Datei                                                     | Zugriff                                                       |
| --------------------------------------------------------- | ------------------------------------------------------------- |
| alles, was `validate` liest                               | gelesen                                                       |
| das Paket (Standard `<project>/<id>-<version>.pithyxapp`) | geschrieben, ersetzt, wenn vorhanden                          |
| ein temporärer Ordner `pithyx-images-*`                   | hält mit `--images` die gespeicherten Images, danach entfernt |

## Exit-Codes

| Code | Wann                                                                                                                                                                  |
| ---- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 0    | Gepackt: `Packed <path> (<n> bytes).`                                                                                                                                 |
| 1    | Ein Befund der Stufe Fehler (`Not packed: fix the errors above first.`), keine gültige `pithyx.json`, oder Docker scheitert mit `--images`.                           |
| 2    | `--out` ist weder eine `.pithyxapp`-Datei noch ein vorhandener Ordner, `--images` für eine App ohne Backend oder ohne gültigen Lock, oder mehr als ein Projektordner. |

## Befunde

Dieselben wie bei [`pithyx validate`](/de/docs/reference/cli/validate/#befunde); siehe [Befunde](/de/docs/reference/cli/findings/).

## Beispiele

```sh
pithyx pack                          # ./com.example.notes-1.0.0.pithyxapp
pithyx pack --out dist-packages/     # in einen vorhandenen Ordner
pithyx pack --out notes.pithyxapp    # ein eigener Dateiname
pithyx pack --images                 # mit den Backend-Images für Offline-Installationen
```
