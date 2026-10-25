---
title: 'pithyx catalog check'
description: 'pithyx catalog check runs the pull request rules of a catalog repository on the changes since a base commit.'
status: ready
audience: developer
sidebar:
  order: 6
---

`pithyx catalog check` is the pull request check of a catalog repository: it runs the rules of `catalog.v1.rules.json` on what changed between a base commit and the checkout, with the same verdicts and codes as the box. Run it in the root of the repository at the pull request's head. See [Publishing to a catalog](/docs/developers/apps/publishing/).

## Synopsis

<!-- generated:cli-synopsis catalog check -->

```sh
pithyx catalog check --base <commit> --author <login> [--index <file>] [--allow-reserved-id]
```

<!-- /generated -->

## Options

<!-- generated:cli-options catalog check -->

| Option                | Value      | Type    | Default  | Description                                                                                                                                                                                                                           |
| --------------------- | ---------- | ------- | -------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `--base`              | `<commit>` | string  | required | Not described in `pithyx --help`; see below.                                                                                                                                                                                          |
| `--author`            | `<login>`  | string  | required | Not described in `pithyx --help`; see below.                                                                                                                                                                                          |
| `--index`             | `<file>`   | string  | –        | Not described in `pithyx --help`; see below.                                                                                                                                                                                          |
| `--allow-reserved-id` | –          | boolean | off      | Let an id start with org.pithyx., which is reserved for the system apps and the official catalog: the app's id (validate, lock, pack, dev; a box still refuses it in a package from a file) or the id of catalog.json (catalog check) |

<!-- /generated -->

- `--base <commit>`: the commit the pull request is based on. The changes are `git diff --name-only --no-renames <base>...HEAD`.
- `--author <login>`: the GitHub login of the pull request's author, checked against the app's `maintainers.json` as it was at the base commit (for a new app, against the file the pull request adds).
- `--index <file>`: the `index.json` of the branch `catalog-next`. Its versions of the app are the published ones that a new version must be higher than. Without it, or when the file does not exist (before the first publish), nothing counts as published.
- `--allow-reserved-id`: lets `catalog.json` carry an id in `org.pithyx.*`. Only the project's own catalogs use it. Apps in `org.pithyx.*` are allowed only in the catalog `org.pithyx.official`, with or without this option.

## What it checks

`catalog.json` first, then for the changed files: only one app folder below `apps/`, the folder named like the app's id, the required files, only text and media files of the allowed kinds (no binaries, nothing minified), every `FROM` of a Dockerfile pinned by digest, screenshots (PNG, JPEG or WebP directly in `screenshots/`, lower case names, at most 1 MB each and 8 in all), at most 5 MB for the app folder, an OSI approved license, the manifest fields `license` and `pithyx`, a version above every published one, a valid manifest, icon and `backend/compose.dev.yaml`, and an author who is a maintainer.

## Files

| File                                                | Access                              |
| --------------------------------------------------- | ----------------------------------- |
| `catalog.json` in the current folder                | read                                |
| the Git history of the current folder               | read with `git diff` and `git show` |
| every file of each changed app folder below `apps/` | read                                |
| the file of `--index`                               | read                                |

`--catalog` does not apply: `check` always reads `catalog.json` of the current folder.

## Exit codes

| Code | When                                                                                                  |
| ---- | ----------------------------------------------------------------------------------------------------- |
| 0    | `The pull request passes the catalog rules.`                                                          |
| 1    | At least one finding, `catalog.json` is not valid, or Git fails (for example an unknown base commit). |
| 2    | `--base` or `--author` is missing.                                                                    |

## Findings

All errors: `store.catalog.rule_violation` with the rule as `rule` (`one_app`, `required_file`, `file_type`, `binary`, `minified`, `media`, `unpinned`, `screenshot`, `screenshots`, `app_size`, `folder_id`, `manifest_field`, `license`, `version_not_higher`, `maintainer`, `maintainers_invalid`), the `store.catalog.*` codes of `catalog.json`, and the `app.*` codes of the manifest, the icon and the Compose source. See [Findings](/docs/reference/cli/findings/).

## Examples

```sh
git fetch origin catalog-next
git show origin/catalog-next:index.json > /tmp/index.json
pithyx catalog check --base "$BASE_SHA" --author "$PR_AUTHOR" --index /tmp/index.json
```
