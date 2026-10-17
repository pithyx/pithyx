---
title: 'Daemon architecture'
description: 'The only privileged process: its modules, the command runner, storage backends and paths.'
status: ready
audience: developer
sidebar:
  order: 1
---

The daemon (`pithyxd`) is the only privileged process on a Pithyx box: one C# binary (.NET 10, ASP.NET Core) with one SQLite database. It serves the desktop and the API, runs every system command through one command runner, and manages storage, accounts, file services, apps and Docker. The desktop never runs with elevated rights.

## Overview

The daemon is a modular monolith. Each module is a folder under `daemon/src/Pithyx.Daemon`: Apps, Auth, Backup, Commands, Database, Errors, FileServices, Files, Health, Hosting, Installer, Jobs, Layouts, Localization, Logs, Monitoring, Network, Notifications, Permissions, Realtime, Shares, Storage, SystemSettings, Updates and Users. The solution is `daemon/Pithyx.slnx`, the tests are in `daemon/tests/Pithyx.Daemon.Tests`.

- **Process.** `pithyx.service` runs `/usr/lib/pithyx/daemon/pithyxd`. The unit restarts it on failure after 2 seconds, allows 5 starts in 120 seconds and does not restart on exit codes 65 and 78.
- **Ports.** Kestrel listens on 7484. In real mode 7484 speaks TLS (`Hosting/WebTransport`): a connection whose first byte is no TLS handshake goes to plain HTTP and is redirected to `https://`. On a real box (production build) it also listens on 443 (TLS) and 80 (a development build on the ports of `PITHYX_DEV_PROXY_PORTS`). There `ReverseProxy/HostProxy` runs first: it forwards the domains of proxy hosts with YARP's `IHttpForwarder`, redirects those of redirect hosts and leaves every other name to `TransportRedirect`, which redirects to 7484, except `/api/install-id` on 80. Every TLS handshake gets its certificate by server name from `ReverseProxy/ProxyService`. Simulation and the installer's live system serve plain HTTP. The certificates come from `Tls/BoxCertificates`.
- **Requests.** Every `/api` endpoint requires a session unless it is marked anonymous. The policies are `admin`, `power` and `appToken`.
- **Static files.** The daemon serves the desktop from `/usr/lib/pithyx/desktop` (or `PITHYX_DESKTOP_DIR`), with a fallback to the single-page app.
- **Modes.** Real mode talks to the system. Simulation mode (`PITHYX_SIMULATE=1`) answers from fixtures; see [Developing the daemon](/docs/developers/daemon/development/).
- **Installer mode.** With `PITHYX_INSTALLER=1` or the kernel command line option `pithyx.installer`, only the installer and setup endpoints exist.
- **Exit codes.** 1 means the database failed, 65 means the database was written by a newer daemon, 75 asks systemd to restart (after a configuration restore) and 78 means a second installation (see below).

Per-app user ids come from 40000 to 49999, user accounts from 20000 to 29999 and groups from 30000 to 39999. Ids are never reused.

Apps run as containers: the daemon talks to Docker over its unix socket and starts a backend with `docker compose up --wait` as a job. See [Backends](/docs/developers/apps/backend/).

The daemon has no watchdog (`sd_notify` heartbeats) and no virtual machine module.

## Command runner

Every external command goes through `ICommandRunner`. Code never calls `zfs`, `zpool` or `smartctl` directly.

- `RunAsync(CommandRequest, ct)` returns a `CommandResult`. `StreamLinesAsync` is for long-lived commands: the request timeout does not apply and cancelling kills the process.
- A `CommandRequest` has the file name, an argument list (no shell), an optional timeout, indexes of sensitive arguments and optional standard input. Standard input is written once, closed and never logged.
- The default timeout is 1 minute. A timeout kills the whole process tree.
- The runner sets `LC_ALL=C.UTF-8` and passes the current trace id as `TRACEPARENT`, so a command can be matched to its request.
- Each call is logged with command, arguments, exit code, duration and the output truncated to 4096 characters. Sensitive arguments appear as `***`.
- In simulation mode `FixtureCommandRunner` answers from a scenario folder with `<id>.json`, `<id>.stdout`, optional `<id>.stderr` and `<id>.stream` files. Record new fixtures with `tools/fixtures/record.sh <scenario> <id> -- <command> [args...]`; it anonymizes serial numbers and WWNs.

Parsers of command output are pure static functions with fixture tests, for example `LsblkParser`, `ZfsDatasetParser` and `ZfsSnapshotParser`.

## Storage backend

`IStorageBackend` has two members: `ReadAsync` returns the pool list and `WatchAsync` yields once per change. `ZfsBackend` is the only implementation; there is no Btrfs or mdadm backend.

Around it sit the disk and pool inventories, the planners (`StoragePlanner`, `DatasetPlanner`, `PoolImportScanner`) and the command plans (`ZfsPlans`, `SnapshotPlans`, `ZfsScrubPlans`, `ImportPlans`) that are shown to the user before they run. `DiskGuard` refuses destructive actions on disks that hold data unless the user confirms with the disk serial.

Storage state reaches clients through the topics `storage/disks`, `storage/pools` and `storage/alerts`.

## Database and migrations

The daemon uses SQLite through Dapper with explicit SQL, no ORM and no repository layer. The database is `/var/lib/pithyx/pithyx.db`, or the path in `PITHYX_DB`. It runs in WAL mode. Logs live in a separate `logs.db`.

- Migrations are embedded files `daemon/src/Pithyx.Daemon/Database/Migrations/NNNN_name.sql` (28 today). Each one runs in its own transaction; a failure exits with code 1. The table `schema_version` records what ran.
- Before migrating a non-empty database, the daemon writes a copy with `VACUUM INTO` named `pithyx.db.pre-<NNNN>-<time>.bak`, where `NNNN` is the first pending version.
- If the database contains versions the daemon does not know, the daemon logs a critical message and exits with 65. Start a newer daemon or point `PITHYX_DB` at another database.
- Dates are stored as UTC ISO 8601 text.
- The configuration backup covers the database, the Samba password database, TLS certificates and SSH host keys. A new table must belong to a backup part or be marked as runtime state.

## Paths on the box

| Path                          | Content                                                        |
| ----------------------------- | -------------------------------------------------------------- |
| `/var/lib/pithyx/pithyx.db`   | The database (override with `PITHYX_DB`)                       |
| `/var/lib/pithyx/catalogs/`   | One clone per enabled catalog                                  |
| `/var/lib/pithyx/tls/`        | The box's certificate authority, its own certificate and an uploaded one (PEM, readable by root only)       |
| `/var/log/pithyx/`            | `daemon.json` and `logs.db` (override with `PITHYX_LOG_DIR`)   |
| `/var/cache/pithyx/`          | Caches (override with `PITHYX_CACHE_DIR`)                      |
| `/usr/lib/pithyx/desktop/`    | The built desktop                                              |
| `/etc/pithyx/environment`     | `dev` marks a development machine; anything else means `prod`  |
| `/<pool>/.pithyx/`            | App data and Docker data on the app pool, never on the system disk |

A machine has exactly one Pithyx installation. A daemon started from a checkout without `PITHYX_DB` on a machine that already has `/etc/pithyx/environment` refuses to start and exits with 78.
