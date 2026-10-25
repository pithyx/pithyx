---
title: 'pithyx catalog check'
description: 'pithyx catalog check prüft die Pull-Request-Regeln eines Katalog-Repositorys auf den Änderungen seit einem Basis-Commit.'
status: ready
audience: developer
sidebar:
  order: 6
---

`pithyx catalog check` ist die Pull-Request-Prüfung eines Katalog-Repositorys: Es prüft die Regeln aus `catalog.v1.rules.json` auf dem, was sich zwischen einem Basis-Commit und dem Checkout geändert hat, mit denselben Urteilen und Codes wie die Box. Starte es im Wurzelordner des Repositorys auf dem Stand des Pull Requests. Siehe [In einem Katalog veröffentlichen](/de/docs/developers/apps/publishing/).

## Aufruf

<!-- generated:cli-synopsis catalog check -->

```sh
pithyx catalog check --base <commit> --author <login> [--index <file>] [--allow-reserved-id]
```

<!-- /generated -->

## Optionen

<!-- generated:cli-options catalog check -->

| Option                | Wert       | Typ     | Standard | Beschreibung                                                                                                                                                                                                                                 |
| --------------------- | ---------- | ------- | -------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `--base`              | `<commit>` | string  | Pflicht  | In `pithyx --help` nicht beschrieben; siehe unten.                                                                                                                                                                                           |
| `--author`            | `<login>`  | string  | Pflicht  | In `pithyx --help` nicht beschrieben; siehe unten.                                                                                                                                                                                           |
| `--index`             | `<file>`   | string  | –        | In `pithyx --help` nicht beschrieben; siehe unten.                                                                                                                                                                                           |
| `--allow-reserved-id` | –          | boolean | aus      | Let an id start with org.pithyx., which is reserved for the system apps and the official catalog: the app's id (validate, lock, build, pack, dev; a box still refuses it in a package from a file) or the id of catalog.json (catalog check) |

<!-- /generated -->

- `--base <commit>`: der Commit, auf dem der Pull Request aufsetzt. Die Änderungen sind `git diff --name-only --no-renames <base>...HEAD`.
- `--author <login>`: der GitHub-Login des Autors des Pull Requests, geprüft gegen die `maintainers.json` der App, wie sie im Basis-Commit war (bei einer neuen App gegen die Datei, die der Pull Request hinzufügt).
- `--index <file>`: die `index.json` des Branches `catalog-next`. Ihre Versionen der App sind die veröffentlichten, über denen eine neue Version liegen muss. Ohne sie, oder wenn die Datei nicht existiert (vor der ersten Veröffentlichung), gilt nichts als veröffentlicht.
- `--allow-reserved-id`: erlaubt `catalog.json` eine ID in `org.pithyx.*`. Nur die Kataloge des Projekts nutzen das. Apps in `org.pithyx.*` sind nur im Katalog `org.pithyx.official` erlaubt, mit und ohne diese Option.

## Was es prüft

Zuerst `catalog.json`, dann für die geänderten Dateien: nur ein App-Ordner unter `apps/`, der Ordner heißt wie die ID der App, die Pflichtdateien, nur Text- und Mediendateien der erlaubten Arten (keine Binärdateien, nichts Minifiziertes), jedes `FROM` eines Dockerfiles per Digest gepinnt, Screenshots (PNG, JPEG oder WebP direkt in `screenshots/`, kleingeschriebene Namen, je höchstens 1 MB und insgesamt höchstens 8), höchstens 5 MB für den App-Ordner, eine von der OSI anerkannte Lizenz, die Manifest-Felder `license` und `pithyx`, eine Version über jeder veröffentlichten, ein gültiges Manifest, Icon und `backend/compose.dev.yaml`, und ein Autor, der Maintainer ist.

## Dateien

| Datei                                                 | Zugriff                               |
| ----------------------------------------------------- | ------------------------------------- |
| `catalog.json` im aktuellen Ordner                    | gelesen                               |
| die Git-Historie des aktuellen Ordners                | gelesen mit `git diff` und `git show` |
| jede Datei jedes geänderten App-Ordners unter `apps/` | gelesen                               |
| die Datei aus `--index`                               | gelesen                               |

`--catalog` gilt hier nicht: `check` liest immer `catalog.json` im aktuellen Ordner.

## Exit-Codes

| Code | Wann                                                                                                                     |
| ---- | ------------------------------------------------------------------------------------------------------------------------ |
| 0    | `The pull request passes the catalog rules.`                                                                             |
| 1    | Mindestens ein Befund, `catalog.json` ist ungültig, oder Git scheitert (zum Beispiel an einem unbekannten Basis-Commit). |
| 2    | `--base` oder `--author` fehlt.                                                                                          |

## Befunde

Alles Fehler: `store.catalog.rule_violation` mit der Regel als `rule` (`one_app`, `required_file`, `file_type`, `binary`, `minified`, `media`, `unpinned`, `screenshot`, `screenshots`, `app_size`, `folder_id`, `manifest_field`, `license`, `version_not_higher`, `maintainer`, `maintainers_invalid`), die `store.catalog.*`-Codes von `catalog.json` und die `app.*`-Codes von Manifest, Icon und Compose-Quelle. Siehe [Befunde](/de/docs/reference/cli/findings/).

## Beispiele

```sh
git fetch origin catalog-next
git show origin/catalog-next:index.json > /tmp/index.json
pithyx catalog check --base "$BASE_SHA" --author "$PR_AUTHOR" --index /tmp/index.json
```
