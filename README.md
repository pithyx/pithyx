# Vyxis

Pronounced "VIK-sis".

Vyxis is a free, self-hosted NAS and server operating system with a web desktop. It runs on Debian 13, manages storage with ZFS, shares files over SMB, NFS, SFTP and WebDAV, and runs apps as containers, all from a desktop in the browser that works the same on a phone and on an ultrawide monitor. Everything stays on your box: no telemetry, no cloud accounts, no services the system depends on.

## Status

Pre-alpha. Nothing here is usable yet. Do not put data you care about on it.

## Development

> **Development runs over plain HTTP on port 8994.** HTTPS on 443 arrives in a later milestone. Never expose a development daemon to a network you do not trust, and never ship this setup.

There are two workflows. Both use the same commands; they differ in where the daemon runs and whether it talks to real tools.

- **Simulation (any Linux, including WSL).** The daemon answers every external command from recorded fixtures. No root, no ZFS, no disks. Best for desktop and API work. Native Windows and macOS are not supported.
- **Real (dev VM).** The daemon runs on a Debian 13 VM with real ZFS, mdadm, Samba and Docker, and test disks you may destroy.

### Prerequisites

- .NET SDK 10.0.401 or a newer 10.0 feature band (`global.json`)
- Node.js 24 (`.nvmrc`)
- pnpm through corepack: `corepack enable` (the version comes from `package.json`)
- git

On a dev VM, `tools/dev-vm/provision.sh` installs all of this (see below).

### Simulation workflow

```bash
git clone git@github.com:vyxis/vyxis.git
```

```bash
cd vyxis && pnpm install
```

Start the daemon in simulation mode in one terminal:

```bash
VYXIS_SIMULATE=1 dotnet watch --project daemon/src/Vyxis.Daemon
```

Start the desktop in a second terminal:

```bash
pnpm dev
```

Open http://localhost:5173. The desktop shows "Connected to daemon (simulated)". The Vite dev server proxies `/api` and `/ws` to the daemon; set `VYXIS_DAEMON_URL` to point it somewhere other than `http://localhost:8994`.

Simulation settings:

| Variable                  | Default                 | Meaning                                                    |
| ------------------------- | ----------------------- | ---------------------------------------------------------- |
| `VYXIS_SIMULATE`          | unset                   | `1` answers commands from fixtures instead of running them |
| `VYXIS_FIXTURES`          | `daemon/tests/fixtures` | Fixture root                                               |
| `VYXIS_SIMULATE_SCENARIO` | `default`               | Folder under `commands/` to use                            |

Fixtures are recorded on a real machine with `tools/fixtures/record.sh <scenario> <id> -- <command> [args...]`, using exactly the arguments the daemon passes.

While the daemon runs in development, the API reference is at http://localhost:8994/api/docs and the OpenAPI document at `/api/openapi.json`. Logs are JSON lines in `.vyxis-dev/logs/daemon.json` (override with `VYXIS_LOG_DIR`); every entry carries the `RequestId` that error responses and the desktop show, so `grep <request id> .vyxis-dev/logs/daemon.json` finds everything that happened for one request.

### API contracts

The daemon build writes `packages/api-client/openapi.json`. The TypeScript types and the error code list in `packages/api-client/src/generated/` are generated from it; never edit them by hand. After changing an endpoint, a response type or an error code, run:

```bash
pnpm generate
```

Commit the regenerated files together with the daemon change. `pnpm check:repo` and CI fail when they are out of date. A new error code also needs a text in `i18n/en/errors.json` and `i18n/de/errors.json`.

### Dev VM

Recommended VM: 4 vCPU, 8 GB RAM, UEFI, a 32 GB system disk and six 10 GB data disks. Give the data disks the serials `VYXTEST01` to `VYXTEST06` (in Proxmox: `serial=VYXTEST01` on each disk). Only disks with these serials are ever used for tests.

Install Debian 13, clone the repository, then run:

```bash
sudo tools/dev-vm/provision.sh
```

The script is idempotent. It enables `contrib`, installs the toolchain in the pinned versions, ZFS (DKMS), mdadm, btrfs-progs, smartmontools, Samba, NFS, Docker and QEMU, sets the system locale to `en_US.UTF-8` (keyboard and time zone stay unchanged), writes `dev` to `/etc/vyxis/environment` and lists the test disks in `/etc/vyxis/test-disks`. Samba and NFS are disabled until the daemon manages them. If it exits with code 2, reboot and run it again.

Take a VM snapshot before destructive tests. Create and destroy test pools only through the wrapper, which refuses any disk that is not a test disk:

```bash
tools/dev-vm/testpool.sh create vyxtest --layout mirror VYXTEST01 VYXTEST02
```

```bash
tools/dev-vm/testpool.sh destroy vyxtest
```

To develop on the VM from your machine, connect with VS Code Remote-SSH (or any SSH client with port forwarding), run `dotnet watch --project daemon/src/Vyxis.Daemon` without `VYXIS_SIMULATE`, run `pnpm dev`, and forward port 5173.

### Checks

`pnpm install` installs the git hooks. Before you push, run `pnpm lint`, `pnpm typecheck`, `pnpm test`, `pnpm build`, `pnpm check:repo` and `dotnet test` in `daemon/`.

### Common problems

- **Port 8994 is already in use:** another daemon is still running. Stop it, or find it with `ss -ltnp | grep 8994`.
- **`pnpm: command not found`:** run `corepack enable` (with `sudo` if Node was installed system-wide).
- **"No simulation fixture for: ..."**: the daemon called a command that has no recording in the active scenario. Record it with `tools/fixtures/record.sh`.
- **"Simulation fixtures not found"**: the daemon was started from an unusual directory. Set `VYXIS_FIXTURES` to `daemon/tests/fixtures`.
- **The desktop shows "Daemon not reachable":** the daemon is not running, or `VYXIS_DAEMON_URL` points to the wrong place.
- **`modprobe zfs` fails after provisioning:** reboot so the DKMS module matches the running kernel, then run `provision.sh` again. Secure Boot must be disabled, because the ZFS module is not signed.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Security issues: see [SECURITY.md](SECURITY.md).

## License

Vyxis is licensed under the [GNU Affero General Public License v3.0](LICENSE) with an attribution term under section 7(b), see [NOTICE](NOTICE). The app SDK in [`sdk/`](sdk/) is licensed under the [MIT License](sdk/LICENSE) so apps built on it can use any license.
