# Pithyx

Free, self-hosted NAS and server OS with a web desktop. Debian 13, C# daemon, React desktop, apps as containers. AGPL-3.0 with the terms in `NOTICE` (section 7, D344, D349: forks keep "Based on Pithyx, originally created by Schecher1" in `NOTICE`, source, docs and About page; a modified version needs its own name and logo); the npm packages (`@pithyx/sdk`, `@pithyx/ui`, `@pithyx/cli`) MIT without it. GitHub org `pithyx`, npm org `pithyx`.

These rules are binding. If a rule blocks you, stop and ask; never work around it.

## Principles

- 100 percent local: no telemetry, phone-home, cloud accounts or project-run services the box depends on. Crash reports stay on the box; the user shares a support bundle manually (support bundles are planned, not built yet).
- Fresh installs only, no import from other NAS systems.

## Environments

`/etc/pithyx/environment` says `dev` or `prod`; if it is missing or unclear, assume prod. Every machine has exactly one Pithyx installation (D277).

- **dev** (development VMs, simulation, CI): the machine is yours. Install packages, change services and config, load modules, reboot, create and destroy pools, reset databases, edit any file, without asking. Document afterwards (commit or decision entry).
- Hard limits in dev: never push or merge to `stable` or `beta`; never delete, force-push or rewrite `origin/stable`, `origin/beta` or `origin/dev`. Never touch the system disk; destructive storage tests use only the disks listed in `/etc/pithyx/test-disks` (scsi_debug disks are free). No secrets in commits or chat; never read password hashes or session tokens from the database, use the API. Commit and push before a reboot.
- Test pools: `tools/dev-vm/testpool.sh create|destroy|wipe` (refuses non-test disks). After `zpool destroy` run `testpool.sh wipe`.
- **prod** (real data): read-only. Before any write show the exact command and wait for confirmation; snapshot or backup first. Never `zpool destroy/create`, `mkfs`, `wipefs`, `dd`, `rm -rf` on data, `docker volume rm` or migrations without a confirmed backup.
- The product does the same: destructive storage actions refuse disks with partitions or data unless the user confirms with the disk serial.
- Local, untracked `CLAUDE.local.md` files for personal setup are allowed (they are in `.gitignore`).

## Current state

- Version `1.0.0` (codename Pyxis) is released: the tag `v1.0.0` on `stable`, a GitHub pre-release with the ISO and the GPL sources, on the stable channel and marked beta (D366); fixes come as 1.0.x. The repositories of the organization are public since 2026-10-18, and the docs are published from `stable` at pithyx.org.
- Built: HTTPS on 7484 with the box as its own certificate authority (D346), the reverse proxy on 443 and 80 with redirect hosts and TCP/UDP streams (D347), storage (ZFS pools, disks, SMART, snapshots, scrubs), users, groups and permissions, home folders (D348), shared folders with SMB, SFTP, WebDAV and FTP, the web desktop with Files and Settings, the app runtime (SDK, manifest, Docker backends, proxy, Store with signed catalogs), network settings, firewall, config backup, the web installer, the ISO build and signed system updates with automatic rollback.
- Not built: pool encryption, NFS, VMs, support bundles; app rights that need elevation (`docker.socket`, `host.full`) are refused with `app.elevated.unsupported`.
- Docs pages under `website/` are all `ready` (checked against the code); new pages start as `draft` until checked. `TODO.md` marks what waits "(after 1.0.0)".
- The migrations are numbered SQL files in `daemon/`; decisions are logged as D-numbers at the end of `TODO.md`, in number order (it conflicts on every merge; keep all entries sorted).

## Known pitfalls

- Load spoils timings: check `uptime` before trusting a budget failure on a dev VM. In CI the E2E job starts after the daemon job (D280).
- A manual or nightly CI run has no `BASE_SHA`, so `repo` skips the commit message and sign-off checks there.
- Known flaky daemon tests are rerun once by `tools/checks/dotnet-test-retry.ts`; the E2E timing budgets are never retried.
- A daemon refuses a database with unknown schema versions (exit 65).
- Disk-pull tests with scsi_debug can wedge ZFS until reboot; for a returning disk run `zpool online` before `zpool clear`, both with a timeout (D270).
- A package a workflow pushes to GHCR is private, also from a public repository, until it is made public in the web interface.
- A fresh worktree needs `pnpm install --frozen-lockfile` before the first commit, or the repo-check hook fails.
- A test that starts its own `smbd` needs `setsid` and a stdin pipe that stays open.
- An interrupted `image/build.sh` can leave mmdebstrap processes and a root file system in `/tmp/mmdebstrap.*`; the script holds a lock on its work directory, so a second run refuses to start.

## Workflow

- Work through `TODO.md` top to bottom, one step at a time; finish its "Done when" before the next. `[DECISION]` and `[REVIEW]` steps need the maintainer: stop and ask. Check items off in the commit that finishes them.
- Branches (D367): `stable` is stable and the default branch (releases are tagged there; it was `master` until 2026-10-18), `beta` the beta channel, `dev` the owner's test and integration branch. Changes flow feature → `dev` → `beta` → `stable`: `beta` and `stable` change only through pull requests from `dev` and `beta` (a hotfix branches from `stable` and goes back into `beta` and `dev`), guarded by the ruleset and lefthook `pre-push`. Work in `feature/`, `fix/`, `hotfix/<name>`; PRs target `dev`, and small changes may be pushed to `dev` directly. New branches: `git branch --unset-upstream` so a bare `git push` never targets `dev`.
- Parallel work only in worktrees under `.worktrees/<name>` (`git worktree add .worktrees/<name> -b feature/<name> origin/dev`). Remove worktree and local branch once merged.
- Commits and PR titles: `<gitmoji> <type>(<scope>): <subject>`, Unicode emoji, English, imperative, no em dashes. E.g. `✨ feat(desktop): add window snapping`, `🐛 fix(daemon): handle missing lsblk serial`, `♻️ refactor(storage): extract zpool parser`, `👷 ci: add DCO check`. Always `git commit -s` (DCO).
- PR flow: merge `dev` into the branch, push, wait for a green CI run (label `e2e` for larger changes, see CI). Storage, account and command runner changes also attach an `integration.sh` result from a dev VM. Then squash-merge into `dev` (you may once CI is green; `beta` and `stable` always need the maintainer). The squash subject including ` (#n)` stays at or under 100 characters, and the message ends with `Signed-off-by: Schecher_1 <74829802+Schecher1@users.noreply.github.com>`: GitHub makes the owner's profile name the squash commit's author, and the sign-off check wants both equal (the owner's name is Schecher1 everywhere else; once the GitHub profile name is Schecher1, so is this line). A branch with a migration proves its upgrade on a dev VM (the daemon keeps `pithyx.db.pre-<version>-<time>.bak`).
- Apps waves: one PR per wave, see `TODO.md` "Working rules" (D313).
- Generated files (`openapi.json`, `schema.ts`, `error-codes.ts`, `sdk/src/manifest.generated.ts`) are never merged by hand: take one side and regenerate (build the daemon and run `pnpm --filter @pithyx/api-client generate`; the manifest types come from `sdk/schema/manifest.v1.schema.json`).
- Every user-visible or developer-relevant change adds a line under `Unreleased` in `CHANGELOG.md` (Keep a Changelog).
- SemVer for the product; releases tagged on `stable` as `vX.Y.Z` with a constellation codename. The npm packages (`sdk/`, `packages/ui`, `packages/cli`) have their own SemVer line, all at the same version, tagged `sdk-vX.Y.Z`; release candidates go to the dist-tag `next`, `latest` only after the maintainer's approval. Only the maintainer publishes, with `tools/release/publish-sdk.sh` (or `tools/release/pithyx-release publish-sdk`, the release tool over every release script, D364) and npm 2FA; no npm token is stored (D306).

## Before you commit

- Build, lint, tests green in CI; generated clients current.
- New visible text has `en` and `de` keys; new error codes have translations (the website's error reference is generated from them).
- `TODO.md` checked off, `CHANGELOG.md` updated.
- Docs (D340): every change checks whether the docs under `website/src/content/docs/` describe it. If a page is wrong, update it in the same commit, English and German. If nothing describes it and a user, admin or developer would otherwise guess (new feature or setting, changed behavior, new error code, limitation, manual step), add it to the matching page or area or create a page; a question users will likely ask becomes an FAQ file. Refactors and internal details need no page. How to find and write pages: `website/CLAUDE.md`.
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
website/  landing page and docs at pithyx.org (Astro + Starlight, GitHub Pages, D340)
```

- One monorepo, one product version (the npm packages have their own, see Workflow). Nested rules: `daemon/CLAUDE.md` (architecture, paths, operations, VMs), `desktop/CLAUDE.md` (responsive, accessibility, performance, layout memory), `website/CLAUDE.md` (where docs live and how to write them); equally binding.
- No developer docs in markdown outside `website/`; the code documents itself. Allowed markdown outside `website/`: README, CLAUDE (root and per top-level folder), CONTRIBUTING, TODO, CHANGELOG, SECURITY, and the GitHub templates GitHub needs in `.github/` (`pull_request_template.md`; the issue forms in `.github/ISSUE_TEMPLATE/` are YAML). No other markdown for agents; state lives in this file.
- `website/` publishes every docs page also as Markdown (`<page>.md`), `/llms.txt` (index), `/llms/<area>.txt` (one area) and `/llms-full.txt` (everything). API and SDK references are generated from OpenAPI and TSDoc.
- An API change updates daemon, generated client, SDK and desktop in the same commit. Never edit generated files.
- Committed content is English. Only `i18n/` and translated `website/` pages may be otherwise. Chat with maintainers may be German.

## Code style

- Short English comments only where logic is not obvious; prefer clear names. No em dashes, banners, divider comments or commented-out code.
- Warnings are errors in C# and TypeScript.
- YAML files end in `.yaml`, never `.yml` (the repo check refuses them).
- No abstraction without a second real implementation or an enforced boundary (`ICommandRunner`, `IStorageBackend` are the exceptions). No repository layer over Dapper, no mediator, no single-implementation interfaces.

## Platforms and budgets

- Supported: x86-64-v2+, UEFI, 64-bit. Community: arm64 (UEFI SystemReady, Raspberry Pi 5); keep code architecture-neutral. No 32-bit.
- Reference hardware: Intel N100/N305, 8 GB RAM. Still frugal: daemon below 200 MB RSS and 1 percent CPU idle; nothing polls when an event source exists. These are CI budgets.

## Network

- HTTPS only on the fixed port 7484 (D346); never change it. Ports 80 and 443 serve the reverse proxy's hosts by name and redirect every other name to `https://<host>:7484` (D347), and plain HTTP on 7484 is redirected to `https://`. No HSTS for the box itself (a proxy host may opt in). The installer's live system and simulation stay on plain HTTP.
- The box is its own certificate authority (root and intermediate with Name Constraints to local names and private addresses unless an administrator lifts them); Settings > Certificates offers the root with per-device instructions, certificates for other devices and an own certificate. Let's Encrypt comes later. Everything works on the untrusted default; passkeys and the service worker switch on only after the trust step.
- The UI never assumes a secure context (plain HTTP by IP must work). `crypto.randomUUID`, `crypto.subtle`, Clipboard, service workers, WebAuthn, notifications only through `desktop/src/platform/browser.ts` (fallback or "missing"); ESLint refuses them elsewhere.
- No subdomains for the core system. IPv6 everywhere IPv4 works.

## Errors and i18n

- Stable codes `area.object.reason` (lowercase, dots, underscores inside words), e.g. `fs.file.not_found`. Defined once as C# constants; the TS union is generated. Exception: SDK transport codes (`sdkErrorCodes` in `sdk/src/errors.ts`), texts under `errors.sdk.*`.
- RFC 9457 problem details with `code`, `params`, `detail` (English fallback), `requestId`. The API never returns translated text, except out-of-band channels (email, push; none built yet; the SSH MOTD is English for now) rendered server-side in the recipient's language.
- No user-visible string literals; keys `area.screen.element`, errors under `errors.<code>`. English reference, German required, ICU plurals and params, German uses informal "du". Never name companies or their products (no other NAS vendors); open-source projects may be named.

## Tracing and logging

- Every HTTP request and WebSocket message has a request id (W3C traceparent, `Activity`), propagated to jobs, every `ICommandRunner` call and proxied app backends.
- Every external command logs command, args, exit code, duration, truncated stdout/stderr. Structured JSON logs only (Serilog), no string concatenation.
- Secrets always masked. Support bundles (planned, not built yet): logs, config, system info without secrets; memory dumps only by explicit opt-in with a warning.
- The UI never shows the request id; it goes to the console and logs.

## Users and security

- Permissions to groups and users; effective = union; admins have everything. Share access: none, read, read-write. Protocol access per user (SMB, SFTP, WebDAV, FTP; NFS is planned, not built yet). App access per app. Special rights: admin, desktop login, SSH login, reboot/shutdown.
- TOTP 2FA per user. Login forms have correct `autocomplete` attributes.
- No password reset without proof: only the recovery key from setup, otherwise reinstall.
- No Secure Boot (installer detects it and asks to disable). SSH off by default; its MOTD (`/etc/update-motd.d/10-pithyx`, run by pam_motd) shows the ASCII logo, the version and a warning, in English. The ASCII logo (`image/rootfs/usr/lib/pithyx/logo.txt`: the isometric box with the lid shut and the word mark PITHYX, at most 72 columns) also appears on the console after boot and in the installer's console (D294); the SSH MOTD uses the same file; it is the only allowed ASCII art.
- Pool encryption: ZFS native. Unlock manually, from a key vault on the system disk (UI explains it only protects against stolen disks) or a USB key.

## Apps

- Every app, core or third-party, uses `@pithyx/sdk` with an identical API: core apps in-process transport, third-party a MessageChannel (one per app instance; identity from the port, never from message data).
- Third-party apps run in an iframe `sandbox="allow-scripts allow-forms"` without `allow-same-origin`, same host. They store data through the SDK, not `localStorage`/IndexedDB.
- An app is a static frontend served by the daemon plus an optional backend of zero to n containers (D300). It ships built as `<id>-<version>.pithyxapp` (deterministic tar.gz: `pithyx.json`, `icon.svg`, `frontend/`, optional `backend/compose.yaml` with `images.lock.json`); the box never runs npm or a compiler (D301). Web apps with their own port (`kind: "web"`) come after SDK 1.0.
- IDs (D302): the author's domain reversed plus the app name, 3 to 5 parts (`com.mueller.chat`), validated by `AppId` in the daemon and the schema in `sdk/schema/`. System apps are `org.pithyx.<name>`; the box accepts `org.pithyx.*` only for system apps and the official catalog, never from a sideload. The box stores each app's origin (sideload or catalog), no `local.*` ids.
- Routes (D303): frontend `/apps/<id>/...`, backend endpoint `api` at `/apps/<id>/api/`, any other endpoint `<name>` at `/apps/<id>/api-<name>/`; endpoints have names, container ports never appear in a URL. Icons from the database at `/api/apps/{id}/icon`. The daemon's API for apps is `/api/app/**`.
- Frontend headers, mandatory on every file under `/apps/<id>/`: `Content-Security-Policy: sandbox allow-scripts allow-forms; default-src 'self'; ...` (`connect-src https:` only with `network.internet`, never scripts from a CDN), `X-Content-Type-Options: nosniff`, `Access-Control-Allow-Origin: *`. Without `sandbox`, a frontend opened directly would run in the desktop's origin with the user's session. The icon is served with `default-src 'none'; sandbox`.
- Tokens (D303): the desktop gets them with `POST /api/apps/{id}/tokens` (cookie and CSRF header) and keeps them in memory; 256 bits, 5 minutes, bound to user, session, app and window instance. `host` tokens only for `/api/app/**` (the app's identity comes from the token, never from the route), `backend` tokens only for the proxy (bearer header or `?pithyx-token=`), one-time WebSocket tickets valid 30 seconds (`?pithyx-ticket=`). A cookie alone never authenticates at the proxy; tokens and tickets are masked in logs.
- Backends: one instance per app shared by all users (separated by `X-Pithyx-User`), on an internal Docker network of its own without internet (outgoing only with `network.internet`, other devices of the LAN only with `network.lan`, for the services in `backend.network`; D358), without Linux capabilities unless the app runs as root (D359), no host ports, with an own uid from 40000 to 49999. Root in the container only when declared (`backend.root`), unprivileged through userns and shown with a warning. The backend is a Compose subset with images pinned by digest; the daemon checks it against the allowlist `sdk/schema/compose.v1.rules.json` and rewrites it (D301). Reached only through YARP in the daemon, which strips `Cookie`, `Authorization`, the `pithyx-*` parameters and incoming `X-Pithyx-*` headers (mandatory for every proxied app, including external ones) and sets `X-Pithyx-User`, `X-Pithyx-User-Id`, `X-Pithyx-Groups`, `X-Pithyx-Admin`, `traceparent` and `X-Forwarded-Prefix`. No `docker.sock`, `privileged` or host network unless the manifest requests the elevated right and the install dialog shows it; 1.0.0 refuses every elevated right (D345).
- App data lives on the app pool the administrator picks at the first install: a dataset per app, Docker's data on the same pool, nothing on the system disk (paths in `daemon/CLAUDE.md`).
- App logs (D305): every level also goes to the browser console; `info`, `warn` and `error` to the daemon, `debug` only while an administrator switched it on. The daemon adds the checked user; backend output lands in the same source `app/<id>`.
- Catalogs (D328, D329): `pithyx/apps-official` (the only source of `org.pithyx.*` besides the image) and `pithyx/apps-community` (both on by default, D343), made from `pithyx/catalog-template`. Source in `apps/<id>/` on `master`, reviewed by the maintainer; CI builds and pushes images to `ghcr.io/<owner>/apps/<id>/<service>:<version>` (by digest, never overwritten) and writes the CI-only branches `catalog-next` and `catalog` with `index.json` signed by Ed25519 (`catalog` only after the maintainer approves in the environment `catalog-release`). The box clones enabled catalogs into `/var/lib/pithyx/catalogs/<id>/`, verifies the signature against keys built into the daemon and never builds. Custom catalogs are unsigned and unverified. System apps stay in the image.
- Apps are developed with `pithyx dev` on the developer's machine and installed from the `.pithyxapp`; there is no development mode on the box (D307).
- Lifecycle in the manifest: `always` or `onDemand` (runs while any window is open, plus `idleTimeout`, default 10 min). Windows are counted through the per-user topic `apps/<id>/context` (the window lease) with a 2-minute grace on disconnect. Background work needs manifest schedules (a keep-alive lease is planned, not built yet); otherwise idle backends get SIGTERM with a timeout.
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
- Tests needing root, real ZFS, loop devices, scsi_debug or Linux accounts (`Category=StorageIntegration`) or a real Docker engine (`Category=DockerIntegration`) never run in GitHub CI, only on a dev VM via `tools/dev-vm/integration.sh`. Storage and Docker PRs run their destructive suite there before merge.
- Contracts shared by the CLI and the box (manifest, compose, catalog) have one fixture corpus under `sdk/schema/fixtures/` that runs in Vitest and xUnit, so both judge alike.

## CI

- Every job of `.github/workflows/ci.yaml` and `pages.yaml` runs on GitHub-hosted runners (`ubuntu-latest`, free for the public repository, D368); there are no self-hosted runners.
- A pull request from a fork runs only after a maintainer approved it (repository setting, Actions, General, "Fork pull request workflows from outside collaborators": "Require approval for all outside collaborators"). No secrets reach fork runs.
- No `services:` or `container:`, nothing needing ZFS, real disks or root in CI: those tests run on a dev VM.
- Workflows: top-level `permissions: contents: read`, widened per job only when needed. No `pull_request_target`, no secrets in logs, no fork code without approval.
- CI does all build-only work (build, lint, typecheck, unit tests, E2E, perf gate): push, keep working, read the result. Use a dev VM only for ZFS/root tests, image builds and deploys. There is no local CI fallback.
- Every PR runs `changes` and `repo` (format, repo rules, shellcheck, commit messages, sign-off), plus `daemon` (without `StorageIntegration`, `DockerIntegration` and `TimingBudget`) and `frontend` when their files changed. That suffices for a small change.
- Nightly (`schedule`, 01:17 UTC, only in `pithyx/pithyx`; it starts on the default branch `stable`, and every checkout takes `dev`, D367): the full CI including E2E, perf gate and design checks. The commit message and sign-off checks are skipped there and on manual runs (no range). GitHub disables a schedule after 60 days without repository activity.
- Caches: pnpm (`setup-node`), NuGet (`setup-dotnet`, keyed on `packages.lock.json`) and the Playwright browsers.
- Flaky daemon tests: the `daemon` job runs `tools/checks/dotnet-test-retry.ts`, which reruns once, alone, only failed tests that are known flakes (message "database is locked", or listed in the script) and logs the rerun as a warning annotation. Any other failed test fails the job at once. Add a test to the list only with its cause in the Known pitfalls; the E2E timing budgets are never retried.
- Required checks of the branch ruleset on `stable` and `beta` (`tools/github/protect-branches.sh`, created when the repository went public on 2026-10-18): `changes`, `daemon`, `frontend`, `website`, `repo`; a skipped job counts as passed. `e2e` is not required.
- E2E with perf gate and design checks only on request (D278): label `e2e` (`gh pr edit <n> --add-label e2e`), `gh workflow run CI --ref <branch>`, or any PR or push to `beta` or `stable`. Add the label for user flows, desktop shell, window manager, sign-in, setup, SDK transport, API contract, loading path or bundle, or several PRs merged in a row. Run it on `dev` before a round is verified on a dev VM.
