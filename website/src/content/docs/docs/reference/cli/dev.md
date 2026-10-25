---
title: 'pithyx dev'
description: 'pithyx dev runs the mock desktop with the app in the box sandbox and a proxy to its backend, for development without a box.'
status: ready
audience: developer
sidebar:
  order: 5
---

`pithyx dev` runs a mock desktop on your machine: your built frontend in the same sandbox as on the box, the SDK's host side, and a proxy to the backend that behaves like the box's. The backend runs with `docker compose`. There is no development mode on the box; with Vite use the plugin `@pithyx/cli/vite` and run `vite` instead ([@pithyx/cli](/docs/sdk/cli/)).

## Synopsis

<!-- generated:cli-synopsis dev -->

```sh
pithyx dev [project folder] [--frontend <folder>] [--port <port>] [--host <host>] [--endpoint <n=url>]... [--no-docker] [--allow-reserved-id]
```

<!-- /generated -->

## Options

<!-- generated:cli-options dev -->

| Option                | Value      | Type               | Default     | Description                                                                                                                                                                                                                           |
| --------------------- | ---------- | ------------------ | ----------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `--frontend`          | `<folder>` | string             | `dist`      | The built frontend                                                                                                                                                                                                                    |
| `--port`              | `<port>`   | string             | `7400`      | The port                                                                                                                                                                                                                              |
| `--host`              | `<host>`   | string             | `127.0.0.1` | The address to listen on                                                                                                                                                                                                              |
| `--endpoint`          | `<n=url>`  | string, repeatable | –           | Reach the endpoint n at url instead of through Docker, for example --endpoint api=http://localhost:8080 (repeatable)                                                                                                                  |
| `--no-docker`         | –          | boolean            | off         | Do not start the backend; endpoints without --endpoint answer 503                                                                                                                                                                     |
| `--allow-reserved-id` | –          | boolean            | off         | Let an id start with org.pithyx., which is reserved for the system apps and the official catalog: the app's id (validate, lock, pack, dev; a box still refuses it in a package from a file) or the id of catalog.json (catalog check) |

<!-- /generated -->

`--port` must be an integer from 1 to 65535. An IPv6 `--host` is printed in brackets. `--endpoint` names an endpoint the manifest declares and a URL, for example `api=http://localhost:8080`; an undeclared name fails.

## What runs

- The page is served at `http://<host>:<port>/`, the frontend as `/apps/<id>/` with the box's headers. Files are read on each request: rebuild and reload to see a change.
- The backend starts when the manifest has one, some endpoint has no `--endpoint`, and `--no-docker` is not given: `docker compose --project-name pithyx-dev-<id with _ for dots> --file <compose> --file <override> up --detach --wait --build`. `<compose>` is `backend/compose.dev.yaml` when it exists, else the file of `backend.compose`. The override publishes each endpoint's port on `127.0.0.1` at a port Docker picks; the proxy reaches it there.
- With `--no-docker`, endpoints without `--endpoint` answer 503.
- Ctrl+C or SIGTERM stops the server, runs `docker compose down` (volumes stay) and exits with 0.

## Files

| File                                                        | Access                                 |
| ----------------------------------------------------------- | -------------------------------------- |
| `pithyx.json`                                               | read; it must exist and be valid       |
| `icon.svg`, the frontend folder                             | read on each request                   |
| `backend/compose.dev.yaml` or the file of `backend.compose` | read by `docker compose`               |
| a temporary folder `pithyx-dev-*` with `compose.ports.yaml` | written at start, removed after `down` |

## Exit codes

| Code | When                                                                                                                                          |
| ---- | --------------------------------------------------------------------------------------------------------------------------------------------- |
| 0    | Stopped with Ctrl+C or SIGTERM.                                                                                                               |
| 1    | `pithyx.json` is missing or not valid, `--endpoint` is malformed or names an undeclared endpoint, Docker fails, or the port cannot be opened. |
| 2    | `--port` is not a port, or more than one project folder.                                                                                      |

## Findings

`dev` prints no findings. Run [`pithyx validate`](/docs/reference/cli/validate/) to see why a manifest is not valid.

## Examples

```sh
pithyx dev                                         # http://127.0.0.1:7400/
pithyx dev --port 8000 --host 0.0.0.0              # reachable from other machines
pithyx dev --endpoint api=http://localhost:8080    # your own server for the endpoint api
pithyx dev --no-docker                             # frontend only
```
