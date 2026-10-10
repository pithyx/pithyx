---
title: 'pithyx catalog sign'
description: 'pithyx catalog sign signiert index.json eines Katalog-Branches mit dem Ed25519-Schlüssel aus PITHYX_SIGNING_KEY.'
status: ready
audience: developer
sidebar:
  order: 9
---

`pithyx catalog sign` signiert die exakten Bytes von `index.json` in einem Branch-Ordner mit einem privaten Ed25519-Schlüssel und schreibt `index.json.sig`. Boxen prüfen diese Signatur gegen die Schlüssel, die für die Kataloge des Projekts in den Daemon eingebaut sind; eigene Kataloge werden nicht geprüft.

## Aufruf

<!-- generated:cli-synopsis catalog sign -->

```sh
pithyx catalog sign <branch folder> --key-id <id> [--append]
```

<!-- /generated -->

## Optionen

<!-- generated:cli-options catalog sign -->

| Option     | Wert   | Typ     | Standard | Beschreibung                                       |
| ---------- | ------ | ------- | -------- | -------------------------------------------------- |
| `--key-id` | `<id>` | string  | Pflicht  | In `pithyx --help` nicht beschrieben; siehe unten. |
| `--append` | –      | boolean | aus      | In `pithyx --help` nicht beschrieben; siehe unten. |

<!-- /generated -->

- `--key-id <id>`: der Name des Schlüssels, wie ihn der Schlüsselbund der Box kennt, zum Beispiel `pithyx-next-1`.
- `--append`: behält die Signaturen anderer Schlüssel-IDs in `index.json.sig`, für einen Schlüsselwechsel. Eine Signatur derselben Schlüssel-ID wird ersetzt. Ohne die Option enthält die Datei nur die neue Signatur.

Der Schlüssel kommt nur aus der Umgebungsvariable `PITHYX_SIGNING_KEY`: ein privater Ed25519-Schlüssel als PEM (PKCS #8, wie `openssl genpkey -algorithm ed25519` ihn schreibt). Er wird nie als Option angenommen, damit er nicht in einer Shell-Historie oder Prozessliste landet.

## Ausgabedatei

```json
{
  "signatureVersion": 1,
  "signatures": [{ "keyId": "pithyx-next-1", "signature": "<base64>" }]
}
```

## Dateien

| Datei                            | Zugriff                             |
| -------------------------------- | ----------------------------------- |
| `<branch folder>/index.json`     | gelesen                             |
| `<branch folder>/index.json.sig` | gelesen mit `--append`, geschrieben |

## Exit-Codes

| Code | Wann                                                                                                 |
| ---- | ---------------------------------------------------------------------------------------------------- |
| 0    | `Signed <branch>/index.json with <id>.`                                                              |
| 1    | Der Schlüssel ist kein privater Ed25519-Schlüssel als PEM, oder `index.json` lässt sich nicht lesen. |
| 2    | Kein Branch-Ordner, kein `--key-id`, oder `PITHYX_SIGNING_KEY` fehlt oder ist leer.                  |

## Befunde

Keine.

## Beispiele

```sh
openssl genpkey -algorithm ed25519 -out next.pem
PITHYX_SIGNING_KEY="$(cat next.pem)" pithyx catalog sign catalog-next --key-id example-next-1
```
