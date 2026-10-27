---
title: 'Backends'
description: 'Containers behind the proxy: the Compose subset, endpoints, users, storage and network.'
status: ready
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
- Allowed: `command`, `entrypoint`, `environment`, `working_dir`, `volumes`, `tmpfs`, `depends_on`, `healthcheck`, `expose`, `read_only`, `init`, `stop_signal`, `stop_grace_period`, `shm_size`, `mem_limit`, `cpus`, `cap_drop` and `labels` (not with the prefixes `com.pithyx` or `com.docker`).
- Needs the matching special right in the manifest's `permissions` (since Pithyx 1.2.0): `network_mode: host` (`host.network`), `pid: host` (`host.pid`), `devices` (`device:<host path>` per device or device class), a mount of the Docker socket (`docker.socket` or `docker.socket.raw`) and `cap_add` (`capability:<NAME>` per capability, only with `root: true`). `privileged` is refused with `app.elevated.unsupported`. See [Elevated rights](/docs/reference/compose-rules/#elevated-rights).
- Mounts are named volumes or `tmpfs`. Bind mounts are refused because host paths are never mounted. The targets `/proc`, `/sys` and `/dev` and the Docker sockets are refused.
- Refused with a reason in the error text: `ports` (backends are reached only through the proxy), `build` (the box never builds), `networks`, `links`, `env_file`, `secrets`, `configs`, `user` (the box sets the user id), `restart` (the box runs the lifecycle), `logging`, `deploy` (use `mem_limit` and `cpus`), `security_opt`, `sysctls`, `ulimits`, `container_name`, `extends`, `profiles` and more.

**Source form.** During development you may keep `backend/compose.dev.yaml`, which follows the same rules plus `build:` (a string or `context`, `dockerfile`, `target`, `args`). Each service has exactly one of `image` or `build`. `pithyx build` turns it into `compose.yaml` and `images.lock.json`. The box never reads the dev file.

**Image lock.** `backend/images.lock.json` lists, for each image (up to 16), the manifest and config digests for `amd64` and `arm64`. The box checks every image it loads against the config digest ([`app.image.mismatch`](/docs/reference/errors/#app.image.mismatch)). A missing or stale lock is [`app.package.lock_invalid`](/docs/reference/errors/#app.package.lock_invalid). Images come from `backend/images/<arch>/<digest>.tar` in the package or are pulled by digest.

**What the box adds when it rewrites the file:**

- `user: <uid>:<uid>` (or `0:0` with `backend.root`), and the group ids of mapped shared folders,
- a private network for the app, and a second one out of the box only if the app has `network.internet` or `network.lan` (for the services in `backend.network`, all without it),
- `cap_drop: [ALL]` for every service unless the app runs as root (`backend.root`), which keeps Docker's default set; your own `cap_drop` is replaced,
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

| Header                               | Value                                                                                                         |
| ------------------------------------ | ------------------------------------------------------------------------------------------------------------- |
| `X-Pithyx-User`                      | the user name, the Linux user name in lower case                                                              |
| `X-Pithyx-User-Id`                   | the user id: the Linux uid, the same as `context.user.id` and `DirectoryUser.id`, never given to another user |
| `X-Pithyx-Groups`                    | the group names, comma separated; empty for a user in no group                                                |
| `X-Pithyx-Admin`                     | `true` or `false`                                                                                             |
| `traceparent`                        | the trace of the request                                                                                      |
| `X-Forwarded-Prefix`                 | `/apps/<id>/api` or `/apps/<id>/api-<name>`                                                                   |
| `X-Forwarded-For`, `-Host`, `-Proto` | the original client and host                                                                                  |

An incoming `traceparent` or `tracestate` is dropped as well: the proxy writes the trace of its own request, so a window cannot choose it. Every endpoint and every WebSocket upgrade gets the same headers. A backend can trust these headers only because it is reachable solely through the proxy.

**Time limits of the proxy.** It gives up connecting to a backend after 5 seconds and waits up to 100 seconds for each read of the answer (a long-running answer that keeps sending stays open); then the window gets 502 or 504. An idle WebSocket stays open for 24 hours.

**Schedules.** A schedule's call does not come through the proxy and has no user: the daemon sends `POST <path>` with an empty body and only `X-Pithyx-Schedule: <schedule id>` and `traceparent`, without any user header. Because the proxy strips every incoming `X-Pithyx-*` header, a request from a window can never carry `X-Pithyx-Schedule`, so a backend can rely on it: accept a schedule path only with that header, and answer it without one with 403. A run shows in the Log Center (source `daemon`, with its status code), not in the job list.

**Tokens.** The desktop requests them with `POST /api/apps/{appId}/tokens` (cookie and CSRF header). A token lasts 5 minutes, has 256 random bits and is bound to the user, session, app and window. `host` tokens work only for `/api/app/**`, `backend` tokens only for the proxy. A ticket is valid for 30 seconds and one use. Every use checks the session, user and grant again; revoking a permission or signing out closes proxied sockets. Tokens and tickets are masked in logs.

## Storage

- Each app has a dataset on the app pool the administrator picked at the first install: `/<pool>/.pithyx/apps/<id>/` with `versions/<version>`, `volumes/` and run files. Docker's own data is on the same pool. Nothing is stored on the system disk.
- Declare top-level `volumes:` in Compose and mount them by name; they become folders owned by the app's user id.
- Manifest `storage` requests appear as a Compose volume with the request's `id`. An optional request that nobody mapped becomes a folder inside the app's dataset. A required, unmapped request stops the backend from starting; the window then sees `unavailable` with the reason `storage`.
- If the pool is locked or missing, the backend does not start, the Store says why, and the window sees `unavailable` with the reason `pool`.
- An update snapshots the dataset; a rollback restores it. On uninstall the administrator chooses whether to keep the data.
- The SDK's `app.storage` (per user key/value) is separate: keys up to 128 characters, values up to 64 KB, 1 MB per user and app.

A storage request in the manifest and its volume in Compose, for one service:

```json
"storage": [{ "id": "clips", "name": { "en": "Clips" }, "access": "readWrite", "required": true, "services": ["api"] }]
```

```yaml
services:
  api:
    volumes:
      - clips:/clips
volumes:
  clips:
```

What the backend sees in such a folder:

- **It cannot tell mapped from unmapped.** An optional request nobody mapped is a folder inside the app's dataset, and the service sees an empty or app-owned folder either way. Only the window knows (`app.context.storage`, `storageChange`); a backend that must know asks its frontend or keeps a marker file of its own.
- **Mapping later starts over.** When an administrator maps an optional request later, the backend restarts with the new folder. What it wrote into the internal folder stays there and is not moved; copy it over yourself if it matters.
- **Files belong to the app and follow the shared folder's rights.** A file the backend writes into a mapped folder is owned by the app's user id and inherits the folder's default ACL, so users with write access to the shared folder can change and delete it over SMB, SFTP, WebDAV and FTP. Never keep the app's own state in a mapped folder; use a named volume for that.

## Network

- Each app gets its own internal Docker bridge network without internet. Services reach each other by service name.
- With `network.internet` or `network.lan`, the services named in the manifest's `backend.network.services` (every service without it) also join a second, non-internal network. Keep the others off it: in an app that fetches pages, only the fetching service needs the way out.
- Where that network leads is up to the box's firewall: with `network.internet` to the internet, with `network.lan` to the local network (private, shared and link-local ranges: `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `100.64.0.0/10`, `169.254.0.0/16`, `fc00::/7`, `fe80::/10`, plus loopback and multicast), with both to both. `network.internet` alone does not reach your router, printer or other servers. DNS to the box's resolvers always works, so names resolve without `network.lan`.
- An app that fetches addresses users type in should still check them: with `network.lan` a user could point it at any device in the network.
- No ports are published on the host, and an nftables rule drops new connections from the app networks to the host, also with `network.lan`.
- Each app has its own user id (and group id) from 40000 to 49999. With `root: true` the user inside the container is root, but user namespace remapping keeps it unprivileged on the host.
- Hardening is always on: `no-new-privileges`, a process limit of 1024, a raised out-of-memory score, and no Linux capabilities for services that do not run as root.
- `privileged` (the right `host.full`) is refused. A mount of `/var/run/docker.sock` or `/run/docker.sock` with `docker.socket` gets the box's filtered Docker socket for this app: the Engine API (version 1.45) with only the app's own containers and volumes, which join the app's network with the backend's limits. Privileged containers, host namespaces, devices, capabilities, published ports and bind mounts outside the app's `volumes/` are refused with 403, objects of other apps answer 404, image pulls need `network.internet`, and build, push, load, commit and prune are refused. One request per connection; attach and exec start keep it open both ways. The containers stop with the backend and go with the app, and after a restart of the box's daemon the backend is started again, because its socket is new. `docker.socket.raw` gives the unfiltered socket, root on the box, from a file or any catalog, after the red consent step of the tier root access (D388).
- Devices: a device class (`/dev/dri`, `/dev/serial`, `/dev/net/tun`) stands for every node of the class plugged in when the backend starts; a node in the class folder keeps its path in the container, a serial adapter appears under its own name (`/dev/ttyUSB0`). Mark a device the backend can do without with `optional: true` in its `permissions` entry; a missing one is then left out instead of stopping the backend.
- Host networking, host PID, devices and capabilities need the matching elevated right, which an administrator grants after a step-up and can revoke; the box then leaves the key out, so the backend must start without it (a service without `host.network` joins the app's network, a missing device or capability is simply not there).
- A service with `network_mode: host`, `pid: host` or a device other than `/dev/net/tun` runs in the host's user namespace (`userns_mode: host`) with the app's uid and, for a device, the device's group added, so such an app cannot use `root: true`. A capability only works for root, which stays unprivileged on the host through the user namespace.

## Lifecycle

- **always:** runs whenever it can.
- **onDemand:** runs while a user holds a window lease, a request or a schedule holds it, and for `idleTimeout` seconds (default 600) after the last one. The desktop subscribes to the topic `apps/<id>/context` once per app and tab; closing the last window ends the lease at once, and a lost connection keeps it for 2 minutes.
- Installing, updating or enabling an app starts an `onDemand` backend once, so a broken backend fails the install; it then idles out after `idleTimeout` like after a closed window.
- A failed start is retried after 1 minute; opening a window retries at once.
- Start timeouts: Compose `up --wait` waits up to 10 minutes, then the manifest's health check up to 60 seconds.
- Stop: `docker compose down --timeout 30`. Containers get their `stop_signal` (SIGTERM by default) and are killed after 30 seconds; the timeout replaces a `stop_grace_period` of the Compose file. Volumes and images stay.
- The health call is a plain `GET <path>` on its endpoint without user headers. Each try may take 5 seconds; it runs only while the backend starts, not afterwards.
- Health: prefer the manifest's `health` (an HTTP check the daemon makes, every 100 ms while the backend starts) to a Compose `healthcheck`. A Compose check starts a process in the container at every interval, and `up --wait` waits for its first pass. Measured with a small Node backend on the box: a cold start took 2.5 seconds with a Node health check every second during `start_period` and 1.05 seconds with the manifest's `health` only; running idle, the Compose check cost about 2 percent of a core (in the container and in Docker), without it about 0.15 percent. `pithyx validate` warns about a Compose healthcheck next to `health` (`cli.compose.healthcheck_redundant`). An image's own `HEALTHCHECK` also runs; switch it off with `healthcheck: { disable: true }`.
- A browser on the box itself sees every backend start as a change of the network (Docker attaches the container to the app's bridge), and Chromium aborts the requests in flight with `net::ERR_NETWORK_CHANGED`. Browsers on other devices do not see it. Load again when `backendStatus` becomes `running` (see the edge cases of the [backend guide](/docs/sdk/guides/backend/)).
- Schedules wake the backend without a window, call `POST path` (holding the backend for up to 10 minutes) and then let it idle.
- The window sees the status `stopped`, `starting`, `running`, `updating` or `unavailable`, with the reason `storage` or `pool` when it cannot start (SDK event `backendStatus`).
- Docker starts only while a backend app is installed and its pool is mounted.

Pithyx has no keep-alive lease for background work; use `always` or schedules.
