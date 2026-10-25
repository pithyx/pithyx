---
title: 'pithyx validate'
description: 'pithyx validate checks an app project as the box checks the package built from it, and writes nothing.'
status: ready
audience: developer
sidebar:
  order: 1
---

`pithyx validate` checks an app project as the box would check the package built from it: `pithyx.json`, `icon.svg`, the built frontend, the package layout, the backend's Compose file and its images lock. It writes nothing and needs no network. [`pithyx pack`](/docs/reference/cli/pack/) runs the same checks before it writes a package.

## Synopsis

<!-- generated:cli-synopsis validate -->

```sh
pithyx validate [project folder] [--frontend <folder>] [--allow-reserved-id]
```

<!-- /generated -->

## Options

<!-- generated:cli-options validate -->

| Option                | Value      | Type    | Default | Description                                                                                                                                                                                                                           |
| --------------------- | ---------- | ------- | ------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `--frontend`          | `<folder>` | string  | `dist`  | The built frontend                                                                                                                                                                                                                    |
| `--allow-reserved-id` | –          | boolean | off     | Let an id start with org.pithyx., which is reserved for the system apps and the official catalog: the app's id (validate, lock, pack, dev; a box still refuses it in a package from a file) or the id of catalog.json (catalog check) |

<!-- /generated -->

## What it checks

1. `pithyx.json` against the [manifest schema](/docs/reference/manifest-schema/) and the rules beyond it (id, version ranges, window sizes, references, schedules, elevated rights).
2. `icon.svg` with the box's icon rules. A warning `cli.icon.template` while it is still the template icon of `@pithyx/cli`.
3. The built frontend folder. Hidden files and folders (names starting with `.`) stay out of the package with a warning `cli.frontend.skipped`; links and special files are errors (`app.package.invalid`, reason `entry_type`); a missing folder is the error `cli.frontend.missing`.
4. The package layout as `pack` would write it, for example that the manifest's `entry` exists below `frontend/`.
5. With a backend: the Compose file named in `backend.compose` against the [Compose rules](/docs/reference/compose-rules/) and the manifest, then `backend/images.lock.json`: it must exist (`cli.lock.missing`), parse (`cli.lock.invalid`) and list exactly the images of the Compose file (`cli.lock.stale`). An image without an amd64 or an arm64 entry is a warning (`cli.lock.platform_missing`): the app will not install on boxes of that architecture. A Compose `healthcheck` next to the manifest's `health` is a warning (`cli.compose.healthcheck_redundant`): it delays every start and keeps the idle backend busy. Before `pithyx build` the project may have only `backend/compose.dev.yaml`: then `validate` checks that source form against the same rules (with `build` in place of `image`) and warns `cli.backend.unbuilt` instead of checking the lock, so it needs no Docker. `pack` still needs the built `compose.yaml`.

The lock is checked only when the Compose file passes. Warnings never change the exit code.

## Files

| File                                                                              | Access                                 |
| --------------------------------------------------------------------------------- | -------------------------------------- |
| `pithyx.json`, `icon.svg`                                                         | read                                   |
| the frontend folder (`--frontend`, default `dist`), every file below it           | read                                   |
| `README.md`, `README`, `LICENSE`, `LICENSE.md`, `LICENSE.txt` at the project root | listed for the package when they exist |
| the Compose file of `backend.compose`, `backend/images.lock.json`                 | read                                   |

## Exit codes

| Code | When                                                            |
| ---- | --------------------------------------------------------------- |
| 0    | No finding of level error. It prints `<id> <version> is valid.` |
| 1    | At least one error.                                             |
| 2    | More than one project folder.                                   |

A folder without `pithyx.json` fails with the error `cli.manifest.missing` and exit code 1, also when it has a built frontend; `pack` and `lock` refuse it the same way.

## Findings

Errors with the box's codes `app.manifest.*`, `app.icon.invalid`, `app.package.invalid`, `app.package.layout_invalid`, `app.compose.*` and `app.elevated.unsupported`, plus the CLI's own `cli.manifest.missing`, `cli.frontend.*`, `cli.icon.template` and `cli.lock.*`. Each one with its message is listed under [Findings](/docs/reference/cli/findings/).

## Examples

```sh
# The project in the current folder, frontend built to dist/
pithyx validate

# Another project whose build goes to build/
pithyx validate ../photo-app --frontend build
```
