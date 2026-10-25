# Daemon

Rules for `daemon/`; the root `CLAUDE.md` applies as well.

- Docs (D340): a change to the API, an error code, a setting, a limit or what the box does on disk or the network checks the matching pages under `website/src/content/docs/` (admin and install pages for behavior, `developers/daemon/` for internals worth knowing, `reference/` for contracts) and updates or adds them in English and German in the same commit (`website/CLAUDE.md`).

## Daemon

- Modular monolith. Modules are folders (`Storage/`, `Users/`, `Shares/`, `Apps/`, `Jobs/`), not projects.
- External tools (zfs, zpool, mdadm, btrfs, smbd, smartctl, lsblk) are called only through `ICommandRunner`, always with machine-readable output flags. `ICommandRunner` always sets `LC_ALL=C.UTF-8` so output never depends on the system language, and fixtures are recorded the same way.
- Parsers are pure static functions: string in, model out. Every parser has fixture tests with real tool output.
- REST for commands and queries. One raw WebSocket per client for server-pushed data. No SignalR, no MQTT broker.
- WebSocket uses topic subscriptions (`sub`, `unsub`, `+` and `#` wildcards). On subscribe the server sends the current snapshot, then only changes. Every subscription is permission-checked. Stats topics are throttled to 1 Hz.
- Data access uses Dapper with explicit SQL. Schema changes are numbered SQL migration files. No EF Core. The daemon copies the database (`VACUUM INTO`) before running migrations.
- The desktop never runs with elevated rights. Only the daemon does.
- No external services. The daemon is one binary plus one SQLite database (WAL) on the system disk. No Postgres, no Redis, no S3 for daemon state.
- Caching, job queues and event fan-out are in-process (`SystemStateService`, `Channels`, `BackgroundService`). Job state is persisted in SQLite.
- User files live on the pool filesystem so SMB, SFTP, WebDAV, FTP and snapshots see the same data.
- Docker is controlled through the Engine API on the Unix socket, not by parsing CLI output. Engine `/events` feed `AppBackendSupervisor` (D324); only `docker compose up --wait` (and down) runs as a job through `ICommandRunner`. Docker (Debian's `docker.io`, `docker-cli`, `docker-compose`) runs only while a backend app is installed and the app pool is mounted.
- The apps module is `Apps/`: `Manifest/` (`AppId`, `ManifestValidator` with JsonSchema.Net, `IconValidator`), `Compose/` (`ComposeValidator` on the shared allowlist, `ComposeRewriter` as a pure function), `Proxy/` (YARP forwarder, routes resolved per request, D323), `Docker/` (`DockerHost`, `DockerEngine`, `AppBackendRuntime`, `AppBackendSupervisor` with the lifecycle `BackendLifecycle` and `CronSchedule`, D324, D327), `Catalog/` (`CatalogValidator` for `catalog.json` and `index.json`, `IndexSignature` with Ed25519 through NSec, `CatalogRules` for pull requests to a catalog, all on the shared corpus, D330; `CatalogGit`, the hardened git, and `StoreService` with the catalogs, their copies, the daily check, updates and the API under `/api/store/`, D334), `AppTokens`, `AppFrontendHosting`, `CoreApps`. Each app gets a uid from 40000 to 49999 (`UidAllocator.AllocateAppAsync`) and its own internal network; container output comes from journald into the source `app/<id>`.
- SQLite is the source of truth for users and groups. The daemon provisions Linux users and Samba entries from it.
- No read request ever spawns a process. Actions that need a CLI (for example `docker compose`) run as jobs with progress. `SystemStateService` keeps disks, pools, shares and containers in memory, updated by events (udev, zed, Docker) and polling only where no events exist.
- Derived, expensive data is cached in memory (no `HybridCache` yet). Anything that must survive a restart goes to SQLite.
- System config files (`smb.conf`, users; `/etc/exports` once NFS is built) are generated from SQLite and reconciled on start and change. Generated files carry a "Managed by pithyx" header. Manual overrides go into include files that are never touched.
- The system disk is disposable. Everything needed to rebuild it is in the config backup (D293, `Backup/`): `pithyx.db`, Samba passdb, TLS certs, SSH host keys. Every new table belongs to a backup part or is runtime state (`BackupDatabase`).
- App data and Docker volumes live on a pool under `/<pool>/.pithyx/`, never on the system disk.
- Logs of the daemon, the system services (one `journalctl --follow`, never a process per request) and the apps go into `logs.db` on the system disk in batches, never one write per line. The archive on a pool is optional and copied about once a day.

### Paths

```
/usr/lib/pithyx/     binaries, read-only
/etc/pithyx/         bootstrap only
/var/lib/pithyx/     database and secrets (tls/: the box's certificate authority, its certificate and an uploaded one, D346; proxy-host-<id>.pem and .key: uploaded certificates of the reverse proxy, D347; proxy-<host|redirect>-<id>-acme.pem and .key from the ACME authority and acme-account-<hash>.key, D378)
/var/lib/pithyx/catalogs/<id>/  a catalog's shallow git repository (repo.git) and its verified copy per channel (stable, beta)
/var/cache/pithyx/   rebuildable, safe to delete
/var/log/pithyx/     logs: daemon.json files and logs.db (the Log Center, capped at 4 GB)
/run/pithyx/sftp/    SFTP views: one chroot per user with bind mounts of its shared folders and homes (home, for administrators homes/<folder>), rebuilt at start
/run/pithyx/backup/  a checked configuration backup's database until it is restored (tmpfs, never on a disk)
/<pool>/homes/<user>  a user's home (dataset <pool>/pithyx/homes/<user> on the home pool, setting homes.pool, D348); a deleted user's kept home is <user>.deleted-<uid>
/<pool>/.pithyx/     app data, volumes
/<pool>/.pithyx/apps/<id>/  one app (dataset <pool>/pithyx/apps/<id>): versions/<version>, current, volumes/, run/
/<pool>/.pithyx/docker/     Docker's data root on the app pool (setting apps.pool, chosen at the first install)
/<pool>/.pithyx/cache/  the pool's thumbnails and other derived data (<pool>/pithyx/cache, hidden)
/<pool>/.pithyx/system/logs/  the optional log archive (archive.db)
```

- Apps never run or keep data on the system disk: frontends, images, volumes and Docker's data live on the app pool, and an app's runtime is its containers. An uploaded package waiting for the install (at most 15 minutes) is unpacked in `/var/cache/pithyx/app-packages`; it is rebuildable scratch, not app data. Only the app registry (`apps`, permissions, storage mappings, icons) is in `pithyx.db`, in the backup part "Apps". A locked or missing app pool stops its apps.
- Derived data of files on a pool (thumbnails) lives in that pool's cache dataset `<pool>/pithyx/cache`, capped at 2 GB, never on the system disk. It goes with the pool when the pool is destroyed or exported; shares outside a pool (development only) use the development cache folder.

## Operations

- Runs under systemd (`Type=simple`, `Restart=on-failure`, `RestartPreventExitStatus=65 78`); a watchdog with `sd_notify` heartbeats is planned, not built yet.
- Detect capabilities, never assume them. In VMs or without sensors, hide or explain missing data (temperatures, SMART) instead of showing errors.
- Recovery never modifies pools automatically.
- Never wake sleeping disks: SMART via `smartctl -n standby`, logs and state on the system disk, listings from ARC or a special vdev. Spindown as a per-pool option, off by default, is planned, not built yet.
- Power profiles (planned, not built yet: performance, balanced, efficient) set CPU governor/EPP and optional link power management (SATA ALPM, PCIe ASPM, USB autosuspend); options known to cause instability stay opt-in.
- Host network changes apply with a 60 second confirmation timer and roll back unless confirmed.

## Virtual machines

- QEMU/KVM directly, no libvirt. VM definitions live in SQLite; the daemon renders the QEMU command line and a systemd unit.
- Every VM runs as its own systemd unit. VMs must survive daemon restarts and updates.
- Control VMs through QMP. Console through QEMU's VNC WebSocket and noVNC.
- VM disks are ZFS zvols. Cap the ZFS ARC when VMs are configured.
