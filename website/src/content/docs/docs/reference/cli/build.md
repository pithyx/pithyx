---
title: 'pithyx build'
description: 'pithyx build builds the backend services of compose.dev.yaml with Docker and pins compose.yaml and the images lock to them.'
status: ready
audience: developer
sidebar:
  order: 3
---

`pithyx build` builds the services of `backend/compose.dev.yaml` that have `build:` and writes the pinned `backend/compose.yaml` and `backend/images.lock.json` from it. `compose.dev.yaml` is the source form of the backend: the same [Compose rules](/docs/reference/compose-rules/) plus `build`. The box never reads it and never builds. Catalogs build from it in their CI with `--oci-out` and `--pin`.

## Synopsis

<!-- generated:cli-synopsis build -->

```sh
pithyx build [project folder] [--frontend <folder>] [--oci-out <folder>] [--pin <prefix>] [--allow-reserved-id]
```

<!-- /generated -->

## Options

<!-- generated:cli-options build -->

| Option                | Value      | Type    | Default | Description                                                                                                                                                                                                                                  |
| --------------------- | ---------- | ------- | ------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `--frontend`          | `<folder>` | string  | `dist`  | The built frontend                                                                                                                                                                                                                           |
| `--oci-out`           | `<folder>` | string  | –       | Write OCI archives &lt;service&gt;.tar for amd64 and arm64 there                                                                                                                                                                             |
| `--pin`               | `<prefix>` | string  | –       | Pin to the archives of --oci-out as &lt;prefix&gt;/&lt;id&gt;/&lt;service&gt;:&lt;version&gt;                                                                                                                                                |
| `--allow-reserved-id` | –          | boolean | off     | Let an id start with org.pithyx., which is reserved for the system apps and the official catalog: the app's id (validate, lock, build, pack, dev; a box still refuses it in a package from a file) or the id of catalog.json (catalog check) |

<!-- /generated -->

## Modes

**Local (no options).** For each service with `build`, in the order of the file: `docker build` for this machine's architecture (amd64 or arm64, others fail), tagged `pithyx.local/<id>/<service>:<version>`, then `docker save` to read the manifest digest (Docker 25 or newer, which saves an OCI layout). The pinned file names `pithyx.local/<id>/<service>:<version>@sha256:...`. Such images exist only in your Docker: put them into the package with [`pithyx pack --images`](/docs/reference/cli/pack/).

**Reproducible (`--oci-out <folder>`).** For each service `docker buildx build --platform linux/amd64,linux/arm64` without provenance and SBOM into the OCI archive `<folder>/<service>.tar`, with timestamps rewritten to `SOURCE_DATE_EPOCH` (from the environment, else the time of the last Git commit of the project; without either it fails). Nothing is pushed and `compose.yaml` is not written.

**Pin (`--oci-out <folder> --pin <prefix>`).** Builds nothing. It reads the archives of `--oci-out` and pins each service to `<prefix>/<id>/<service>:<version>@<digest>`, as the images will be found after you push the archives there. `--pin` without `--oci-out` fails.

In the local and the pin mode, services that only name an `image` keep it and are resolved from their registry as by [`pithyx lock`](/docs/reference/cli/lock/). The written `compose.yaml` starts with the comment `Written by pithyx build from compose.dev.yaml; edit that file and build again.` and keeps the other comments of the source. Before writing, the pinned file must pass the box's own check.

## Files

| File                                                                                     | Access                                                       |
| ---------------------------------------------------------------------------------------- | ------------------------------------------------------------ |
| `pithyx.json`                                                                            | read; it must be valid and have a backend                    |
| `backend/compose.dev.yaml` (next to `backend.compose`, with `.dev` before the extension) | read and checked with the rules of the source form           |
| the build contexts and Dockerfiles it names                                              | read by Docker                                               |
| `backend/compose.yaml` (the path of `backend.compose`) and `backend/images.lock.json`    | written in the local and the pin mode                        |
| `<folder>/<service>.tar`                                                                 | written with `--oci-out`, read with `--pin`                  |
| a temporary folder `pithyx-build-*`                                                      | holds the saved images in the local mode, removed afterwards |

## Exit codes

| Code | When                                                                                                                                                                                                                                                                                    |
| ---- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 0    | Built (it prints one line per service) or pinned.                                                                                                                                                                                                                                       |
| 1    | `pithyx.json` is not valid, the app has no backend, `compose.dev.yaml` is missing or breaks the rules (its messages follow), Docker or a registry fails, the machine is not amd64 or arm64, `--pin` without `--oci-out`, or no `SOURCE_DATE_EPOCH` and no Git checkout for `--oci-out`. |
| 2    | More than one project folder.                                                                                                                                                                                                                                                           |

## Findings

Problems of `compose.dev.yaml` and of the pinned file are printed as the box's messages after `pithyx: <file>:`, one per line; their codes are `app.compose.*` and `app.elevated.unsupported` ([Findings](/docs/reference/cli/findings/)).

## Examples

```sh
# Build for this machine and pack the images for an offline install
pithyx build && pithyx pack --images

# What a catalog's CI does: build both architectures, push, pin
pithyx build --oci-out out
# ... push out/<service>.tar to ghcr.io/example/apps/<id>/<service>:<version>
pithyx build --oci-out out --pin ghcr.io/example/apps
```
