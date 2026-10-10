---
title: 'pithyx catalog pending'
description: 'pithyx catalog pending prints, as JSON, the app versions of a catalog repository that catalog-next does not list yet.'
status: ready
audience: developer
sidebar:
  order: 7
---

`pithyx catalog pending` lists the app versions in `apps/` of a catalog repository that the index of `catalog-next` does not have yet, as JSON on standard output. A catalog's CI builds exactly these.

## Synopsis

<!-- generated:cli-synopsis catalog pending -->

```sh
pithyx catalog pending [--index <file>]
```

<!-- /generated -->

## Options

<!-- generated:cli-options catalog pending -->

| Option    | Value    | Type   | Default | Description                                  |
| --------- | -------- | ------ | ------- | -------------------------------------------- |
| `--index` | `<file>` | string | –       | Not described in `pithyx --help`; see below. |

<!-- /generated -->

- `--index <file>`: the `index.json` of `catalog-next`. Without it, or when the file does not exist, every app version is pending.

## Output

A JSON array, apps in folder order, one line:

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

`backend` says whether the manifest has a backend, that is whether CI must build images. Folders without `pithyx.json` are skipped. The manifests are not validated here; [`catalog check`](/docs/reference/cli/catalog-check/) did that for the pull request.

## Files

| File                                       | Access |
| ------------------------------------------ | ------ |
| `apps/*/pithyx.json` in the current folder | read   |
| the file of `--index`                      | read   |

## Exit codes

| Code | When                                               |
| ---- | -------------------------------------------------- |
| 0    | The list is printed, also when it is empty (`[]`). |
| 1    | A `pithyx.json` or the index is not valid JSON.    |

## Findings

None.

## Examples

```sh
pithyx catalog pending --index catalog-next/index.json
```
