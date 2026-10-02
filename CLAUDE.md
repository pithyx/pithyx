# Pithyx

Free, self-hosted NAS and server OS with a web desktop. Debian 13, C# daemon, React desktop, apps as containers. AGPL-3.0, SDK MIT. GitHub org `pithyx`.

These rules are binding. If a rule blocks you, stop and ask; never work around it.

## Principles

- 100 percent local: no telemetry, phone-home, cloud accounts or project-run services the box depends on. Crash reports stay on the box; the user shares a support bundle manually.
- Fresh installs only, no import from other NAS systems.

## Environments

`/etc/pithyx/environment` says `dev` or `prod`, or the owner states it; if neither, assume prod. Every machine has exactly one Pithyx installation (D277).

- **dev** (dev01, dev VMs, simulation, CI): the machine is yours. Install packages, change services and config, load modules, reboot, create and destroy pools, reset databases, edit any file including every `CLAUDE.md`, without asking. Document afterwards (commit, D entry or Current state).
- Hard limits in dev: never push or merge to `master`; never delete, force-push or rewrite `origin/master` or `origin/dev`. On dev01: never touch `sda` (system disk); test disks are `VYXTEST01` to `VYXTEST06` (`/etc/pithyx/test-disks`), scsi_debug disks are free. No long write tests or benchmarks (worn SSD). No access to pve02, the home network or the CI runner's server. No secrets in commits or chat; never read password hashes or session tokens from the database, use the API. Before a reboot: commit and push everything (`🚧 wip:` branches are fine), say so and wait for the owner's "weiter" (the session runs on dev01).
- Test pools: `tools/dev-vm/testpool.sh create|destroy|wipe` (refuses non-test disks). After `zpool destroy` run `testpool.sh wipe`.
- **prod** (real data, including the owner's NAS): read-only. Before any write show the exact command and wait for confirmation; snapshot or backup first. Never `zpool destroy/create`, `mkfs`, `wipefs`, `dd`, `rm -rf` on data, `docker volume rm` or migrations without a confirmed backup.
- The product does the same: destructive storage actions refuse disks with partitions or data unless the user confirms with the disk serial.

## Dev VM (dev01)

- The only installation is `pithyx-dev.service` on 7484: real mode, database `/var/lib/pithyx/pithyx.db` on the system disk, pools on the test disks. It always runs so the owner can watch, except while `tools/dev-vm/integration.sh` runs (it stops and restarts the unit, because the daemon manages every pool it sees, D276). Never point it at another database or data directory. No second instance, no `.pithyx-dev/`, no simulation on dev01; a checkout daemon without `PITHYX_DB` exits 78. Simulation exists only for tests and E2E on the runner.
- Deploy: `tools/dev-vm/install-dev-daemon.sh --yes` fast-forwards the main checkout to `origin/dev`, builds and restarts. Deploy after every merge into `dev`.
- Fresh state only via `tools/dev-vm/reset-dev-db.sh` (stops the unit, deletes the DB, starts, recreates the owner account). A reset turns the owner's 2FA off; report that.
- Owner credentials: `~gabriel/.config/pithyx-dev/owner.env` (mode 600, never in repo or chat). Never change the owner account (password, 2FA, deactivation). Tests use `e2e-admin` and `e2e-user` from `~gabriel/.config/pithyx-dev/e2e.env`, created by `tools/dev-vm/seed-dev-accounts.sh`.

## Current state

Update in the same PR whenever the state changes. Last updated: 2026-10-03.

- 7484 runs `dev` from `/home/gabriel/code/pithyx`. Check with `systemctl status pithyx-dev` and `git -C /home/gabriel/code/pithyx log -1`; the deployed commit must equal `origin/dev`.
- `owner.env` is stale since the owner reran setup on 2026-09-30; `seed-dev-accounts.sh` needs it updated before it can create the e2e accounts.
- The runner has `libatomic1`, `shellcheck`, Playwright libs. dev01 has `acl`, `smbclient`, `sshpass`. 7484 writes `/etc/samba/smb.conf` (original kept as `smb.conf.pre-pithyx`), runs `smbd` and owns `/etc/ssh/sshd_config.d/50-pithyx.conf`.
- Migrations on `dev`: 0001 to 0018 (#68 with `0018_reapply_share_acls.sql` is merged, its worktree is gone). Merged history: `git log origin/dev`. 7484 has test pool `pithyxtestfs` (VYXTEST01) with shared folder "FS Test" for `e2e-user` (read-write, SFTP).
- The owner widened v0.1 on 2026-10-03 (`TODO.md`, "Scope of v0.1"): groups, the permission model, WebDAV and FTP, changing the network. Open, one PR at a time in this order: A groups (step 10), B permission model (step 11, label `e2e`), C WebDAV and FTP (step 12, its architecture is a `[DECISION]` first), D network (step 14.2, label `e2e`; nothing on the host changes until the owner decides the test method, D110).
- Next after D: verify every "Release 0.1" item in `TODO.md` on 7484 in the browser (Playwright and screenshots as `e2e-admin`/`e2e-user`), fix failures, then report: deployed commit and `dev` HEAD, runner status per job, a table (item, status, proof), merged PRs with migration numbers, the rename grep (D279, only `VYXTEST` allowed), open problems with cause and proposal.
- Decisions D269 to D288 are on `dev`; next free number D289. The decision log at the end of `TODO.md` conflicts on every merge; keep all entries in number order.

## Known pitfalls

- Pulling a scsi_debug disk from a pool under write can wedge ZFS until reboot (OpenZFS 2.3.9: `txg_sync` stuck in `zio_wait`, `zpool clear/destroy/list` hang in D state, load ~17). So `integration.sh --disk-pulls` only on a fresh boot.
- D270: for a returning disk run `zpool online` before `zpool clear`, both with 30 s timeout; `clear` first hangs in the kernel.
- Load spoils timings: check `uptime` before trusting a budget failure.
- A daemon refuses a DB with unknown schema versions (exit 65); `install-dev-daemon.sh` then stops before touching anything. If a branch's daemon wrote the dev DB, run `reset-dev-db.sh`.
- `pithyx-dev.service` allows 5 starts in 120 s; several `integration.sh` runs in a row leave it `start-limit-hit`: `sudo systemctl reset-failed pithyx-dev && sudo systemctl start pithyx-dev`.
- A test that starts its own `smbd` needs `setsid` and a stdin pipe that stays open: a socket on stdin is served as an inetd connection, a closed stdin ends smbd, and an ending smbd signals its whole process group.
- Old WIP commits were made with `--no-verify`; lefthook never checked them.
- A fresh worktree needs `pnpm install --frozen-lockfile` before the first commit, or the repo-check hook fails.

## Workflow

- Work through `TODO.md` top to bottom, one step at a time; finish its "Done when" before the next. `[DECISION]` and `[REVIEW]` steps need the owner: stop and ask. Check items off in the commit that finishes them.
- Branches: `master` stable, `dev` beta and integration. Work in `feature/`, `fix/`, `hotfix/<name>`; PRs target `dev`. Never commit directly to `master` or `dev` (lefthook `pre-push` refuses it). New branches: `git branch --unset-upstream` so a bare `git push` never targets `dev`.
- Parallel work only in worktrees under `.worktrees/<name>` (`git worktree add .worktrees/<name> -b feature/<name> origin/dev`). Remove worktree and local branch once merged.
- Commits and PR titles: `<gitmoji> <type>(<scope>): <subject>`, Unicode emoji, English, imperative, no em dashes. E.g. `✨ feat(desktop): add window snapping`, `🐛 fix(daemon): handle missing lsblk serial`, `♻️ refactor(storage): extract zpool parser`, `👷 ci: add DCO check`. Always `git commit -s` (DCO).
- PR flow: merge `dev` into the branch, push, wait for a green runner run (label `e2e` for larger changes, see CI). Storage, account and command runner changes also attach an `integration.sh` result from dev01. Then squash-merge into `dev` (you may once CI is green; `master` always needs the owner). The squash subject including ` (#n)` stays at or under 100 characters, and the message ends with `Signed-off-by: Schecher_1 <74829802+Schecher1@users.noreply.github.com>`. Then deploy with `install-dev-daemon.sh --yes` and check the change on 7484; a branch with a migration proves its upgrade there (the daemon keeps `pithyx.db.pre-<version>-<time>.bak`).
- One PR run on the runner host at a time, and only after the previous merge's `dev` run finished; parallel runs break timing budgets.
- Generated files (`openapi.json`, `schema.ts`, `error-codes.ts`) are never merged by hand: take one side, build the daemon, run `pnpm --filter @pithyx/api-client generate`.
- Every user-visible or developer-relevant change adds a line under `Unreleased` in `CHANGELOG.md` (Keep a Changelog).
- SemVer for the whole monorepo; releases tagged on `master` as `vX.Y.Z` with a constellation codename.

## Before you commit

- Build, lint, tests green on the runner; generated clients current.
- New visible text has `en` and `de` keys; new error codes have translations and a doc entry.
- `TODO.md` checked off, `CHANGELOG.md` updated.
- `website/` docs checked: update pages describing changed behavior in the same commit. Add a page when a user or admin would otherwise guess (new feature or setting, changed behavior, new error code, limitation, manual step), not for refactors or developer-only details. If out of scope, add a `TODO.md` item.
- No em dashes, no non-English text outside translation files.

## Repository

```
daemon/   C# / ASP.NET Core (.NET 10), the only privileged process
desktop/  Vite + React + TypeScript web desktop
sdk/      npm package for app developers (@pithyx/sdk)
apps/     bundled apps (files, settings, store)
image/    mmdebstrap, live-build, mkosi
i18n/     translation files (en is the reference)
website/  landing page and user docs (Starlight)
```

- One monorepo, one version. Nested rules: `daemon/CLAUDE.md` (architecture, paths, operations, VMs), `desktop/CLAUDE.md` (responsive, accessibility, performance, layout memory); equally binding.
- No developer docs in markdown; the code documents itself. Allowed markdown outside `website/`: README, CLAUDE (root and per top-level folder), CONTRIBUTING, TODO, CHANGELOG, SECURITY. No other markdown for agents; state lives in this file.
- `website/` publishes `/llms.txt` and `/llms-full.txt`. API and SDK references are generated from OpenAPI and TSDoc.
- An API change updates daemon, generated client, SDK and desktop in the same commit. Never edit generated files.
- Committed content is English. Only `i18n/` and translated `website/` pages may be otherwise. Chat with the owner may be German.

## Code style

- Short English comments only where logic is not obvious; prefer clear names. No em dashes, banners, divider comments or commented-out code.
- Warnings are errors in C# and TypeScript.
- No abstraction without a second real implementation or an enforced boundary (`ICommandRunner`, `IStorageBackend` are the exceptions). No repository layer over Dapper, no mediator, no single-implementation interfaces.

## Platforms and budgets

- Supported: x86-64-v2+, UEFI, 64-bit. Community: arm64 (UEFI SystemReady, Raspberry Pi 5); keep code architecture-neutral. No 32-bit.
- Reference hardware: Intel N100/N305, 8 GB RAM. Still frugal: daemon below 200 MB RSS and 1 percent CPU idle; nothing polls when an event source exists. These are CI budgets.

## Network

- HTTPS on 443, 80 redirects. Fixed alternative port 7484; never change it.
- Self-signed certificate by default; a settings wizard makes it trusted (local CA download, or own domain with Let's Encrypt DNS-01 wildcard). Everything works on the untrusted default; passkeys and the service worker switch on only after the trust step.
- The UI never assumes a secure context (plain HTTP by IP must work). `crypto.randomUUID`, `crypto.subtle`, Clipboard, service workers, WebAuthn, notifications only through `desktop/src/platform/browser.ts` (fallback or "missing"); ESLint refuses them elsewhere.
- No subdomains for the core system. IPv6 everywhere IPv4 works.

## Errors and i18n

- Stable codes `area.object.reason` (lowercase, dots, underscores inside words), e.g. `fs.file.not_found`. Defined once as C# constants; the TS union is generated. Exception: SDK transport codes (`sdkErrorCodes` in `sdk/src/errors.ts`), texts under `errors.sdk.*`.
- RFC 9457 problem details with `code`, `params`, `detail` (English fallback), `requestId`. The API never returns translated text, except out-of-band channels (email, push, SSH MOTD) rendered server-side in the recipient's language.
- No user-visible string literals; keys `area.screen.element`, errors under `errors.<code>`. English reference, German required, ICU plurals and params, German uses informal "du". Never name companies or their products (no other NAS vendors); open-source projects may be named.

## Tracing and logging

- Every HTTP request and WebSocket message has a request id (W3C traceparent, `Activity`), propagated to jobs, every `ICommandRunner` call and proxied app backends.
- Every external command logs command, args, exit code, duration, truncated stdout/stderr. Structured JSON logs only (Serilog), no string concatenation.
- Secrets always masked. Support bundles: logs, config, system info without secrets; memory dumps only by explicit opt-in with a warning.
- The UI never shows the request id; it goes to the console and logs.

## Users and security

- Permissions to groups and users; effective = union; admins have everything. Share access: none, read, read-write. Protocol access per user (SMB, NFS, SFTP, WebDAV). App access per app. Special rights: admin, desktop login, SSH login, reboot/shutdown.
- TOTP 2FA per user. Login forms have correct `autocomplete` attributes.
- No password reset without proof: only the recovery key from setup, otherwise reinstall.
- No Secure Boot (installer detects it and asks to disable). SSH off by default; its MOTD shows the ASCII logo and a warning (the only allowed ASCII art).
- Pool encryption: ZFS native. Unlock manually, from a key vault on the system disk (UI explains it only protects against stolen disks) or a USB key.

## Apps

- Every app, core or third-party, uses `@pithyx/sdk` with an identical API: core apps in-process transport, third-party a MessageChannel (one per app instance; identity from the port, never from message data).
- Third-party apps run in an iframe `sandbox="allow-scripts allow-forms"` without `allow-same-origin`, same host. They store data through the SDK, not `localStorage`/IndexedDB.
- Backends: one instance per app shared by all users (separated by `X-Pithyx-User`), non-root with userns-remap, on an internal Docker network, no host ports, reached only via YARP at `/apps/<id>/api/` with a 5-minute SDK token. YARP validates the token, strips the desktop session cookie (mandatory for every proxied app, including external ones), adds `X-Pithyx-User`, `X-Pithyx-Groups` and the request id. No `docker.sock`, `privileged` or host network unless the manifest requests it and the install dialog shows it.
- Lifecycle in the manifest: `always` or `onDemand` (runs while any window is open, plus 10 min idle). Windows are counted via WebSocket with a 2-minute grace on disconnect. Background work needs a keep-alive lease or manifest schedules; otherwise idle backends get SIGTERM with a timeout.
- Window instances in the manifest: `single` (focus the user's window on this device, pass launch args) or `multi`.
- Apps with mounts on a locked or missing pool do not start; the UI says why. Revoked permissions apply on the next token refresh and close affected windows over the WebSocket.
- External web apps (Immich, Jellyfin) keep their own ports behind a YARP listener that may rewrite `X-Frame-Options`/`frame-ancestors`. Linked apps on other hosts open in a window if framable, else a new tab.
- Apps open in windows, never redirect the page; "Open in tab" is optional.

## Misc

- Dates: store and transmit UTC ISO 8601; format with `Intl` per user time zone and locale.
- Fonts: IBM Plex Sans (UI), Sora (word mark, large headings), JetBrains Mono (terminal, logs, paths), all OFL 1.1, shipped unmodified (a subset of Plex would need a new name). Every bundled third-party asset keeps its license and appears in the About page notices.
- Stable contracts (breaking = major bump): REST/WS API, SDK API, manifest schema, extension points, error codes.

## Tests

- Test parsers, decision logic, permission checks, manifest validation, the update state machine. Not getters, DTOs, pass-through controllers or individual React components.
- Daemon tests run in simulation (`DaemonFactory`) with a fake clock where time matters. E2E in `e2e/` (Playwright, `pnpm build && pnpm e2e`) with its own daemon and fresh DB.
- Tests needing root, real ZFS, loop devices, scsi_debug or Linux accounts (`Category=StorageIntegration`) never run in GitHub CI, only on dev01 via `tools/dev-vm/integration.sh`. Storage PRs run their destructive suite there against the test disks before merge.

## CI

- Jobs in `.github/workflows/ci.yml` use `${{ vars.CI_RUNNER || 'ubuntu-latest' }}` (self-hosted `pithyx-ci` on dedi-01); E2E uses `${{ vars.CI_RUNNER_E2E || vars.CI_RUNNER || 'ubuntu-latest' }}` (`pithyx-e2e`, pinned cores, D280).
- The runner has no Docker and no root: no `services:` or `container:`, nothing needing ZFS, disks or root. The owner maintains it: never change it or add workflow workarounds for something missing; tell the owner.
- Workflows: top-level `permissions: contents: read`, widened per job only when needed. No `pull_request_target`, no secrets in logs, no fork code without approval.
- The runner does all build-only work (build, lint, typecheck, unit tests, E2E, perf gate): push, keep working, read the result. dev01 is too weak; use it only for ZFS/root tests and deploys. There is no local CI fallback.
- Every PR runs `changes` and `repo` (format, repo rules, shellcheck, commit messages, sign-off), plus `daemon` (without `StorageIntegration` and `TimingBudget`) and `frontend` when their files changed. That suffices for a small change.
- E2E with perf gate and design checks only on request (D278): label `e2e` (`gh pr edit <n> --add-label e2e`), `gh workflow run CI --ref <branch>`, or any PR to `master`. Add the label for user flows, desktop shell, window manager, sign-in, setup, SDK transport, API contract, loading path or bundle, or several PRs merged in a row. Run it on `dev` before the owner verifies a round on 7484.
