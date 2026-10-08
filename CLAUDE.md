# Pithyx

Free, self-hosted NAS and server OS with a web desktop. Debian 13, C# daemon, React desktop, apps as containers. AGPL-3.0; the npm packages (`@pithyx/sdk`, `@pithyx/ui`, `@pithyx/cli`) MIT. GitHub org `pithyx`, npm org `pithyx`.

These rules are binding. If a rule blocks you, stop and ask; never work around it.

## Principles

- 100 percent local: no telemetry, phone-home, cloud accounts or project-run services the box depends on. Crash reports stay on the box; the user shares a support bundle manually.
- Fresh installs only, no import from other NAS systems.

## Environments

`/etc/pithyx/environment` says `dev` or `prod`, or the owner states it; if neither, assume prod. Every machine has exactly one Pithyx installation (D277).

- **dev** (dev01, dev VMs, simulation, CI): the machine is yours. Install packages, change services and config, load modules, reboot, create and destroy pools, reset databases, edit any file including every `CLAUDE.md`, without asking. Document afterwards (commit, D entry or Current state).
- Hard limits in dev: never push or merge to `master`; never delete, force-push or rewrite `origin/master` or `origin/dev`. On dev01: never touch `sda` (system disk); test disks are `VYXTEST01` to `VYXTEST06` (`/etc/pithyx/test-disks`), scsi_debug disks are free. No access to pve02, the home network or the CI runner's server. No secrets in commits or chat; never read password hashes or session tokens from the database, use the API. Before a reboot: commit and push everything (`🚧 wip:` branches are fine), say so and wait for the owner's "weiter" (the session runs on dev01).
- Test pools: `tools/dev-vm/testpool.sh create|destroy|wipe` (refuses non-test disks). After `zpool destroy` run `testpool.sh wipe`.
- **prod** (real data, including the owner's NAS): read-only. Before any write show the exact command and wait for confirmation; snapshot or backup first. Never `zpool destroy/create`, `mkfs`, `wipefs`, `dd`, `rm -rf` on data, `docker volume rm` or migrations without a confirmed backup.
- The product does the same: destructive storage actions refuse disks with partitions or data unless the user confirms with the disk serial.

## Dev VM (dev01)

- The only installation is `pithyx-dev.service` on 7484: real mode, database `/var/lib/pithyx/pithyx.db` on the system disk, pools on the test disks. It always runs so the owner can watch, except while `tools/dev-vm/integration.sh` runs (it stops and restarts the unit, because the daemon manages every pool it sees, D276). Never point it at another database or data directory. No second instance, no `.pithyx-dev/`, no simulation on dev01; a checkout daemon without `PITHYX_DB` exits 78. Simulation exists only for tests and E2E on the runner.
- Deploy: `tools/dev-vm/install-dev-daemon.sh --yes` fast-forwards the main checkout to `origin/dev`, builds and restarts. Deploy after every merge into `dev`.
- Fresh state only via `tools/dev-vm/reset-dev-db.sh` (stops the unit, deletes the DB, starts, recreates the owner account). A reset turns the owner's 2FA off; report that.
- Owner credentials: `~gabriel/.config/pithyx-dev/owner.env` (mode 600, never in repo or chat). Never change the owner account (password, 2FA, deactivation). Tests use `e2e-admin` and `e2e-user` from `~gabriel/.config/pithyx-dev/e2e.env`, created by `tools/dev-vm/seed-dev-accounts.sh`.

## Current state

Update in the same PR whenever the state changes. Last updated: 2026-10-08.

- 7484 runs `dev` from `/home/gabriel/code/pithyx`. Check with `systemctl status pithyx-dev` and `git -C /home/gabriel/code/pithyx log -1`; the deployed commit must equal `origin/dev`.
- `owner.env` and `e2e.env` work again (the deploy of 2026-10-03 signed in as the owner and found `e2e-admin` and `e2e-user`).
- The runner has `libatomic1`, `shellcheck`, Playwright libs. dev01 has `acl`, `smbclient`, `sshpass`. 7484 writes `/etc/samba/smb.conf` (original kept as `smb.conf.pre-pithyx`), runs `smbd` and owns `/etc/ssh/sshd_config.d/50-pithyx.conf`.
- dev01 has Debian's Docker (`docker.io`, `docker-cli`, `docker-compose`), stopped and disabled with containerd. `tools/dev-vm/docker-spike.sh` (D309) starts it for its run with a runtime drop-in, its data on the throwaway pool `pithyxtestdocker` (VYXTEST06) and the remap user `pithyx-docker`, and removes all of it afterwards; `--keep` leaves them. After its first start Docker's `DOCKER*` chains and forward policy drop stay until a reboot (the Docker integration runs of 2026-10-08 left them again). `tools/dev-vm/integration.sh --docker` (A14) configures Docker with the box's own files on a throwaway pool and puts the previous state back afterwards. Since wave W4 the daemon on 7484 owns Docker as on a box (D324): on 2026-10-08 a chat backend (the app-chat template, built locally, from an offline package) was installed on `pithyxtestfs`, reached through the proxy as `e2e-admin` and `e2e-user`, disabled, enabled and removed with its data; since then dev01 keeps `/etc/docker/daemon.json` (data root `/pithyxtestfs/.pithyx/docker`, dataset `pithyxtestfs/pithyx/docker`), the drop-in `/etc/systemd/system/docker.service.d/pithyx.conf`, the user `pithyx-docker` with its lines in `/etc/subuid` and `/etc/subgid` and the nftables chain `inet pithyx apps-input`, with Docker, docker.socket and containerd stopped and disabled until the next backend app. `integration.sh --docker` keeps copies of these files and restores them.
- Migrations on `dev`: 0001 to 0024 (0019 groups, 0020 grants, 0021 WebDAV and FTP, 0022 system app ids `org.pithyx.*`, 0023 app registry, 0024 app storage and log level). Merged history: `git log origin/dev`. 7484 has test pool `pithyxtestfs` (VYXTEST01) with shared folder "FS Test" for `e2e-user` (read-write, SFTP).
- The owner widened v0.1 on 2026-10-03 (`TODO.md`, "Scope of v0.1"): groups, the permission model, WebDAV and FTP, changing the network. Open, one PR at a time in this order: A groups (step 10, #70, merged), B permission model (step 11, #71, merged), C WebDAV and FTP (step 12, `feature/webdav-ftp`, D291), D network (step 14.2, label `e2e`; nothing on the host changes until the owner decides the test method, D110).
- On 2026-10-05 the owner pulled SDK 1.0 and the app runtime into v0.1, before D (D300): `TODO.md` "Apps v0.1 (SDK and runtime)", steps A0 to A17 in waves W0 to W6, with the architecture, contract drafts and dependencies there. The owner starts each wave; parallel agents of a wave work in their own worktrees, and the wave merges as one PR (D313). A3 merges before A10 (lower migration number, both change `grants`). The owner publishes to npm (D306); nothing is published and no public repository is created without the owner. Wave W1 is merged and deployed (2026-10-06): A1 Docker spike (#87, D309), A4 SDK API v1 (#90, `@pithyx/sdk` at `1.0.0-rc.1`), A2 manifest v1 (#89), A5 `@pithyx/ui` (#88). `1.0.0-rc.1` was never published; the owner runs `tools/release/publish-sdk.sh` from a clean checkout of `origin/dev`. Wave W2 is merged and deployed (2026-10-06): A3 system app ids (#92, migration 0022), then A6 Compose contract (D310), A7 desktop host v1 and A10 app registry (migration 0023) as one PR (#93). 7484 has the E2E probe `com.example.sdk-probe` installed from its package, with the app pool `pithyxtestfs` (dataset `pithyxtestfs/pithyx/apps`); the old demo is gone. Wave W3 (A8 CLI and rc.2, A11 app tokens and the daemon's API for apps with migration 0024, A12 Store and dynamic registry) is merged and deployed (2026-10-07, #95, migration 0024 proven on 7484); `1.0.0-rc.2` (sdk, ui, cli) is on npm since 2026-10-07 under `next` and, as the first publish, also `latest` (npm stages a publish with `PUT 202` until the owner approves it on npmjs.com); `1.0.0-rc.3` with the work-in-progress READMEs is on npm under `next` (rc.2 stays `latest`). The templates blank and showcase never go into the monorepo or onto the box by default: they live only in their own repositories (A9), so A12 is checked with the E2E probe. Wave W4 is merged and deployed (2026-10-08, #98, no migration): A13 proxy (D323), A14 Docker runtime (D324), of A9 only `tools/release/bump-templates.ts`. The template repositories `app-blank`, `app-chat`, `app-showcase` and `.github` exist only as local repositories in `~/code/pithyx-templates/` until the owner's go; app-chat's image needs a registry first. The special folder "Apps" (D314) is deferred to its own step. Next: wave W5 (A15, A16).
- Next after D: verify every "Release 0.1" item in `TODO.md` on 7484 in the browser (Playwright and screenshots as `e2e-admin`/`e2e-user`), fix failures, then report: deployed commit and `dev` HEAD, runner status per job, a table (item, status, proof), merged PRs with migration numbers, the rename grep (D279, only `VYXTEST` allowed), open problems with cause and proposal.
- The web installer, image build and updater are planned (D294 to D297, `TODO.md` "Web installer v0.1" to "Updates v0.1"). The owner asked on 2026-10-04 to build the installer and the image now, before D (D298). `image/build.sh` builds the ISO on dev01 in about 7 minutes (`--reuse-rootfs` about 1), `image/test-boot.sh` boots it in QEMU with KVM, `image/test-install.sh [--mirror]` installs it into a QEMU VM through the installer's API and checks the installed system (D299); a build runner (`pithyx-image`) comes later. dev01 has the image packages (also `qemu-utils`), its user is in group `kvm`, and dev01 has 8 cores and 21 GB since 2026-10-04.
- Decisions D269 to D291, D293 to D310 and D312 to D324 are on `dev`; D292 belongs to `feature/network`; D311 (elevated rights, A16) is reserved; next free number D325. The decision log at the end of `TODO.md` conflicts on every merge; keep all entries in number order.

## Known pitfalls

- Pulling a scsi_debug disk from a pool under write can wedge ZFS until reboot (OpenZFS 2.3.9: `txg_sync` stuck in `zio_wait`, `zpool clear/destroy/list` hang in D state, load ~17). So `integration.sh --disk-pulls` only on a fresh boot.
- D270: for a returning disk run `zpool online` before `zpool clear`, both with 30 s timeout; `clear` first hangs in the kernel.
- Load spoils timings: check `uptime` before trusting a budget failure. The daemon tests skew the E2E budgets even on the E2E runner's pinned cores, so the E2E job starts after the daemon job (D280).
- A manual CI run (`workflow_dispatch`) has no `BASE_SHA`: the commit message check in `repo` then reads the whole history and fails on the 101-character squash subject of #56.
- `ProtocolApiTests.FtpNeedsTlsAndTheRightAndUsesTheFileService` can fail with "Broken pipe" (once in 40 runs, `dev` run of #89): the FTP server answers `STOR` without the right with `150` and refuses only when the transfer starts, so the client's write can hit a closed data connection.
- `ProcessCommandRunnerTests.StreamsLinesAndKillsTheCommandWhenCancelled` failed once on the runner (CI of #98: the stream ran the full 30 s of `sleep`), 15 of 15 green on dev01; rerun once. The E2E reload budget (500 ms) varies on the runner between about 450 and 520 ms for the same commit (#98: 451, 517, 501, 481).
- A daemon refuses a DB with unknown schema versions (exit 65); `install-dev-daemon.sh` then stops before touching anything. If a branch's daemon wrote the dev DB, run `reset-dev-db.sh`.
- `install-dev-daemon.sh` fast-forwards the checkout and then goes on with the copy of itself that bash already read; when the merge changed the script, its build can fail on the old steps (#93 removed `apps/demo`). Run it a second time.
- `pithyx-dev.service` allows 5 starts in 120 s; several `integration.sh` runs in a row leave it `start-limit-hit`: `sudo systemctl reset-failed pithyx-dev && sudo systemctl start pithyx-dev`.
- A test that starts its own `smbd` needs `setsid` and a stdin pipe that stays open: a socket on stdin is served as an inetd connection, a closed stdin ends smbd, and an ending smbd signals its whole process group.
- Old WIP commits were made with `--no-verify`; lefthook never checked them.
- A fresh worktree needs `pnpm install --frozen-lockfile` before the first commit, or the repo-check hook fails.
- An interrupted `image/build.sh` can leave mmdebstrap processes (they ignore SIGTERM inside the namespace; `pkill -9 -f "[m]mdebstrap --mode"`) and a root file system in `/tmp/mmdebstrap.*` owned by subuids (`unshare --map-auto --map-root-user rm -rf /tmp/mmdebstrap.*`). The script holds a lock on its work directory, so a second run refuses to start.
- `/tmp` on dev01 is an 11 GB tmpfs: the test VMs keep their disks in `image/out/vm` with `detect-zeroes=unmap`, because the mdadm resync of a mirror writes 72 GB of zeros and a full tmpfs stalls the VM.
- The live system and the installed system have no login (root is locked, SSH off). A failed installer step names its command in `errorParams` of `GET /api/installer`; a screenshot of the console comes from the QEMU monitor (`screendump`).

## Workflow

- Work through `TODO.md` top to bottom, one step at a time; finish its "Done when" before the next. `[DECISION]` and `[REVIEW]` steps need the owner: stop and ask. Check items off in the commit that finishes them.
- Branches: `master` stable, `dev` beta and integration. Work in `feature/`, `fix/`, `hotfix/<name>`; PRs target `dev`. Never commit directly to `master` or `dev` (lefthook `pre-push` refuses it). New branches: `git branch --unset-upstream` so a bare `git push` never targets `dev`.
- Parallel work only in worktrees under `.worktrees/<name>` (`git worktree add .worktrees/<name> -b feature/<name> origin/dev`). Remove worktree and local branch once merged.
- Commits and PR titles: `<gitmoji> <type>(<scope>): <subject>`, Unicode emoji, English, imperative, no em dashes. E.g. `✨ feat(desktop): add window snapping`, `🐛 fix(daemon): handle missing lsblk serial`, `♻️ refactor(storage): extract zpool parser`, `👷 ci: add DCO check`. Always `git commit -s` (DCO).
- PR flow: merge `dev` into the branch, push, wait for a green runner run (label `e2e` for larger changes, see CI). Storage, account and command runner changes also attach an `integration.sh` result from dev01. Then squash-merge into `dev` (you may once CI is green; `master` always needs the owner). The squash subject including ` (#n)` stays at or under 100 characters, and the message ends with `Signed-off-by: Schecher_1 <74829802+Schecher1@users.noreply.github.com>`. Then deploy with `install-dev-daemon.sh --yes` and check the change on 7484; a branch with a migration proves its upgrade there (the daemon keeps `pithyx.db.pre-<version>-<time>.bak`).
- Apps waves: one PR per wave, see `TODO.md` "Working rules" (D313).
- One PR run on the runner host at a time, and only after the previous merge's `dev` run finished; parallel runs break timing budgets.
- Generated files (`openapi.json`, `schema.ts`, `error-codes.ts`, `sdk/src/manifest.generated.ts`) are never merged by hand: take one side and regenerate (build the daemon and run `pnpm --filter @pithyx/api-client generate`; the manifest types come from `sdk/schema/manifest.v1.schema.json`).
- Every user-visible or developer-relevant change adds a line under `Unreleased` in `CHANGELOG.md` (Keep a Changelog).
- SemVer for the product; releases tagged on `master` as `vX.Y.Z` with a constellation codename. The npm packages (`sdk/`, `packages/ui`, `packages/cli`) have their own SemVer line, all at the same version, tagged `sdk-vX.Y.Z`; release candidates go to the dist-tag `next`, `latest` only after the owner's approval. Only the owner publishes, with `tools/release/publish-sdk.sh` and npm 2FA; no npm token is stored (D306).

## Before you commit

- Build, lint, tests green on the runner; generated clients current.
- New visible text has `en` and `de` keys; new error codes have translations (their website entries come with the docs, D312).
- `TODO.md` checked off, `CHANGELOG.md` updated.
- No `website/` docs until v0.1 is complete (D312): `website/` does not exist yet, and the user, admin and developer docs are written once from the code at the v0.1 release (`TODO.md`, "Scope of v0.1"). Until then commits add no doc pages; `CHANGELOG.md` and the code carry the behavior. After that: update pages describing changed behavior in the same commit, and add a page when a user or admin would otherwise guess (new feature or setting, changed behavior, new error code, limitation, manual step), not for refactors or developer-only details.
- No em dashes, no non-English text outside translation files.

## Repository

```
daemon/   C# / ASP.NET Core (.NET 10), the only privileged process
desktop/  Vite + React + TypeScript web desktop
sdk/      npm package for app developers (@pithyx/sdk, MIT), manifest and compose schemas in sdk/schema/
packages/ api-client (generated), ui (@pithyx/ui, MIT), cli (@pithyx/cli, MIT, the `pithyx` command)
apps/     bundled apps (files, settings, store)
image/    ISO build (mmdebstrap, squashfs, GRUB, xorriso)
i18n/     translation files (en is the reference)
website/  landing page and user docs (Starlight), created at the v0.1 release (D312)
```

- One monorepo, one product version (the npm packages have their own, see Workflow). Nested rules: `daemon/CLAUDE.md` (architecture, paths, operations, VMs), `desktop/CLAUDE.md` (responsive, accessibility, performance, layout memory); equally binding.
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
- No Secure Boot (installer detects it and asks to disable). SSH off by default; its MOTD shows the ASCII logo and a warning. The ASCII logo also appears on the console after boot and in the installer's console (D294); it is the only allowed ASCII art.
- Pool encryption: ZFS native. Unlock manually, from a key vault on the system disk (UI explains it only protects against stolen disks) or a USB key.

## Apps

- Every app, core or third-party, uses `@pithyx/sdk` with an identical API: core apps in-process transport, third-party a MessageChannel (one per app instance; identity from the port, never from message data).
- Third-party apps run in an iframe `sandbox="allow-scripts allow-forms"` without `allow-same-origin`, same host. They store data through the SDK, not `localStorage`/IndexedDB.
- An app is a static frontend served by the daemon plus an optional backend of zero to n containers (D300). It ships built as `<id>-<version>.pithyxapp` (deterministic tar.gz: `pithyx.json`, `icon.svg`, `frontend/`, optional `backend/compose.yaml` with `images.lock.json`); the box never runs npm or a compiler (D301). Web apps with their own port (`kind: "web"`) come after SDK 1.0.
- IDs (D302): the author's domain reversed plus the app name, 3 to 5 parts (`com.mueller.chat`), validated by `AppId` in the daemon and the schema in `sdk/schema/`. System apps are `org.pithyx.<name>`; the box accepts `org.pithyx.*` only for system apps and the official catalog, never from a sideload. The box stores each app's origin (sideload or catalog), no `local.*` ids.
- Routes (D303): frontend `/apps/<id>/...`, backend endpoint `api` at `/apps/<id>/api/`, any other endpoint `<name>` at `/apps/<id>/api-<name>/`; endpoints have names, container ports never appear in a URL. Icons from the database at `/api/apps/{id}/icon`. The daemon's API for apps is `/api/app/**`.
- Frontend headers, mandatory on every file under `/apps/<id>/`: `Content-Security-Policy: sandbox allow-scripts allow-forms; default-src 'self'; ...` (`connect-src https:` only with `network.internet`, never scripts from a CDN), `X-Content-Type-Options: nosniff`, `Access-Control-Allow-Origin: *`. Without `sandbox`, a frontend opened directly would run in the desktop's origin with the user's session. The icon is served with `default-src 'none'; sandbox`.
- Tokens (D303): the desktop gets them with `POST /api/apps/{id}/tokens` (cookie and CSRF header) and keeps them in memory; 256 bits, 5 minutes, bound to user, session, app and window instance. `host` tokens only for `/api/app/**` (the app's identity comes from the token, never from the route), `backend` tokens only for the proxy (bearer header or `?pithyx-token=`), one-time WebSocket tickets valid 30 seconds (`?pithyx-ticket=`). A cookie alone never authenticates at the proxy; tokens and tickets are masked in logs.
- Backends: one instance per app shared by all users (separated by `X-Pithyx-User`), on an internal Docker network of its own without internet (outgoing only with `network.internet`), no host ports, with an own uid from 40000 to 49999. Root in the container only when declared (`backend.root`), unprivileged through userns and shown with a warning. The backend is a Compose subset with images pinned by digest; the daemon checks it against the allowlist `sdk/schema/compose.v1.rules.json` and rewrites it (D301). Reached only through YARP in the daemon, which strips `Cookie`, `Authorization`, the `pithyx-*` parameters and incoming `X-Pithyx-*` headers (mandatory for every proxied app, including external ones) and sets `X-Pithyx-User`, `X-Pithyx-User-Id`, `X-Pithyx-Groups`, `X-Pithyx-Admin`, `traceparent` and `X-Forwarded-Prefix`. No `docker.sock`, `privileged` or host network unless the manifest requests the elevated right and the install dialog shows it; v0.1 refuses `docker.socket` and `host.full`.
- App data lives on the app pool the administrator picks at the first install: a dataset per app, Docker's data on the same pool, nothing on the system disk (paths in `daemon/CLAUDE.md`).
- App logs (D305): every level also goes to the browser console; `info`, `warn` and `error` to the daemon, `debug` only while an administrator switched it on. The daemon adds the checked user; backend output lands in the same source `app/<id>`.
- Apps are developed with `pithyx dev` on the developer's machine and installed from the `.pithyxapp`; there is no development mode on the box (D307).
- Lifecycle in the manifest: `always` or `onDemand` (runs while any window is open, plus `idleTimeout`, default 10 min). Windows are counted through the per-user topic `apps/<id>/context` (the window lease) with a 2-minute grace on disconnect. Background work needs a keep-alive lease or manifest schedules; otherwise idle backends get SIGTERM with a timeout.
- Window instances in the manifest: `single` (focus the user's window on this device, pass launch args) or `multi`.
- Apps with mounts on a locked or missing pool do not start; the UI says why. Revoked permissions apply on the next token refresh, reach the app as `permissionsChange` and close affected windows and proxied sockets over the WebSocket.
- External web apps (Immich, Jellyfin) keep their own ports behind a YARP listener that may rewrite `X-Frame-Options`/`frame-ancestors`. Linked apps on other hosts open in a window if framable, else a new tab.
- Apps open in windows, never redirect the page; "Open in tab" is optional.

## Misc

- Dates: store and transmit UTC ISO 8601; format with `Intl` per user time zone and locale.
- Fonts: IBM Plex Sans (UI), Sora (word mark, large headings), JetBrains Mono (terminal, logs, paths), all OFL 1.1, shipped unmodified (a subset of Plex would need a new name). Every bundled third-party asset keeps its license and appears in the About page notices.
- Stable contracts (breaking = major bump): REST/WS API, SDK API, manifest schema, extension points, error codes.

## Tests

- Test parsers, decision logic, permission checks, manifest validation, the update state machine. Not getters, DTOs, pass-through controllers or individual React components.
- Daemon tests run in simulation (`DaemonFactory`) with a fake clock where time matters. E2E in `e2e/` (Playwright, `pnpm build && pnpm e2e`) with its own daemon and fresh DB.
- Tests needing root, real ZFS, loop devices, scsi_debug or Linux accounts (`Category=StorageIntegration`) or a real Docker engine (`Category=DockerIntegration`) never run in GitHub CI, only on dev01 via `tools/dev-vm/integration.sh`. Storage and Docker PRs run their destructive suite there before merge.
- Contracts shared by the CLI and the box (manifest, compose) have one fixture corpus under `sdk/schema/fixtures/` that runs in Vitest and xUnit, so both judge alike.

## CI

- Jobs in `.github/workflows/ci.yml` use `${{ vars.CI_RUNNER || 'ubuntu-latest' }}` (self-hosted `pithyx-ci` on dedi-01); E2E uses `${{ vars.CI_RUNNER_E2E || vars.CI_RUNNER || 'ubuntu-latest' }}` (`pithyx-e2e`, pinned cores, D280).
- The runner has no Docker and no root: no `services:` or `container:`, nothing needing ZFS, disks or root. The owner maintains it: never change it or add workflow workarounds for something missing; tell the owner.
- Workflows: top-level `permissions: contents: read`, widened per job only when needed. No `pull_request_target`, no secrets in logs, no fork code without approval.
- The runner does all build-only work (build, lint, typecheck, unit tests, E2E, perf gate): push, keep working, read the result. Use dev01 only for ZFS/root tests, image builds and deploys. There is no local CI fallback.
- Every PR runs `changes` and `repo` (format, repo rules, shellcheck, commit messages, sign-off), plus `daemon` (without `StorageIntegration`, `DockerIntegration` and `TimingBudget`) and `frontend` when their files changed. That suffices for a small change.
- E2E with perf gate and design checks only on request (D278): label `e2e` (`gh pr edit <n> --add-label e2e`), `gh workflow run CI --ref <branch>`, or any PR to `master`. Add the label for user flows, desktop shell, window manager, sign-in, setup, SDK transport, API contract, loading path or bundle, or several PRs merged in a row. Run it on `dev` before the owner verifies a round on 7484.
