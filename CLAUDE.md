# Pithyx

Free, self-hosted NAS and server OS with a web desktop. Debian 13 base, C# daemon, React desktop, apps as containers. License: AGPL-3.0, SDK under MIT. Hosted on GitHub (org `pithyx`).

These rules are binding. If a rule blocks you, stop and ask. Do not work around it.

## Principles

- 100 percent local. No telemetry, no phone-home, no cloud accounts, no project-run services the box depends on.
- Crash reports stay on the box. The user can create a support bundle and share it manually.
- Fresh installs only. There is no import from Synology, XPEnology or other systems.

## Environments

Know where you are before you act. Every machine declares its environment in `/etc/pithyx/environment` (`dev` or `prod`); the owner may also state it ("this is dev", "this is prod"). If neither says anything, treat it as prod. `dev` and `prod` say whether a machine may be broken or holds real data; they are not two installations. Every machine has exactly one Pithyx installation (D277).

- **dev** (dev01, other dev VMs, simulation mode, CI): the machine belongs to the agent. Nothing needs permission: install and remove packages (apt), change services and configuration, load kernel modules, reboot, create and destroy pools and arrays, reset databases, edit any file in the repo including every `CLAUDE.md`. Do it, then document it (commit message, D entry or the current state section below).
- The only hard limits in dev: never push or merge to `master` (merging into `master` always needs the owner), and never delete, force-push or rewrite `origin/master` or `origin/dev`. On dev01 also: never touch `sda` (the system disk); real test disks are `VYXTEST01` to `VYXTEST06` (listed in `/etc/pithyx/test-disks`, historical VYXTEST prefix from Proxmox), scsi_debug disks are free to use. No long write tests or benchmarks (the host's SSD is worn). No access to pve02, the home network or the CI runner's server. No secrets in commits or chat; never read password hashes or session tokens from the database, use the API. Before a reboot, commit and push all work (WIP branches with `🚧 wip:` are fine), say so in the chat and wait for the owner's "weiter": the session runs on dev01 itself, so a reboot ends it.
- `tools/dev-vm/testpool.sh` (`create`, `destroy`, `wipe`) is the convenient way to handle test pools; it refuses any disk that is not a test disk. `zpool destroy` leaves labels and partitions behind; run `testpool.sh wipe` afterwards.
- **prod** (anything with real data, including the owner's own NAS): read-only by default. Before any write, show the exact command and wait for explicit confirmation. Take a snapshot or backup first. Never run `zpool destroy`, `zpool create`, `mkfs`, `wipefs`, `dd`, `rm -rf` on data paths, `docker volume rm` or database migrations without a confirmed backup.
- The product follows the same idea: destructive storage actions in the daemon refuse to touch disks that contain partitions or data unless the user confirms with the disk serial.

## Dev VM

- `pithyx-dev.service` on port 7484 is the one dev instance, with one database at `/var/lib/pithyx/pithyx.db`. It always runs, so the owner can sign in and watch, except while `tools/dev-vm/integration.sh` runs its tests: the script stops the unit for the test run and starts it again afterwards, because the daemon manages every pool it sees, the throwaway test pools included (D276). Never point it at another database or data directory.
- It runs dev only: `tools/dev-vm/install-dev-daemon.sh` fast-forwards the main checkout to `origin/dev`, builds and restarts the unit. Deploy after every merge into `dev`.
- Reset the database only with `tools/dev-vm/reset-dev-db.sh`: it stops the unit, deletes the database, starts the unit (migrations run on start) and sets up the owner account again.
- The owner account's credentials live in `~gabriel/.config/pithyx-dev/owner.env` (mode 600, never in the repo, never in the chat). Never change the owner account: no password change, no 2FA reset, no deactivation. A reset turns the owner's 2FA off; report that.
- Tests use their own accounts `e2e-admin` and `e2e-user` from `~gabriel/.config/pithyx-dev/e2e.env`, created by `tools/dev-vm/seed-dev-accounts.sh`.
- 7484 is the only installation on dev01: real mode, the database on the system disk like on a box, the pools on the test disks. There is no second instance, no `.pithyx-dev/` and no simulation on dev01; a daemon from a checkout without `PITHYX_DB` refuses to start (exit code 78). Simulation exists only for the automated tests and E2E on the runner, each with a throwaway database. A fresh state means `reset-dev-db.sh`.
- Branches are checked before the merge by the runner (and `integration.sh` for ZFS) and on 7484 after the merge and deploy; what fails there is fixed right away.

## Current state

Update this section in the same PR whenever the state changes (a merge, a new branch, a finished item). Last updated: 2026-09-30.

- 7484 runs `dev` from the main checkout `/home/gabriel/code/pithyx` in real mode. Check with `systemctl status pithyx-dev` and `git -C /home/gabriel/code/pithyx log -1`; the deployed commit must equal `origin/dev`.
- On 2026-09-30 every database on dev01 was deleted (7484's, its backups, leftovers of old second instances) so the owner can run the setup again. The owner set it up again. `owner.env` is stale until the owner updates it with the new credentials; `seed-dev-accounts.sh` needs it to create `e2e-admin` and `e2e-user` (the users API from #46 is on `dev` now).
- The runner has `libatomic1`, `shellcheck` and the Playwright libraries (owner, 2026-09-30). dev01 has `acl` (`setfacl`).
- Merged into `dev` on 2026-09-30 by the owner's decision without waiting for the runner, then one full run on `dev`: #50, #51, #35, #52 (usage refresh), #37, #40, #36 (migration `0012_notifications.sql`), #38, #45, #53 (quick fixes B1 to B8), #54 (regenerated API client), #46 (users, migration `0013_linux_accounts.sql`), #55 (CI fixes). Migrations on `dev`: 1 to 13 without gaps.
- The usual way again: merge `dev` into the branch, push, green runner run (E2E with the label `e2e` for larger changes), for storage, account and command runner changes also `integration.sh` on dev01 with the result in the PR, squash-merge (the commit ends with `Signed-off-by: Schecher_1 <74829802+Schecher1@users.noreply.github.com>`), deploy with `install-dev-daemon.sh --yes`. A branch with a migration proves its upgrade path on 7484 after the merge; the daemon keeps a `pithyx.db.pre-<version>-<time>.bak` next to the database before it migrates. Generated files (`openapi.json`, `schema.ts`, `error-codes.ts`) are never merged by hand: take one side, build the daemon, run `pnpm --filter @pithyx/api-client generate`.
- Open:
  1. #56 `feature/theme-switch`: appearance in the account menu, the clock as plain text.
  2. `wip/users-acl-disable` (never built, ACLs as raw xattrs, adds `auth.account.disabled`), on top of the old `feature/users`: rebase onto `dev`, choose `setfacl` or a library for ACLs (D entry, also in the image), D264 (primary group `users` gid 100, homes 0700, umask 077, share access only through ACLs), register the UsersPage in Settings.
  3. `wip/settings-c1-c4` (never built): migrations become `0014_appearance.sql` and `0015_restore_points.sql`; the network page stays read-only until the 60 s rollback of step 14. Its per-user appearance replaces the per-device choice of #56.
- Not started: pool delete as a background job (the dialog closes at once, progress in notifications and jobs, a hanging `zpool destroy` ends with a new, translated and documented error code). Then the rename from the old project name (patterns in `OLD_NAME` in `tools/checks/repo-check.ts`): code, packages, headers, environment variables, paths, datasets, units, docs and links; exceptions are the `VYXTEST` serials, the git history and existing D entries (plus a new D entry); no shims; migrate dev01 (state directories, database, `zfs rename`, units, the old `/etc` directory) until `systemctl list-units`, `/etc`, `/var/lib`, `/var/log` and `zfs list` show nothing under the old name.
- Then verify every item of "Release 0.1" in `TODO.md` on 7484 in the browser (Playwright and screenshots, as `e2e-admin` or `e2e-user`), fix what fails, and report: deployed commit and `dev` HEAD, runner status per job, a table item, status, proof, the merged PRs with their migration numbers, the rename grep, open problems with cause and proposal.
- Decision numbers: D269 to D278 are on `dev`. The next free number is D279. The decision log at the end of `TODO.md` conflicts on every merge; keep all entries in number order.
- Worktrees under `.worktrees/`: `appearance` (#56), `settings` (`wip/settings-c1-c4`), `users` (`wip/users-acl-disable`), `state` (this update). Remove each with its local branch once merged.

## Known pitfalls

- Pulling a scsi_debug disk from a pool under write can wedge ZFS until a reboot (OpenZFS 2.3.9): `txg_sync` waits in `zio_wait`, `zpool clear`, `destroy` and `list` hang in D state, load goes to about 17. Only a reboot helps. Hence `--disk-pulls` only on a fresh boot.
- D270: for a returning disk, `zpool online` runs before `zpool clear`, both with a 30 s timeout. `clear` first hangs in the kernel.
- Load spoils timings: check `uptime` before trusting a budget; budget failures under load are not code failures.
- A daemon refuses a database with schema versions it does not know (exit code 65); `install-dev-daemon.sh` stops before touching anything then. If a branch's daemon wrote the dev database, run `reset-dev-db.sh`.
- WIP commits were made with `--no-verify`; the lefthook checks never ran on them.
- A fresh worktree needs `pnpm install --frozen-lockfile` before the first commit, or the repo-check hook fails.

## Nested rules

- `daemon/CLAUDE.md`: daemon architecture, paths, virtual machines.
- `desktop/CLAUDE.md`: responsive layout, accessibility, frontend performance, desktop layout memory.

Claude Code loads them when working in those folders. They are as binding as this file.

## Language

- Everything that gets committed is English: code, identifiers, comments, commit messages, PR descriptions, markdown files, user docs, generated reference docs.
- The only non-English content allowed in the repo is translation files (`i18n/`, translated doc pages under `website/`).
- Conversations with the owner may be in German. Output that ends up in a file is always English.

## Workflow

- Work through `TODO.md` from top to bottom, one step at a time. Do not start the next step before the current one meets its "Done when" criteria.
- Steps marked `[DECISION]` or `[REVIEW]` require the owner. Stop there and ask.
- Check off finished items in `TODO.md` in the same commit that finishes them.
- Commit messages: gitmoji plus Conventional Commits, English, imperative, no em dashes: `<gitmoji> <type>(<scope>): <subject>`. Use the Unicode emoji, not the `:shortcode:`. Examples: `✨ feat(desktop): add window snapping`, `🐛 fix(daemon): handle missing lsblk serial`, `📝 docs(website): explain spindown`, `♻️ refactor(storage): extract zpool parser`, `✅ test(auth): cover rate limit`, `🔧 chore: update lefthook`, `👷 ci: add DCO check`.
- The same format applies to PR titles.
- Every commit is signed off (`git commit -s`, DCO).
- Branches: `master` is stable, `dev` is beta and the integration branch. Work in `feature/<name>`, `fix/<name>`, `hotfix/<name>`. PRs target `dev`. Never commit directly to `master` or `dev`.
- GitHub branch protection is not available on the current plan; the lefthook `pre-push` hook refuses direct pushes to `master` and `dev` instead. New branches are pushed without an upstream on `dev` (`git branch --unset-upstream`), so a bare `git push` can never target `dev`.
- Parallel work uses git worktrees only under `.worktrees/<name>` in the repository (gitignored), for example `git worktree add .worktrees/<name> -b feature/<name> origin/dev`. Remove a worktree and its local branch as soon as its PR is merged (`git worktree remove .worktrees/<name>`).
- Claude may squash-merge a PR into `dev` once CI is green; merging into `master` always needs the owner. The squash commit uses the commit format above and ends with `Signed-off-by` for the merging GitHub account exactly as GitHub records the author (profile name and noreply address); otherwise the DCO check on `dev` fails.
- Every user-visible or developer-relevant change adds a line under `Unreleased` in `CHANGELOG.md` (Keep a Changelog format).
- Versioning: SemVer for the whole monorepo, releases tagged on `master` as `vX.Y.Z` with a constellation codename.

## Target platforms

- Supported: x86-64-v2 or newer, UEFI, 64-bit only. Code must not rely on anything below that.
- Community: arm64 (UEFI SystemReady, Raspberry Pi 5 image). Keep code architecture-neutral; no x86-only dependencies.
- Not supported: any 32-bit platform.
- Pithyx is not a lightweight NAS OS. The reference class is an Intel N100/N305 or better with 8 GB RAM or more. Single-board computers are not a goal.
- It must still be frugal: the daemon stays below 200 MB RSS and below 1 percent CPU when idle, and nothing polls when an event source exists. These are CI budgets like the frontend ones.

## Repository

Single monorepo. Everything that shares an API lives here and ships with one version number.

```
daemon/   C# / ASP.NET Core (.NET 10), the only privileged process
desktop/  Vite + React + TypeScript web desktop
sdk/      npm package for app developers (@pithyx/sdk)
apps/     bundled apps (files, settings, store)
image/    mmdebstrap, live-build, mkosi
i18n/     translation files (en is the reference language)
website/  landing page and user docs (Starlight, Markdown)
```

- No developer docs in markdown. The code is the developer documentation. Allowed markdown outside `website/`: README.md, CLAUDE.md (root and per top-level folder), CONTRIBUTING.md, TODO.md, CHANGELOG.md, SECURITY.md. No other markdown for agents: the current state and open work live in this file.
- User docs live in `website/`. After every change, check whether an existing doc page describes the changed behavior and update it in the same commit.
- Then decide whether the change deserves a new doc entry. It does if a user or admin would otherwise have to guess: a new feature or setting, changed behavior, a new error code, a limitation or a required manual step. It does not for internal refactors, performance work without visible effect, or developer-only details (those belong in code and commit messages).
- If a doc update is needed but out of scope for the current task, add an item to `TODO.md` instead of skipping it silently.
- Every error code has a doc entry. CI checks completeness.
- The site publishes `/llms.txt` and `/llms-full.txt` for LLMs.
- API and SDK references are generated from OpenAPI and TSDoc. Never hand-write them.
- An API change updates daemon, generated client, SDK and desktop in the same commit.
- The TypeScript API client is generated from the OpenAPI spec. Never edit generated files.

## Code style

- Comments in English only. Short, factual, only where the logic is not obvious.
- No em dashes, no ASCII banners, no decorative or divider comments.
- No commented-out code. Git has the history.
- Prefer clear names over comments.
- Warnings are errors in C# and TypeScript.
- No abstraction without a second real implementation or an enforced boundary. `ICommandRunner` and `IStorageBackend` are the known exceptions.
- No repository layer over Dapper, no mediator, no interface for a class with one implementation.

## Network

- HTTPS on 443, port 80 redirects to it.
- Fixed alternative port 7484 (P-I-T-H on a phone keypad; IANA leaves 7479 to 7490 unassigned). Never change it.
- HTTPS with a self-signed certificate generated at install by default. A settings wizard makes it trusted: download the box's local CA with per-OS instructions, or use the owner's own domain with a Let's Encrypt wildcard certificate via DNS-01.
- Everything must work on the untrusted default. Passkeys and the service worker are enabled only once the certificate is trusted.
- The UI never assumes a secure context: it must work fully over plain HTTP by IP address and with the untrusted self-signed certificate. APIs that need one (`crypto.randomUUID`, `crypto.subtle`, the Clipboard API, service workers, WebAuthn, notifications) are reached only through `desktop/src/platform/browser.ts`, which falls back or reports that they are missing; ESLint refuses them elsewhere. Features that cannot work without one (passkeys, the service worker) are optional and switch on only after the trust step.
- No subdomains are required for the core system. Existing web apps installed as containers keep their own ports and open in desktop windows.
- IPv6 is supported everywhere IPv4 is.

## Errors

- Every error has a stable code: `area.object.reason`, lowercase, dots for hierarchy, underscores inside a word. Example: `fs.file.not_found`.
- Error codes are defined once as C# constants. The TypeScript union is generated from them. The only exception are the SDK's transport codes (`sdkErrorCodes` in `sdk/src/errors.ts`), which never cross the API; they have texts under `errors.sdk.*`, checked by a test.
- Responses use RFC 9457 problem details with `code`, `params`, `detail` (English fallback) and `requestId`.
- The server never returns translated text through the API. Exception: out-of-band channels (email, push, SSH MOTD) are rendered server-side from the same i18n files in the recipient's language.
- A new error code without an `en` translation fails CI.

## Internationalization

- No user-visible string literals in code. Every visible text is an i18n key.
- Keys: `area.screen.element`, errors under `errors.<code>`.
- English is the reference language. German must exist from day one.
- Plurals and parameters use ICU syntax.
- German texts use informal "du", consistently.
- Texts never name companies or their products (no other NAS vendors); compare with neutral terms such as RAID 5. Open-source projects (Jellyfin, Immich) may be named.

## Tracing and logging

- Every HTTP request and WebSocket message carries a request id (W3C traceparent, `Activity` in .NET).
- The request id propagates to jobs, every `ICommandRunner` call and proxied app backends.
- Every external command logs: command, arguments, exit code, duration, truncated stdout and stderr.
- Structured JSON logs only (Serilog). No string-concatenated log messages.
- Secrets are always masked: passwords, tokens, cookies, credentials. User data in logs is acceptable.
- Support bundles contain logs, config and system info with secrets removed. Never include memory dumps by default; a full dump requires an explicit opt-in with a warning.
- The UI never shows the request id: error dialogs, toasts and error pages show the translated message only. The request id goes to the browser console and the logs, where support finds it (Log Center, support bundle).

## Users and permissions

- Permissions are granted to groups and to users. Effective permissions are the union of all grants. Administrators have everything.
- Share access per share: none, read, read-write.
- Protocol access per user: SMB, NFS, SFTP, WebDAV (more later).
- App access per app: allowed or not.
- Special rights: administrator, desktop login, SSH login, reboot and shutdown.
- 2FA (TOTP) is chosen by each user. Login forms use correct `autocomplete` attributes so password managers (Bitwarden and others) autofill reliably.
- No password reset without proof. Recovery works only with the recovery key created during setup; without it the admin must reinstall.

## Security

- Secure Boot is not supported. The installer detects it and asks the user to disable it.
- SSH is off by default and enabled in settings. Its MOTD shows the logo as ASCII art and a short warning that the system is fully open and can be broken. This is the only place ASCII art is allowed.
- Pool encryption uses ZFS native encryption. Keys are unlocked manually, from a key vault on the system disk for automatic unlock after power loss, or from a USB key. The UI explains that the key vault only protects against stolen disks, not a stolen box.

## Apps

- Every third-party SDK app runs in an iframe with `sandbox="allow-scripts allow-forms"` and without `allow-same-origin`, served from the same host. The opaque origin means it never sees desktop cookies, storage or DOM.
- Apps store data through the SDK (backed by the daemon), not through `localStorage` or IndexedDB.
- App backends are reached through the daemon proxy with a short-lived token issued by the SDK.
- SDK app backends never publish host ports. They sit on an internal Docker network and are reachable only through YARP at `/apps/<id>/api/`.
- YARP validates the app token, removes the desktop session cookie from every proxied request, and adds `X-Pithyx-User`, `X-Pithyx-Groups` and the request id. Cookies are scoped to the host, not the port, so stripping the cookie is mandatory for every proxied app, including external web apps on their own ports.
- One backend instance per app, shared by all users. The backend separates users through `X-Pithyx-User`. No per-user containers.
- Backend lifecycle is declared in the manifest: `always` or `onDemand`. `onDemand` backends run while at least one window of the app is open for any user, plus an idle timeout (default 10 minutes).
- Open windows are counted through the WebSocket. A lost connection keeps the count for a grace period (2 minutes) so a reload does not stop the backend.
- Backends that do work without an open window (conversions, sync, schedules) hold a keep-alive lease through the daemon API or declare schedules in the manifest. Without a lease or schedule, idle `onDemand` backends are stopped with SIGTERM and a timeout.
- Window instances are declared in the manifest: `single` focuses the existing window of this user on this device and passes launch arguments to it; `multi` opens a new window.
- Apps with mounts on a locked or missing pool do not start; the UI shows why.
- App tokens live 5 minutes. Revoking a permission takes effect on the next refresh and is pushed over the WebSocket to close affected windows.
- External web apps (Immich, Jellyfin) run behind a YARP listener on their port, which may rewrite `X-Frame-Options` and `frame-ancestors` so they open in desktop windows. Linked apps on other hosts open in a window if they allow framing, otherwise in a new tab.
- Desktop and app talk through one MessageChannel per app instance. App identity comes from the port, never from message data.
- App backends run as non-root containers with userns-remap. No `docker.sock`, `privileged` or host network unless the manifest requests it and the install dialog shows it.
- Apps open in windows. Redirecting to another page is not allowed. "Open in tab" is optional.
- Every app, core or third-party, is built on `@pithyx/sdk`. Core apps use an in-process transport, third-party apps a MessageChannel transport. The API is identical.

## Dates and times

- Store and transmit UTC (ISO 8601). Each user has a time zone and locale; format with `Intl` in the UI.

## Fonts and third-party assets

- Fonts: IBM Plex Sans (UI), Sora (word mark, large headings), JetBrains Mono (terminal, logs, paths). All SIL OFL 1.1.
- Ship official font files unmodified. IBM Plex reserves the name "Plex": a subset or converted file would need a new name.
- Every bundled third-party asset keeps its license file and appears in the third-party notices shown on the About page.

## Operations

- The daemon runs under systemd with `Restart=on-failure` and a watchdog. It must send `sd_notify` heartbeats.
- Detect capabilities, never assume them. In VMs or on hardware without sensors, hide or explain missing data (temperatures, SMART) instead of showing errors.
- Recovery never modifies pools automatically.
- Pithyx itself never wakes sleeping disks: SMART queries use `smartctl -n standby`, logs and system state live on the system disk, listings are served from ARC or a special vdev. Disk spindown is an option per pool, off by default.
- Power profiles (performance, balanced, efficient) set the CPU governor/EPP and optional link power management (SATA ALPM, PCIe ASPM, USB autosuspend). Options that are known to cause instability on some hardware stay opt-in.
- Host network changes apply with a 60 second confirmation timer and roll back automatically if not confirmed.

## Stable contracts (SemVer)

Breaking any of these requires a major version bump:
REST/WS API, SDK API, app manifest schema, extension points, error codes.

Everything else is internal and may change at any time.

## Tests

Test parsers, decision logic, permission checks, manifest validation and the update state machine.
Integration tests that need root, real zfs (and later mdadm), loop devices, scsi_debug disks or Linux accounts (`Category=StorageIntegration`) never run in GitHub CI. They run on the dev VM with `tools/dev-vm/integration.sh` (sparse files in `/dev/shm`, throwaway pools), and a PR that changes storage, account or command runner code attaches the result. Before a storage PR is merged, its destructive suite runs on the dev VM against the declared test disks.
Do not test getters, DTOs, pass-through controllers or individual React components.
Daemon tests run in simulation mode by default (`DaemonFactory`), with a fake clock where time matters. End-to-end tests live in `e2e/` (Playwright, run with `pnpm build && pnpm e2e`) and start their own daemon with a fresh database. The E2E suite and the performance gate run on the CI runner, not on dev01, and only when asked (see CI).
Disk pull tests (a scsi_debug disk pulled from a live pool) can wedge ZFS until a reboot. They run only with `integration.sh --disk-pulls` on a freshly booted dev01.

## CI

- Every job in `.github/workflows/ci.yml` runs on `runs-on: ${{ vars.CI_RUNNER || 'ubuntu-latest' }}`: the owner's self-hosted runner (label `pithyx-ci`) while the repository variable is set, GitHub-hosted runners once it is unset (for the public repository).
- The self-hosted runner has no Docker and no root. No job uses `services:` or `container:`, and nothing that needs ZFS, real disks or root runs there (see Tests).
- Workflows set `permissions: contents: read` at the top and widen it per job only where needed. No `pull_request_target`, no secrets in logs. A self-hosted runner never executes code from forks without approval.
- The self-hosted runner (`dedi-01`, label `pithyx-ci`) is the CI; a merge into `dev` needs its green run. The owner maintains the runner: never change it, and never add workarounds to the workflows for something missing on it.
- The runner does everything that only needs building and can run in the background while the agent goes on: build, lint, typecheck, unit tests, E2E and the performance gate. Push the branch, keep working, read the result. dev01 is too weak for that.
- Every pull request runs `changes`, `repo` and `dco`, plus `daemon` and `frontend` when their files changed. That green run is enough to merge a small change into `dev`.
- The E2E suite with the performance gate and the design checks takes minutes and runs only when asked (D278): add the label `e2e` to the pull request (`gh pr edit <n> --add-label e2e`), start the workflow by hand (`gh workflow run CI --ref <branch>`), or open a pull request to `master` (always). Add the label before merging a larger change: new or changed user flows, the desktop shell, window manager, sign-in, setup, the SDK transport, the API contract, anything on the loading path or the bundle, or several PRs merged in a row. Run it by hand on `dev` as the final check before the owner verifies a round on 7484.
- What needs ZFS, root or real disks is tested on dev01 (`tools/dev-vm/integration.sh`, which builds the daemon for it), as is the deploy of `dev`. There is no local CI fallback; when the runner cannot run a job, tell the owner.

## Before you commit

- Build, lint and tests pass on the runner.
- Generated clients are up to date.
- New visible text has i18n keys in `en` and `de`.
- New error codes have translations.
- `TODO.md` is checked off and `CHANGELOG.md` is updated.
- Docs in `website/` were checked and updated, or the change was judged not doc-worthy.
- No em dashes and no non-English text outside translation files.
