<img src="desktop/public/logo.svg" alt="" width="96" height="96">

# Pithyx

Pronounced /ˈpɪθɪks/, "PITH-iks".

Pandora never opened a box. She opened a pithos (πίθος), a great storage jar of ancient Greece. Erasmus of Rotterdam later mistranslated it as pyxis, a box, and the box stuck. What stayed inside was hope. Pithyx keeps what matters, on your hardware.

The stem is pithos; the ending -yx is a stylization after Greek models such as onyx and Styx.

Pithyx is a free, self-hosted NAS and server operating system with a web desktop. It runs on Debian 13, manages storage with ZFS, shares files over SMB, SFTP, WebDAV and FTP, and runs apps as containers, all from a desktop in the browser that works the same on a phone and on an ultrawide monitor. Everything stays on your box: no telemetry, no cloud accounts, no services the system depends on.

## Status

Beta (1.0.0-beta.1). Expect rough edges and keep backups of data you care about.

## Development

> **Development runs over plain HTTP on port 7484.** HTTPS on 443 arrives in a later milestone. Never expose a development daemon to a network you do not trust, and never ship this setup.

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
git clone git@github.com:pithyx/pithyx.git
```

```bash
cd pithyx && pnpm install
```

Start the daemon in simulation mode in one terminal:

```bash
PITHYX_SIMULATE=1 dotnet watch --project daemon/src/Pithyx.Daemon
```

Start the desktop in a second terminal:

```bash
pnpm dev
```

Open http://localhost:5173. On the first run the desktop asks you to create the administrator and shows the recovery key once; after that you sign in and see the disks. To start over, stop the daemon and delete `.pithyx-dev/`. The Vite dev server proxies `/api` and `/ws` to the daemon; set `PITHYX_DAEMON_URL` to point it somewhere other than `http://localhost:7484`.

Simulation settings:

| Variable                   | Default                 | Meaning                                                    |
| -------------------------- | ----------------------- | ---------------------------------------------------------- |
| `PITHYX_SIMULATE`          | unset                   | `1` answers commands from fixtures instead of running them |
| `PITHYX_FIXTURES`          | `daemon/tests/fixtures` | Fixture root                                               |
| `PITHYX_SIMULATE_SCENARIO` | `default`               | Folder under `commands/` to use                            |

Fixtures are recorded on a real machine with `tools/fixtures/record.sh <scenario> <id> -- <command> [args...]`, using exactly the arguments the daemon passes.

While the daemon runs in development, the API reference is at http://localhost:7484/api/docs and the OpenAPI document at `/api/openapi.json`. The database is `.pithyx-dev/pithyx.db` (override with `PITHYX_DB`); stop the daemon and delete `.pithyx-dev/` to start over with a fresh setup. Logs are JSON lines in `.pithyx-dev/logs/daemon.json` (override with `PITHYX_LOG_DIR`); every entry carries the `RequestId` that error responses and the desktop show, so `grep <request id> .pithyx-dev/logs/daemon.json` finds everything that happened for one request.

### Design gallery

`pnpm design` starts a Vite server on port 5174 (all interfaces) with the design mockups at `/design.html`: every screen at phone, tablet and laptop width in light and dark, with switches for accent, taskbar position and language. It is a development tool only; the production build contains just `index.html`.

`pnpm design:shots` renders every screen at full size as `<screen>_<viewport>_<theme>_<lang>.png` into `tools/design-shots/out/` (gitignored). It starts its own design server unless `--base-url` points to a running one; `--screens login,tokens` limits the run.

`pnpm design:test` fails if any screen overflows horizontally at phone, tablet or laptop width in English or German. CI runs it next to the end-to-end tests.

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

The script is idempotent. It enables `contrib`, installs the toolchain in the pinned versions, ZFS (DKMS), mdadm, btrfs-progs, smartmontools, Samba, NFS, Docker and QEMU, sets the system locale to `en_US.UTF-8` (keyboard and time zone stay unchanged), writes `dev` to `/etc/pithyx/environment` and lists the test disks in `/etc/pithyx/test-disks`. Samba and NFS are disabled until the daemon manages them. If it exits with code 2, reboot and run it again.

Take a VM snapshot before destructive tests. Create and destroy test pools only through the wrapper, which refuses any disk that is not a test disk:

```bash
tools/dev-vm/testpool.sh create pithyxtest --layout mirror VYXTEST01 VYXTEST02
```

```bash
tools/dev-vm/testpool.sh destroy pithyxtest
```

The dev VM has exactly one installation: the unit `pithyx-dev.service` runs dev as root in real mode on port 7484, with the one database `/var/lib/pithyx/pithyx.db` on the system disk and its pools on the test disks, like a box. A daemon started from a checkout without `PITHYX_DB` refuses to start there (exit code 78), so no second installation appears in `.pithyx-dev/`. Branches are checked by CI before the merge and on 7484 after it. To deploy the latest `origin/dev`:

```bash
tools/dev-vm/install-dev-daemon.sh
```

It fast-forwards the main checkout, builds as your user (also under `sudo`), checks first that dev knows every schema version in the database, says what it would stop on port 7484 and asks (`--yes` skips the question, `--no-build` reuses the build, `--uninstall` removes the unit). Afterwards it makes sure the owner account from `~/.config/pithyx-dev/owner.env` can sign in. To start over with an empty database and the owner and test accounts set up again:

```bash
tools/dev-vm/reset-dev-db.sh
```

### Checks

`pnpm install` installs the git hooks. CI runs `pnpm lint`, `pnpm typecheck`, `pnpm test`, `pnpm build`, `pnpm check:repo`, `dotnet test` in `daemon/` and the E2E suite on every push; on your own machine you can run the same commands.

The end-to-end tests start their own daemon in simulation mode with a fresh database and drive the built desktop in Chromium:

```bash
pnpm --filter @pithyx/e2e exec playwright install chromium
```

```bash
pnpm build && pnpm e2e
```

### Common problems

- **Port 7484 is already in use:** another daemon is still running. Stop it, or find it with `ss -ltnp | grep 7484`.
- **`pnpm: command not found`:** run `corepack enable` (with `sudo` if Node was installed system-wide).
- **"No simulation fixture for: ..."**: the daemon called a command that has no recording in the active scenario. Record it with `tools/fixtures/record.sh`.
- **"Simulation fixtures not found"**: the daemon was started from an unusual directory. Set `PITHYX_FIXTURES` to `daemon/tests/fixtures`.
- **The desktop shows "Daemon not reachable":** the daemon is not running, or `PITHYX_DAEMON_URL` points to the wrong place.
- **`modprobe zfs` fails after provisioning:** reboot so the DKMS module matches the running kernel, then run `provision.sh` again. Secure Boot must be disabled, because the ZFS module is not signed.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Security issues: see [SECURITY.md](SECURITY.md).

## License

Pithyx was originally created by [Schecher1](https://github.com/Schecher1). It is licensed under the [GNU Affero General Public License v3.0](LICENSE) with additional terms under section 7(b), see [NOTICE](NOTICE): forks and redistributions keep the attribution "Based on Pithyx, originally created by Schecher1" in their source, their About page and their docs; a fork may rename its product but states that it is based on Pithyx by Schecher1. The app SDK in [`sdk/`](sdk/) is licensed under the [MIT License](sdk/LICENSE) so apps built on it can use any license.
