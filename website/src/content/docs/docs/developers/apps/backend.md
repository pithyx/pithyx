---
title: 'Backends'
description: 'Containers behind the proxy: the Compose subset, endpoints, users, storage and network.'
status: draft
audience: developer
sidebar:
  order: 4
---

An app's backend is a Docker Compose file in the package that the daemon checks against an allowlist, rewrites and runs. Browsers reach it only through the daemon's proxy, every request carries the signed-in user's identity in headers the proxy sets, and the containers run on a private network, with their own user id, on the pool the administrator chose.

## Compose subset

The file is `backend/compose.yaml`. The rules are in `sdk/schema/compose.v1.rules.json`, shared by the CLI and the daemon, so `pithyx validate` gives the box's answer. A key that is not on the list is refused ([`app.compose.key_unsupported`](/docs/reference/errors/#app.compose.key_unsupported)). The box rewrites the file before it runs it.

**Limits:** 65,536 bytes per file, nesting depth 10, 256 entries, 16 services, 32 volumes, memory limits from 6 MiB to 256 GiB, at most 256 CPUs. YAML anchors, aliases, tags, merge keys, duplicate keys and `x-*` keys are refused.

**Top level:** `services` (required) and `volumes`.

**Per service:**

- `image` is required and pinned by digest (`name@sha256:...`).
- Allowed: `command`, `entrypoint`, `environment`, `working_dir`, `volumes`, `tmpfs`, `depends_on`, `healthcheck`, `expose`, `read_only`, `init`, `stop_signal`, `stop_grace_period`, `shm_size`, `mem_limit`, `cpus` and `labels` (not with the prefixes `com.pithyx` or `com.docker`).
- Only with a matching elevated right: `network_mode: host`, `pid: host`, `devices`, `cap_add`, `cap_drop` and `privileged`.
- Mounts are named volumes or `tmpfs`. Bind mounts are refused because host paths are never mounted. The targets `/proc`, `/sys` and `/dev` and the Docker sockets are refused.
- Refused with a reason in the error text: `ports` (backends are reached only through the proxy), `build` (the box never builds), `networks`, `links`, `env_file`, `secrets`, `configs`, `user` (the box sets the user id), `restart` (the box runs the lifecycle), `logging`, `deploy` (use `mem_limit` and `cpus`), `security_opt`, `sysctls`, `ulimits`, `container_name`, `extends`, `profiles` and more.

**Source form.** During development you may keep `backend/compose.dev.yaml`, which follows the same rules plus `build:` (a string or `context`, `dockerfile`, `target`, `args`). Each service has exactly one of `image` or `build`. `pithyx build` turns it into `compose.yaml` and `images.lock.json`. The box never reads the dev file.

**Image lock.** `backend/images.lock.json` lists, for each image (up to 16), the manifest and config digests for `amd64` and `arm64`. The box checks every image it loads against the config digest ([`app.image.mismatch`](/docs/reference/errors/#app.image.mismatch)). A missing or stale lock is [`app.package.lock_invalid`](/docs/reference/errors/#app.package.lock_invalid). Images come from `backend/images/<arch>/<digest>.tar` in the package or are pulled by digest.

**What the box adds when it rewrites the file:**

- `user: <uid>:<uid>` (or `0:0` with `backend.root`), and the group ids of mapped shared folders,
- a private network for the app, and a second one with internet access only if the app has `network.internet`,
- `restart: "no"` (the daemon starts and stops the backend), logging to journald, `pids_limit: 1024`, `oom_score_adj: 500`, `no-new-privileges`,
- labels `com.pithyx.app`, `com.pithyx.service` and `com.pithyx.version`,
- named volumes as folders under `<pool>/.pithyx/apps/<id>/volumes/<name>`, and mapped shared folders as bind mounts (read-only if the administrator granted less).

## Endpoints and routes

An endpoint maps a name to a service and a container port in the manifest. The route never contains the port:

- endpoint `api`: `/apps/<id>/api/...`
- endpoint `<name>`: `/apps/<id>/api-<name>/...`

The path after the prefix is forwarded. The SDK offers `app.backend.fetch(path, { endpoint })`, `url`, `socket` and `token`; see [@pithyx/sdk](/docs/sdk/sdk/#backend).

**Authentication at the proxy.** HTTP requests carry `Authorization: Bearer <token>` or `?pithyx-token=<token>`. WebSockets carry a one-time `?pithyx-ticket=<ticket>` for that endpoint. A cookie never authenticates. A token for another app, or a bad one, gets 401 [`app.token.invalid`](/docs/reference/errors/#app.token.invalid).

**Behavior:**

- A browser preflight (`OPTIONS` with `Origin: null`) is answered with `Access-Control-Allow-Origin: *`; other origins get 403.
- Request bodies have no size limit at the proxy; the backend decides.
- The connect timeout is 5 seconds. Idle WebSockets are kept for up to 24 hours of inactivity.
- If the backend is stopped or starting, the request wakes it and waits up to 30 seconds. If it is not running, updating or unavailable, the answer is 503 [`app.backend.unavailable`](/docs/reference/errors/#app.backend.unavailable).
- The proxy removes headers that would be dangerous from the answer (for example `Set-Cookie`, `Clear-Site-Data`, HSTS) and adds the sandbox headers.

## Who is calling

The proxy strips `Cookie`, `Authorization`, `Proxy-Authorization`, `Referer`, `Forwarded`, any `X-Pithyx-*` and `X-Forwarded-*` header and the `pithyx-token` and `pithyx-ticket` query parameters from the incoming request. It then sets:

| Header                               | Value                                       |
| ------------------------------------ | ------------------------------------------- |
| `X-Pithyx-User`                      | the user name                               |
| `X-Pithyx-User-Id`                   | the user id                                 |
| `X-Pithyx-Groups`                    | the group names, comma separated            |
| `X-Pithyx-Admin`                     | `true` or `false`                           |
| `traceparent`                        | the trace of the request                    |
| `X-Forwarded-Prefix`                 | `/apps/<id>/api` or `/apps/<id>/api-<name>` |
| `X-Forwarded-For`, `-Host`, `-Proto` | the original client and host                |

A backend can trust these headers only because it is reachable solely through the proxy.

**Tokens.** The desktop requests them with `POST /api/apps/{appId}/tokens` (cookie and CSRF header). A token lasts 5 minutes, has 256 random bits and is bound to the user, session, app and window. `host` tokens work only for `/api/app/**`, `backend` tokens only for the proxy. A ticket is valid for 30 seconds and one use. Every use checks the session, user and grant again; revoking a permission or signing out closes proxied sockets. Tokens and tickets are masked in logs.

## Storage

- Each app has a dataset on the app pool the administrator picked at the first install: `/<pool>/.pithyx/apps/<id>/` with `versions/<version>`, `volumes/` and run files. Docker's own data is on the same pool. Nothing is stored on the system disk.
- Declare top-level `volumes:` in Compose and mount them by name; they become folders owned by the app's user id.
- Manifest `storage` requests appear as a Compose volume with the request's `id`. An optional request that nobody mapped becomes a folder inside the app's dataset. A required, unmapped request stops the backend from starting.
- If the pool is locked or missing, the backend does not start and the Store says why.
- An update snapshots the dataset; a rollback restores it. On uninstall the administrator chooses whether to keep the data.
- The SDK's `app.storage` (per user key/value) is separate: keys up to 128 characters, values up to 64 KB, 1 MB per user and app.

## Network

- Each app gets its own internal Docker bridge network without internet. Services reach each other by service name.
- With `network.internet`, every service also joins a second, non-internal network.
- No ports are published on the host, and an nftables rule drops new connections from the app networks to the host.
- Each app has its own user id (and group id) from 40000 to 49999. With `root: true` the user inside the container is root, but user namespace remapping keeps it unprivileged on the host.
- Hardening is always on: `no-new-privileges`, a process limit of 1024, a raised out-of-memory score.
- Docker socket, `privileged` and host networking are refused in this version.

## Lifecycle

- **always:** runs whenever it can.
- **onDemand:** runs while a user holds a window lease, a request or a schedule holds it, and for `idleTimeout` seconds (default 600) after the last one. The desktop subscribes to the topic `apps/<id>/context` once per app and tab; closing the last window ends the lease at once, and a lost connection keeps it for 2 minutes.
- A failed start is retried after 1 minute; opening a window retries at once.
- Start timeouts: Compose `up --wait` waits up to 10 minutes, then the manifest's health check up to 60 seconds.
- Stop: `docker compose down --timeout 30`. Containers get SIGTERM and are killed after 30 seconds; volumes and images stay.
- Schedules wake the backend without a window, call `POST path` (holding the backend for up to 10 minutes) and then let it idle.
- The window sees the status `stopped`, `starting`, `running`, `updating` or `unavailable` (SDK event `backendStatus`).
- Docker starts only while a backend app is installed and its pool is mounted.

Pithyx has no keep-alive lease for background work; use `always` or schedules.
