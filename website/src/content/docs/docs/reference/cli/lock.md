---
title: 'pithyx lock'
description: 'pithyx lock resolves the images of the backend Compose file from their registries and writes backend/images.lock.json.'
status: ready
audience: developer
sidebar:
  order: 2
---

`pithyx lock` resolves every image of the backend's Compose file from its registry and writes `backend/images.lock.json`: per image the platform manifest digest and the image id (config digest) for amd64 and arm64. The box and `pithyx validate` refuse a backend whose lock does not match its Compose file. Use it for backends that only name images; [`pithyx build`](/docs/reference/cli/build/) writes the lock itself for the services it builds.

## Synopsis

<!-- generated:cli-synopsis lock -->

```sh
pithyx lock [project folder] [--frontend <folder>] [--allow-reserved-id]
```

<!-- /generated -->

## Options

<!-- generated:cli-options lock -->

| Option                | Value      | Type    | Default | Description                                                                                                                                                                                                                           |
| --------------------- | ---------- | ------- | ------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `--frontend`          | `<folder>` | string  | `dist`  | The built frontend                                                                                                                                                                                                                    |
| `--allow-reserved-id` | –          | boolean | off     | Let an id start with org.pithyx., which is reserved for the system apps and the official catalog: the app's id (validate, lock, pack, dev; a box still refuses it in a package from a file) or the id of catalog.json (catalog check) |

<!-- /generated -->

`--frontend` is read but changes nothing for `lock`.

## How it resolves

- It reads `pithyx.json` and the Compose file named in `backend.compose`, and takes the `image` of every service (each image once, sorted). Images must be pinned by digest (`name@sha256:...`), as the [Compose rules](/docs/reference/compose-rules/) require.
- For each image it fetches the manifest by its digest. For a multi-platform index it takes the `linux/amd64` and `linux/arm64` entries (for arm64 without a variant or with `v8`) and reads each one's config digest. For a single-platform image it reads the config to learn the architecture.
- An image with neither architecture fails the command. An image with only one is written; `validate` then warns `cli.lock.platform_missing`.
- Registries are asked anonymously, or with the `auths` credentials in `~/.docker/config.json` (or `$DOCKER_CONFIG/config.json`), including the token flow of registries such as Docker Hub and GHCR. Credential helpers are not used.
- It writes the file only when the content changed: `backend/images.lock.json is up to date.` or `Wrote backend/images.lock.json for <n> image(s).`

## Files

| File                                  | Access                        |
| ------------------------------------- | ----------------------------- |
| `pithyx.json`                         | read                          |
| the Compose file of `backend.compose` | read                          |
| `backend/images.lock.json`            | read, written when it changed |

## Exit codes

| Code | When                                                                                                                                                                                                   |
| ---- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| 0    | The lock is written or up to date. Also when the app has no backend: `The app has no backend; there is nothing to lock.`                                                                               |
| 1    | `pithyx.json` is missing or invalid (it prints the errors and `The project is not valid; there is nothing to lock.`), a registry fails or refuses access, or an image has no amd64 and no arm64 image. |
| 2    | More than one project folder.                                                                                                                                                                          |

## Findings

`lock` reports no findings itself; run [`pithyx validate`](/docs/reference/cli/validate/) to see why a manifest is not valid.

## Examples

```sh
pithyx lock
pithyx validate   # checks the new lock against the Compose file
```
