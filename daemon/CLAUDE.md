# Daemon

Rules for `daemon/`. The root `CLAUDE.md` applies as well.

## Daemon

- Modular monolith. Modules are folders (`Storage/`, `Users/`, `Shares/`, `Apps/`, `Jobs/`), not projects.
- External tools (zfs, zpool, mdadm, btrfs, smbd, smartctl, lsblk) are called only through `ICommandRunner`, always with machine-readable output flags. `ICommandRunner` always sets `LC_ALL=C.UTF-8` so output never depends on the system language, and fixtures are recorded the same way.
- Parsers are pure static functions: string in, model out. Every parser has fixture tests with real tool output.
- REST for commands and queries. One raw WebSocket per client for server-pushed data. No SignalR, no MQTT broker.
- WebSocket uses topic subscriptions (`sub`, `unsub`, `+` and `#` wildcards). On subscribe the server sends the current snapshot, then only changes. Every subscription is permission-checked. Stats topics are throttled to 1 Hz.
- Data access uses Dapper with explicit SQL. Schema changes are numbered SQL migration files. No EF Core. The daemon copies the database (`VACUUM INTO`) before running migrations.
- The desktop never runs with elevated rights. Only the daemon does.
- No external services. The daemon is one binary plus one SQLite database (WAL) on the system disk. No Postgres, no Redis, no S3 for daemon state.
- Caching, job queues and event fan-out are in-process (`SystemStateService`, `HybridCache`, `Channels`, `BackgroundService`). Job state is persisted in SQLite.
- User files live on the pool filesystem so SMB, NFS and snapshots see the same data.
- Docker is controlled through the Engine API on the Unix socket, not by parsing CLI output.
- SQLite is the source of truth for users and groups. The daemon provisions Linux users and Samba entries from it.
- No read request ever spawns a process. Actions that need a CLI (for example `docker compose`) run as jobs with progress. `SystemStateService` keeps disks, pools, shares and containers in memory, updated by events (udev, zed, Docker) and polling only where no events exist.
- Derived, expensive data uses `HybridCache` without L2. Anything that must survive a restart goes to SQLite.
- System config files (`smb.conf`, `/etc/exports`, users) are generated from SQLite and reconciled on start and change. Generated files carry a "Managed by pithyx" header. Manual overrides go into include files that are never touched.
- The system disk is disposable. Everything needed to rebuild it is in the config backup: `pithyx.db`, Samba passdb, TLS certs, SSH host keys.
- App data and Docker volumes live on a pool under `/<pool>/.pithyx/`, never on the system disk.

### Paths

```
/usr/lib/pithyx/     binaries, read-only
/etc/pithyx/         bootstrap only
/var/lib/pithyx/     database and secrets
/var/cache/pithyx/   rebuildable, safe to delete
/var/log/pithyx/     logs
/run/pithyx/         API socket
/<pool>/.pithyx/     app data, volumes, config backups
/<pool>/.pithyx/cache/  the pool's thumbnails and other derived data (<pool>/pithyx/cache, hidden)
```

- Derived data of files on a pool (thumbnails) lives in that pool's cache dataset `<pool>/pithyx/cache`, capped at 2 GB, never on the system disk. It goes with the pool when the pool is destroyed or exported; shares outside a pool (development only) use the development cache folder.

## Virtual machines

- QEMU/KVM directly, no libvirt. VM definitions live in SQLite; the daemon renders the QEMU command line and a systemd unit.
- Every VM runs as its own systemd unit. VMs must survive daemon restarts and updates.
- Control VMs through QMP. Console through QEMU's VNC WebSocket and noVNC.
- VM disks are ZFS zvols. Cap the ZFS ARC when VMs are configured.
