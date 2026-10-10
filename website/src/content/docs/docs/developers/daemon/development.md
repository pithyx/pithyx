---
title: 'Developing the daemon'
description: 'Building, running in simulation, tests and the integration suites that need root.'
status: draft
audience: developer
sidebar:
  order: 4
---

You can build and run the daemon on any Linux machine without ZFS or root: simulation mode answers system commands from recorded fixtures, and the unit tests run against an in-memory daemon with a fake clock. Only the storage, Docker and network integration suites need a dedicated virtual machine.

## Building

Prerequisites: .NET SDK 10.0.401 or newer (`global.json`), Node.js 24, pnpm (enable it with `corepack enable`) and git.

```sh
pnpm install            # also installs the git hooks (lefthook)
dotnet build daemon/Pithyx.slnx
pnpm build              # builds every package
pnpm generate           # builds the daemon, then regenerates the clients
```

The hooks run Prettier, ESLint, the repository checks, shellcheck and `dotnet format whitespace` before a commit. A pre-push hook refuses pushes to `master` and `dev`. Warnings are errors in C# and TypeScript.

The desktop dev server runs with `pnpm dev` on `http://localhost:5173` and proxies `/api` and `/ws` to a daemon on `http://localhost:7484`. `PITHYX_DAEMON_URL` changes the target.

## Simulation mode

```sh
PITHYX_SIMULATE=1 dotnet watch --project daemon/src/Pithyx.Daemon
```

Simulation works on any Linux, including WSL; native Windows and macOS are not supported. State goes to `.pithyx-dev/` (database, logs, cache), which git ignores; stop the daemon and delete the folder to start over. On the first run the desktop asks you to create the administrator and shows the recovery key once.

| Variable                   | Meaning                                                    |
| -------------------------- | ---------------------------------------------------------- |
| `PITHYX_SIMULATE`          | `1` turns simulation on                                    |
| `PITHYX_FIXTURES`          | Fixture folder (default `daemon/tests/fixtures`)           |
| `PITHYX_SIMULATE_SCENARIO` | Scenario folder (default `default`)                        |
| `PITHYX_ETC_DIR`           | Stand-in for `/etc/pithyx` (default `<fixtures>/etc`)      |
| `PITHYX_DB`                | Database path                                              |
| `PITHYX_LOG_DIR`           | Log folder                                                 |
| `PITHYX_CACHE_DIR`         | Cache folder                                               |
| `PITHYX_DESKTOP_DIR`       | Built desktop to serve                                     |
| `PITHYX_DEV_PORT`          | Port override, only in Development builds                  |
| `PITHYX_FTP_PORT`          | FTP port (21 on a box, 0 in simulation)                    |

Simulation reads fake account files from the fixtures and applies nothing to the system. If a command has no fixture, the daemon says "No simulation fixture for: ..."; record one with `tools/fixtures/record.sh`.

If your development machine itself runs Pithyx (`/etc/pithyx/environment` exists) and you start a daemon without `PITHYX_DB`, it exits with code 78. Set `PITHYX_DB`.

## Tests

- **Daemon:** `dotnet test` in `daemon/` (xUnit). The folders under `daemon/tests/Pithyx.Daemon.Tests` mirror the modules. `DaemonFactory` starts the daemon in memory with a temporary data folder, simulation on and a `FakeTimeProvider`; `CreateApiClient()` adds the CSRF header.
- **Frontend:** `pnpm lint`, `pnpm typecheck`, `pnpm test` (Vitest).
- **End to end:** `pnpm --filter @pithyx/e2e exec playwright install chromium`, then `pnpm build && pnpm e2e`. It starts its own daemon in simulation with a fresh database.
- **Layout checks:** `pnpm design:test` checks every screen for overflow at phone, tablet and laptop sizes in English and German.
- **Shared corpora:** the manifest, Compose and catalog fixtures in `sdk/schema/fixtures/` run in both Vitest and xUnit, so the CLI and the daemon judge alike.

Test parsers, decision logic, permission checks, manifest validation and state machines. Do not test getters, DTOs, pass-through controllers or single React components.

The categories `StorageIntegration`, `DockerIntegration`, `NetworkIntegration` and `TimingBudget` do not run in the normal daemon test run on CI.

## Integration tests

These suites need root, real ZFS, loop devices, `scsi_debug`, Docker or systemd-networkd, so they run in a Debian 13 virtual machine, not on your workstation. The VM described in the README has 4 vCPUs, 8 GB RAM, UEFI, a 32 GB system disk and six 10 GB data disks with the serials `VYXTEST01` to `VYXTEST06`.

- `sudo tools/dev-vm/provision.sh` prepares the machine (safe to repeat; exit code 2 means reboot and run again). It writes `dev` to `/etc/pithyx/environment` and the test serials to `/etc/pithyx/test-disks`.
- `tools/dev-vm/testpool.sh create|destroy|wipe` manages test pools and refuses disks that are not test disks. After `zpool destroy`, run `testpool.sh wipe`.
- `tools/dev-vm/integration.sh [--filter <class>] [--disk-pulls] [--docker] [--network]` runs the suites as a normal user and builds in Release first. `--disk-pulls` pulls virtual disks out of a running pool; run it only on a freshly booted machine. `--docker` and `--network` exclude each other.

Load spoils timing budgets: check `uptime` before trusting a failure.
