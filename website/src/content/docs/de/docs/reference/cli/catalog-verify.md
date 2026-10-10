---
title: 'pithyx catalog verify'
description: 'pithyx catalog verify prüft einen Katalog-Branch wie eine Box: die Signatur gegen angegebene Schlüssel, den Index und jede Datei.'
status: ready
audience: developer
sidebar:
  order: 10
---

`pithyx catalog verify` prüft einen Branch-Ordner eines Katalogs so, wie eine Box es tut, bevor sie ihn nutzt: die Signatur (wenn du Schlüssel angibst), den Index für diesen Katalog und Kanal und jede Datei jeder Version gegen ihren Eintrag. Die CI eines Katalogs startet es nach dem Signieren.

## Aufruf

<!-- generated:cli-synopsis catalog verify -->

```sh
pithyx catalog verify <branch folder> --channel stable|beta [--key <id>=<base64>]... [--catalog <file>]
```

<!-- /generated -->

## Optionen

<!-- generated:cli-options catalog verify -->

| Option      | Wert           | Typ                  | Standard       | Beschreibung                                       |
| ----------- | -------------- | -------------------- | -------------- | -------------------------------------------------- |
| `--catalog` | `<file>`       | string               | `catalog.json` | The catalog.json                                   |
| `--channel` | `stable\|beta` | string               | Pflicht        | In `pithyx --help` nicht beschrieben; siehe unten. |
| `--key`     | `<id>`         | string, wiederholbar | –              | In `pithyx --help` nicht beschrieben; siehe unten. |

<!-- /generated -->

- `--channel stable|beta`: der Kanal, den der Index angeben muss.
- `--key <id>=<base64>` (wiederholbar): ein vertrauenswürdiger öffentlicher Schlüssel, der rohe 32-Byte-Ed25519-Schlüssel in Base64, vertrauenswürdig für den angegebenen Kanal. Mit mindestens einem Schlüssel muss `index.json.sig` eine gültige Signatur von einem davon tragen. Ohne Schlüssel wird die Signatur nicht geprüft, wie bei einem eigenen Katalog.
- `--catalog <file>`: die Katalogdatei, deren ID der Index nennen muss (Standard `catalog.json`).

## Dateien

| Datei                                                                      | Zugriff |
| -------------------------------------------------------------------------- | ------- |
| `catalog.json` (oder `--catalog`)                                          | gelesen |
| `<branch folder>/index.json`, `index.json.sig`                             | gelesen |
| jede Datei in `<branch folder>/apps/<id>/<version>/`, die der Index listet | gelesen |

## Exit-Codes

| Code | Wann                                                                                                                       |
| ---- | -------------------------------------------------------------------------------------------------------------------------- |
| 0    | `<branch> is valid and signed.`, oder ohne Schlüssel `<branch> is valid (signature not checked).`                          |
| 1    | Ein Befund, `catalog.json` ist ungültig, `--channel` fehlt oder ist weder `stable` noch `beta`, oder ein `--key` ohne `=`. |
| 2    | Kein Branch-Ordner.                                                                                                        |

## Befunde

`store.catalog.invalid_json`, wenn `index.json` fehlt, die übrigen `store.catalog.*`-Codes des Index (`signature_invalid` mit dem Grund `missing`, `malformed`, `untrusted` oder `mismatch`) und `store.package.hash_mismatch` für jede abweichende Datei. Siehe [Befunde](/de/docs/reference/cli/findings/).

## Beispiele

```sh
pithyx catalog verify catalog-next --channel beta --key "example-next-1=$(cat next.pub.b64)"
```
