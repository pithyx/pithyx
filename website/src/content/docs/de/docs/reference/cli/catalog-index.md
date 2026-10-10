---
title: 'pithyx catalog index'
description: 'pithyx catalog index fügt gebaute Pakete hinzu oder übernimmt die Versionen eines anderen Branches und schreibt index.json mit der nächsten Sequenz.'
status: ready
audience: developer
sidebar:
  order: 8
---

`pithyx catalog index` schreibt einen Katalog-Branch (`catalog-next` für Beta, `catalog` für Stabil): Es legt neue Versionen in den Branch-Ordner und schreibt `index.json` mit der nächsten Sequenznummer. Eine veröffentlichte Version ändert sich nie. Die alte Signatur wird entfernt; signiere den neuen Index danach mit [`catalog sign`](/de/docs/reference/cli/catalog-sign/).

## Aufruf

<!-- generated:cli-synopsis catalog index -->

```sh
pithyx catalog index <branch folder> --channel stable|beta (--add <package>... --source <checkout> --repository <url> --commit <sha> | --promote <folder>) [--catalog <file>]
```

<!-- /generated -->

## Optionen

<!-- generated:cli-options catalog index -->

| Option         | Wert           | Typ                  | Standard               | Beschreibung                                       |
| -------------- | -------------- | -------------------- | ---------------------- | -------------------------------------------------- |
| `--catalog`    | `<file>`       | string               | `catalog.json`         | The catalog.json                                   |
| `--channel`    | `stable\|beta` | string               | Pflicht                | In `pithyx --help` nicht beschrieben; siehe unten. |
| `--add`        | `<package>`    | string, wiederholbar | siehe die Formen unten | In `pithyx --help` nicht beschrieben; siehe unten. |
| `--source`     | `<checkout>`   | string               | siehe die Formen unten | In `pithyx --help` nicht beschrieben; siehe unten. |
| `--repository` | `<url>`        | string               | siehe die Formen unten | In `pithyx --help` nicht beschrieben; siehe unten. |
| `--commit`     | `<sha>`        | string               | siehe die Formen unten | In `pithyx --help` nicht beschrieben; siehe unten. |
| `--promote`    | `<folder>`     | string               | siehe die Formen unten | In `pithyx --help` nicht beschrieben; siehe unten. |

<!-- /generated -->

Der Branch-Ordner ist der Checkout des Branches, der geschrieben wird. Es gibt zwei Formen:

- **Hinzufügen** (`--add <package>...` mit `--source`, `--repository` und `--commit`): `--add` nennt eine gebaute `.pithyxapp` (wiederholbar). `--source <checkout>` ist der Checkout von `master`, aus dem die Pakete gebaut wurden; Screenshots kommen aus `<source>/apps/<id>/screenshots/`. `--repository <url>` und `--commit <sha>` kommen in die `source` jeder Version.
- **Übernehmen** (`--promote <folder>`): kopiert jede Version eines anderen Branch-Ordners (meist `catalog-next` nach `catalog`) mit ihrem Eintrag und überspringt Versionen, die der Branch schon hat.

Beide Formen lassen sich kombinieren; ohne beide bekommt der Index nur eine neue Sequenz. `--channel` ist `stable` oder `beta` und muss zum Branch passen. `--catalog <file>` nennt die Katalogdatei (Standard `catalog.json`); ihre ID kommt in den Index.

## Was es tut

1. Liest `catalog.json` und die aktuelle `index.json` des Branches (falls vorhanden) und prüft sie für diesen Katalog und Kanal.
2. Prüft jede Datei jeder Version, die schon im Index steht, gegen ihren Eintrag (`store.package.hash_mismatch`).
3. Für jedes Paket aus `--add`: liest `pithyx.json` und `icon.svg` daraus, prüft das Manifest (IDs in `org.pithyx.*` nur für `org.pithyx.official`), lehnt eine vorhandene Version ab, nimmt die Architekturen, die jedes Image seines Locks haben (ohne Backend beide), und schreibt `apps/<id>/<version>/` mit dem Paket, `pithyx.json`, `icon.svg` und den Screenshots.
4. Für `--promote`: prüft die Dateien jeder Version im anderen Ordner und kopiert den Ordner.
5. Schreibt `index.json` (Apps nach ID, Versionen nach SemVer, zwei Leerzeichen, ein abschließender Zeilenumbruch), prüft sie wie eine Box und entfernt `index.json.sig`.

## Dateien

| Datei                                                     | Zugriff                                             |
| --------------------------------------------------------- | --------------------------------------------------- |
| `catalog.json` (oder `--catalog`)                         | gelesen                                             |
| `<branch folder>/index.json`                              | gelesen, geschrieben                                |
| `<branch folder>/index.json.sig`                          | entfernt                                            |
| `<branch folder>/apps/<id>/<version>/`                    | zur Prüfung gelesen; für neue Versionen geschrieben |
| die Pakete aus `--add`, `<source>/apps/<id>/screenshots/` | gelesen                                             |
| `<promote folder>/index.json` und seine Versionsordner    | gelesen                                             |

## Exit-Codes

| Code | Wann                                                                                                                                                                                                                                                                                                                                               |
| ---- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 0    | `Added <id> <version>, ...` oder `Nothing new; the index has a new sequence.`                                                                                                                                                                                                                                                                      |
| 1    | Ein Befund (nach dem Grund ausgegeben), `--channel` fehlt oder ist weder `stable` noch `beta`, `--add` ohne `--source`, `--repository` und `--commit`, eine schon veröffentlichte Version, ein Paket ohne `pithyx.json` oder `icon.svg` oder mit ungültigem Lock, keine Architektur mit jedem Image, oder `--promote` aus einem Ordner ohne Index. |
| 2    | Kein Branch-Ordner, oder mehr als einer.                                                                                                                                                                                                                                                                                                           |

## Befunde

`store.catalog.*` für `catalog.json` und die Indexe, `store.package.hash_mismatch` für geänderte Dateien und `app.manifest.*` für das Manifest eines Pakets. Siehe [Befunde](/de/docs/reference/cli/findings/).

## Beispiele

```sh
# Was die CI aus master gebaut hat, zu catalog-next hinzufügen
pithyx catalog index catalog-next --channel beta \
  --add out/com.example.notes-1.2.0.pithyxapp \
  --source . --repository https://github.com/example/apps --commit "$GITHUB_SHA"

# Alles aus catalog-next nach catalog übernehmen
pithyx catalog index catalog --channel stable --promote catalog-next
```
