# Pithyx

Free, self-hosted NAS and server OS with a web desktop. Debian 13 base, C# daemon, React desktop, apps as containers. License: AGPL-3.0, SDK under MIT. Hosted on GitHub (org `pithyx`).

These rules are binding. If a rule blocks you, stop and ask. Do not work around it.

## Principles

- 100 percent local. No telemetry, no phone-home, no cloud accounts, no project-run services the box depends on.
- Crash reports stay on the box. The user can create a support bundle and share it manually.
- Fresh installs only. There is no import from Synology, XPEnology or other systems.

## Environments

Know where you are before you act. Every machine declares its environment in `/etc/pithyx/environment` (`dev` or `prod`); the owner may also state it ("this is dev", "this is prod"). If neither says anything, treat it as prod.

- **dev** (dev VM, simulation mode, CI): you may create and destroy pools on the VM's test disks, reset the database and restart services freely. Hard limit: never touch disks that are not the VM's declared test disks.
- On the dev VM, test pools and disks are handled only through `tools/dev-vm/testpool.sh` (`create`, `destroy`, `wipe`), which refuses any disk whose serial is not a test disk (`VYXTEST01` to `VYXTEST06`, listed in `/etc/pithyx/test-disks`). Serials keep their historical VYXTEST prefix (virtual hardware in Proxmox). `.claude/settings.json` denies raw `zpool create`/`destroy`, `wipefs`, `mkfs` and `dd` to devices. `zpool destroy` leaves labels and partitions behind; run `testpool.sh wipe` afterwards.
- **prod** (anything with real data, including the owner's own NAS): read-only by default. Before any write, show the exact command and wait for explicit confirmation. Take a snapshot or backup first. Never run `zpool destroy`, `zpool create`, `mkfs`, `wipefs`, `dd`, `rm -rf` on data paths, `docker volume rm` or database migrations without a confirmed backup.
- The product follows the same idea: destructive storage actions in the daemon refuse to touch disks that contain partitions or data unless the user confirms with the disk serial.

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

- No developer docs in markdown. The code is the developer documentation. Allowed markdown outside `website/`: README.md, CLAUDE.md (root and per top-level folder), CONTRIBUTING.md, TODO.md, CHANGELOG.md, SECURITY.md.
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
- Error dialogs show the request id with a copy button.

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
Daemon tests run in simulation mode by default (`DaemonFactory`), with a fake clock where time matters. End-to-end tests live in `e2e/` (Playwright, run with `pnpm build && pnpm e2e`) and start their own daemon with a fresh database.

## CI

- Every job in `.github/workflows/ci.yml` runs on `runs-on: ${{ vars.CI_RUNNER || 'ubuntu-latest' }}`: the owner's self-hosted runner (label `pithyx-ci`) while the repository variable is set, GitHub-hosted runners once it is unset (for the public repository).
- The self-hosted runner has no Docker and no root. No job uses `services:` or `container:`, and nothing that needs ZFS, real disks or root runs there (see Tests).
- Workflows set `permissions: contents: read` at the top and widen it per job only where needed. No `pull_request_target`, no secrets in logs. A self-hosted runner never executes code from forks without approval.
- When Actions does not start (a billing lock, a runner that is down), do not wait: `tools/ci-local.sh` runs the same steps locally. A green local run approves a merge into `dev`; its summary goes into the pull request.

## Before you commit

- Build, lint and tests pass locally.
- Generated clients are up to date.
- New visible text has i18n keys in `en` and `de`.
- New error codes have translations.
- `TODO.md` is checked off and `CHANGELOG.md` is updated.
- Docs in `website/` were checked and updated, or the change was judged not doc-worthy.
- No em dashes and no non-English text outside translation files.
