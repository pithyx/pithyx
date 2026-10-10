---
title: 'pithyx catalog pending'
description: 'pithyx catalog pending gibt als JSON die App-Versionen eines Katalog-Repositorys aus, die catalog-next noch nicht listet.'
status: ready
audience: developer
sidebar:
  order: 7
---

`pithyx catalog pending` listet die App-Versionen in `apps/` eines Katalog-Repositorys, die der Index von `catalog-next` noch nicht hat, als JSON auf der Standardausgabe. Die CI eines Katalogs baut genau diese.

## Aufruf

<!-- generated:cli-synopsis catalog pending -->

```sh
pithyx catalog pending [--index <file>]
```

<!-- /generated -->

## Optionen

<!-- generated:cli-options catalog pending -->

| Option    | Wert     | Typ    | Standard | Beschreibung                                       |
| --------- | -------- | ------ | -------- | -------------------------------------------------- |
| `--index` | `<file>` | string | –        | In `pithyx --help` nicht beschrieben; siehe unten. |

<!-- /generated -->

- `--index <file>`: die `index.json` von `catalog-next`. Ohne sie, oder wenn die Datei nicht existiert, steht jede App-Version aus.

## Ausgabe

Ein JSON-Array, Apps in Ordner-Reihenfolge, eine Zeile:

```json
[
  {
    "id": "com.example.notes",
    "version": "1.2.0",
    "path": "apps/com.example.notes",
    "backend": true
  }
]
```

`backend` sagt, ob das Manifest ein Backend hat, ob die CI also Images bauen muss. Ordner ohne `pithyx.json` werden übersprungen. Die Manifeste werden hier nicht geprüft; das hat [`catalog check`](/de/docs/reference/cli/catalog-check/) für den Pull Request getan.

## Dateien

| Datei                                    | Zugriff |
| ---------------------------------------- | ------- |
| `apps/*/pithyx.json` im aktuellen Ordner | gelesen |
| die Datei aus `--index`                  | gelesen |

## Exit-Codes

| Code | Wann                                                      |
| ---- | --------------------------------------------------------- |
| 0    | Die Liste ist ausgegeben, auch wenn sie leer ist (`[]`).  |
| 1    | Eine `pithyx.json` oder der Index ist kein gültiges JSON. |

## Befunde

Keine.

## Beispiele

```sh
pithyx catalog pending --index catalog-next/index.json
```
