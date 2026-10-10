---
title: 'pithyx catalog index'
description: 'pithyx catalog index adds built packages, or promotes the versions of another branch, and writes index.json with the next sequence.'
status: ready
audience: developer
sidebar:
  order: 8
---

`pithyx catalog index` writes a catalog branch (`catalog-next` for beta, `catalog` for stable): it puts new versions into the branch folder and writes `index.json` with the next sequence number. A published version never changes. The old signature is removed; sign the new index with [`catalog sign`](/docs/reference/cli/catalog-sign/) afterwards.

## Synopsis

<!-- generated:cli-synopsis catalog index -->

```sh
pithyx catalog index <branch folder> --channel stable|beta (--add <package>... --source <checkout> --repository <url> --commit <sha> | --promote <folder>) [--catalog <file>]
```

<!-- /generated -->

## Options

<!-- generated:cli-options catalog index -->

| Option         | Value          | Type               | Default             | Description                                  |
| -------------- | -------------- | ------------------ | ------------------- | -------------------------------------------- |
| `--catalog`    | `<file>`       | string             | `catalog.json`      | The catalog.json                             |
| `--channel`    | `stable\|beta` | string             | required            | Not described in `pithyx --help`; see below. |
| `--add`        | `<package>`    | string, repeatable | see the forms below | Not described in `pithyx --help`; see below. |
| `--source`     | `<checkout>`   | string             | see the forms below | Not described in `pithyx --help`; see below. |
| `--repository` | `<url>`        | string             | see the forms below | Not described in `pithyx --help`; see below. |
| `--commit`     | `<sha>`        | string             | see the forms below | Not described in `pithyx --help`; see below. |
| `--promote`    | `<folder>`     | string             | see the forms below | Not described in `pithyx --help`; see below. |

<!-- /generated -->

The branch folder is the checkout of the branch to write. It has two forms:

- **Add** (`--add <package>...` with `--source`, `--repository` and `--commit`): `--add` names a built `.pithyxapp` (repeatable). `--source <checkout>` is the checkout of `master` the packages were built from; screenshots come from `<source>/apps/<id>/screenshots/`. `--repository <url>` and `--commit <sha>` go into each version's `source`.
- **Promote** (`--promote <folder>`): copies every version of another branch folder (usually `catalog-next` into `catalog`) with its entry, skipping versions the branch has.

Both forms can be combined; without either the index gets only a new sequence. `--channel` is `stable` or `beta` and must match the branch. `--catalog <file>` names the catalog file (default `catalog.json`); its id goes into the index.

## What it does

1. Reads `catalog.json` and the branch's current `index.json` (if any) and checks it for this catalog and channel.
2. Checks every file of every version already in the index against its entry (`store.package.hash_mismatch`).
3. For each `--add` package: reads `pithyx.json` and `icon.svg` from it, validates the manifest (ids in `org.pithyx.*` only for `org.pithyx.official`), refuses a version that exists, takes the architectures that have every image of its lock (both without a backend), and writes `apps/<id>/<version>/` with the package, `pithyx.json`, `icon.svg` and the screenshots.
4. For `--promote`: checks each version's files in the other folder and copies the folder.
5. Writes `index.json` (apps by id, versions by SemVer, two spaces, a final line break), checks it as a box would, and removes `index.json.sig`.

## Files

| File                                                       | Access                                      |
| ---------------------------------------------------------- | ------------------------------------------- |
| `catalog.json` (or `--catalog`)                            | read                                        |
| `<branch folder>/index.json`                               | read, written                               |
| `<branch folder>/index.json.sig`                           | removed                                     |
| `<branch folder>/apps/<id>/<version>/`                     | read for checking; written for new versions |
| the packages of `--add`, `<source>/apps/<id>/screenshots/` | read                                        |
| `<promote folder>/index.json` and its version folders      | read                                        |

## Exit codes

| Code | When                                                                                                                                                                                                                                                                                                                                         |
| ---- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 0    | `Added <id> <version>, ...` or `Nothing new; the index has a new sequence.`                                                                                                                                                                                                                                                                  |
| 1    | A finding (printed after the reason), `--channel` missing or not `stable` or `beta`, `--add` without `--source`, `--repository` and `--commit`, a version that is published already, a package without `pithyx.json` or `icon.svg` or with an invalid lock, no architecture with every image, or `--promote` from a folder without an index. |
| 2    | No branch folder, or more than one.                                                                                                                                                                                                                                                                                                          |

## Findings

`store.catalog.*` for `catalog.json` and the indexes, `store.package.hash_mismatch` for changed files, and `app.manifest.*` for a package's manifest. See [Findings](/docs/reference/cli/findings/).

## Examples

```sh
# Add what CI built from master to catalog-next
pithyx catalog index catalog-next --channel beta \
  --add out/com.example.notes-1.2.0.pithyxapp \
  --source . --repository https://github.com/example/apps --commit "$GITHUB_SHA"

# Promote everything in catalog-next to catalog
pithyx catalog index catalog --channel stable --promote catalog-next
```
