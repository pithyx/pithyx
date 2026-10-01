# TODO

Step-by-step plan for building the Pithyx foundation. Read `CLAUDE.md` before starting and follow it at every step.

## How to use this file

- Work top to bottom. One step at a time.
- A step is finished only when every item is checked and its "Done when" criteria are met.
- `[DECISION]` means the owner must choose before you continue. Present the options with a short recommendation and stop.
- `[REVIEW]` means the owner must look at the result before you continue. Stop and ask.
- Check items off in the commit that completes them. One step usually equals one or a few commits.
- If a step turns out to be wrong or incomplete, propose a change to this file instead of silently deviating.
- Start every step in plan mode: read the relevant sections of `CLAUDE.md`, write a short plan, then implement.
- Each step and some sub-steps carry an effort level. `high` means extended thinking for design-defining work that is expensive to change later. `medium` is the default. `low` is mechanical work.

## Release 0.1 (minimal)

Set by the owner on 2026-09-29. This section is the roadmap from now on: work through it top to bottom. M4 and everything else below (including the parts of steps 9 to 14 that are not listed here) come after it. The release blockers apply to v0.1 as far as its scope reaches; a blocker about a feature v0.1 does not ship (third-party apps, NFS) is met by not shipping it and moves to the release that does.

### Findings from the owner's test on 2026-09-29

Core bugs first, each in its own PR with a regression test:

- [ ] Disk failure: a hard-removed disk only disappears from the list; there is no DEGRADED, no banner, no notification, and other pools look dead. Expected: DEGRADED at once, a banner and a persistent notification, other pools unaffected; after the disk is back, a resilver and the all-clear. Tested for real on dev01: remove with `echo 1 > /sys/block/<dev>/device/delete`, bring back with a SCSI rescan (`echo '- - -' > /sys/class/scsi_host/host*/scan`).
- [x] Notification center and toasts do not work (no entries, no toasts): find the cause and fix it. (The daemon had no notifications at all: the bell only showed storage alerts and toasts were the desktop's own. Now the daemon keeps a history per user (jobs that end, storage alerts that appear or go away, SDK `notify`), sends it on the per-user topic `notifications` and the user's running jobs on `jobs`; the panel shows running jobs, alerts and the history, and every new notification is a toast.)
- [x] After the disks were removed and brought back, Files shows shares without files and "share not found": the mount state and the Files cache recover once the disks are back. A share on an unreachable dataset is `fs.share.unavailable` and marked in the list; Files reads it again when it comes back (the disks themselves come back with pithyx/pithyx#35).
- [x] Deleting a shared folder fails with a timeout (`internal.admin.shares.plan`). Cause: every `zfs` call waited behind a `zpool destroy` stuck in the kernel (see the disk failure item); plans now wait at most 10 seconds and answer `storage.zfs.unavailable`.
- [ ] Destroying a pool: the dialog hangs for a long time at `zpool destroy`. Long operations run as background jobs, the dialog can be closed, progress shows in the notification center. Find the cause of the long run (open handles, watches, ...).
- [x] Usage of pools, shared folders and disks does not update after uploads. Writes through the file service now trigger a pool read (D209); writes over SMB and SFTP need their own trigger when step 12 lands (see below).
- [x] A second right-click on the desktop while its menu is open shows the browser's menu instead of the Pithyx menu at the new position.

Quick fixes, one PR:

- [x] The request id is no longer shown anywhere in the UI, only in the browser console and the logs; `CLAUDE.md` says so.
- [x] Upload and download indicator in the taskbar: progress bar, size, speed, time left, cancel.
- [x] Desktop wallpaper with the new box proportions (lid height 16 as in the logo).
- [x] Larger logo in the menu button at the bottom left.
- [x] Pithyx menu: the tab "Pinned" is called "Apps"; pinning is removed completely.
- [x] Sign-in language picker: a styled list of our own instead of the native select (light and dark, keyboard, axe).
- [x] Remove "Open Storage" from Settings.

Settings and user menu:

- [ ] Settings holds system settings only, as a tile overview with categories and search (no company names): Info Center (OS version, hardware, uptime, system data), Shared folders, Users, File services (SMB, SFTP; a page saying "coming in v0.1" until they are done), Network (DHCP or static; shown now, changes with the rollback timer of step 14), Regional settings (system language, formats, date and time, time zone, time servers). Only finished or clearly marked pages.
- [ ] Personal settings live in the user menu, not in Settings: theme (system, light, dark), accent color, language, taskbar position, wallpaper. The personalization page moves there.
- [ ] Storage app: the internal datasets (`pithyx`, `apps`, `system`, `homes`, `shares`) are never shown. The tab "Datasets and volumes" leaves the default view; volumes and export and import move under "Advanced" with a short explanation ("Export a pool to move its disks to another computer").
- [ ] Snapshots become "restore points", explained simply: one switch per shared folder, "Daily restore points, kept 7 days", plus "Previous versions" in Files. The dialog explains with examples: restore points only take space for files that were changed or deleted after them; a deleted file frees its space only when the last restore point that holds it expires; they help against accidental deletes and ransomware, and they are no replacement for a backup on another device. Schedules, holds, rollback and manual management leave the UI; the code stays. New shared folders get restore points switched on by default (daily, kept 7 days; owner's decision on 2026-09-30).

Log Center (new core app, own PR):

- [x] Shows the logs of the system (the daemon and the relevant services) and of apps, filtered by source, level (debug, info, warn, error) and time range, with search and export.
- [x] Storage (changed by the owner on 2026-09-30): the current logs of the system and of apps live on the system partition, capped together at 4 GB (oldest dropped first), so they are always available: without a pool, with a locked, exported or failed pool, and during boot. An optional archive with longer retention lives on a pool, chosen in the Log Center (default: the first pool). Both sources are shown together. Lines are written in batches, not one write per line.
- [x] Retention (duration and maximum size) can be set for the current logs and the archive.
- [x] SDK: a logging API for apps (debug, info, warn, error) as part of the public SDK (TSDoc, its own decision number), rate limited per app so no app can flood the logs.
- [x] Administrators only, its own chunk, en and de, light and dark, axe.

### Additions on 2026-09-30

Password rules (one PR; the same rules everywhere: setup, user management, changing the password, later the installer):

- [x] The hard minimum of 12 characters goes. New hard floor: 8 characters (NIST SP 800-63B). The blocklist of common passwords stays hard, with a friendly message ("This password is too well known").
- [x] A live strength meter (weak, medium, strong) with a short reason (too short, a well-known password, only dictionary words, repetitions). Weak passwords from 8 characters on are allowed with a warning only. No character class rules. The sentence about long passphrases becomes an optional tip.
- [x] The estimate needs no large library (sign-in budget); the server runs the same blocklist check. One central validator that every place uses: `CredentialRules.ValidateNewPassword` in the daemon, `auth/password` (`NewPasswordField`, `assessPassword`) in the desktop; the Users page and the password change use them when they are built.

Thumbnail cache (one PR, replaces D197 in pithyx/pithyx#33):

- [x] Thumbnails live in a dataset of their own per pool (`<pool>/pithyx/cache`, never shown in the UI) instead of `/var/cache/pithyx/thumbnails`, capped at 2 GB per pool (least recently used dropped first). The cache leaves with its pool when the pool is destroyed or exported. Without a pool there are no files that need thumbnails. `daemon/CLAUDE.md` says so.
      Follow-ups found on the way:

- [ ] Used space after writes over SMB and SFTP (step 12): they bypass the file service, so they need their own trigger for a pool read (for example Samba's `full_audit` or a slow timer while a share is connected), without waking sleeping disks.
- [ ] Users (v0.1): deleting a user must also clear the daemon's in-memory notifications and jobs of that user, and user ids must never be reused (`users.id` has no `AUTOINCREMENT`), or a new user could see a deleted user's notifications until a restart (review of pithyx/pithyx#36).
- [ ] Storage alert notifications: a pool whose fill level hovers around 80 or 90 percent notifies on every crossing; add a hysteresis, and do not call an exported or destroyed pool's alert "resolved".

Owner feedback on 2026-09-30 (the rest is in #56):

- [ ] SDK: a context menu API for apps. The desktop draws the menu (same look, keyboard and touch handling as its own menus) from items the app passes with the point of the right-click, and returns the chosen item; an app can also ask for the browser's menu in an area. Until then the SDK and the window frame only suppress the browser's menu outside text fields. Needs its own decision number and TSDoc.

### Scope of v0.1

- [ ] Web installer (from M5): boot from USB, open the setup page in a browser, choose the system disk, the system installs itself and continues setup in the same page. Details under "Web installer v0.1" and "System layout v0.1" below.
- [ ] Storage: create, extend, repair and destroy pools; failures are detected and reported reliably (step 8 plus the findings above).
- [ ] Shared folders.
- [ ] Users: create, edit, delete (groups later), as Linux and Samba accounts (the user part of step 10 and the share rights of step 11).
- [ ] SMB and SFTP (step 12 without NFS; no FTP).
- [ ] Core apps: Files, Storage, Settings, Resource Monitor, Viewer, Log Center.
- [ ] Updater with rollback (from M5).
- [ ] Configuration backup and restore (`pithyx.db`, Samba passdb, TLS certificates, SSH host keys; see `daemon/CLAUDE.md`).
- [x] Encryption comes after v0.2 (owner's decision on 2026-09-30; step 9 moves there). ZFS encrypts a dataset only when it is created: an existing pool cannot be encrypted in place, its data would have to be copied into a new encrypted dataset. Pools created with v0.1 and v0.2 therefore stay unencrypted, even after an update that brings encryption; only pools created later can be encrypted. The pool wizard keeps saying that encryption can only be chosen at creation, and the release notes of v0.1 say that its pools stay unencrypted.

### Web installer v0.1

Only planned here; it is built after the items above.

- [ ] Boot from a USB stick or ISO (UEFI). The console shows only the logo, the address or addresses and a QR code.
- [ ] The installer UI listens on port 7484; port 80 redirects to 7484. It announces itself over mDNS as `pithyx-setup.local`.
- [ ] Same desktop wallpaper with the boxes, a welcome message in the spirit of "Let's begin the free journey", the logo with a welcome animation.
- [ ] Steps with back navigation:
  1. Name of the NAS, language, time zone, region and formats, time servers.
  2. Network: the detected network cards with their link state, DHCP by default, static optional, a connection test.
  3. Storage: choose the system disk (SSD or NVMe suggested, USB disks excluded), optionally create a data pool right away with the pool wizard.
  4. Administrator with the password rules above, root locked, reserved names from the central list, show the recovery key and confirm it was stored.
  5. Summary listing every disk that will be erased (model, size, serial) and a checkbox "All data on these disks will be erased".
- [ ] Installation with progress, then "Remove the USB stick", reboot, and a redirect to the new address.
- [ ] Test: a throwaway QEMU VM with several disks and network cards, driven by Playwright, including the reboot.

### System layout v0.1

- [ ] Single-disk installation that is always future-compatible: ESP 512 MB (boot loader plus the fallback `\EFI\BOOT\BOOTX64.EFI`), system partition 32 GB as an mdadm RAID1 with one member (btrfs, snapshots for update rollback), swap 4 GB as an mdadm RAID1 with one member, the rest of the disk unused for now. The one-member RAID1 lets v0.2+ add a second disk without reinstalling.
- [ ] Never on the system partition: Docker images, VM disks, app data, thumbnails.
- [ ] The system disk stays locked for pools (the disk guard of step 7).
- [ ] `[DECISION]` v0.2+: multi-disk system installation. Only "single disk" or "mirror", never a stripe. Suggest at most 2 system disks (SSDs preferred), a hint from 4 on, a hard limit of 8, never all disks automatically. EFI partitions kept in sync, booting with a missing disk, automatic re-add when a disk is replaced, the remaining space usable as a pool or volume (a stripe there only with a warning), and a hint about speed and power saving when the system disks are HDDs. Open: mdadm plus btrfs, or root on ZFS with boot environments. Optional zram.

### After the scope

- [ ] Audit: security, licenses, secrets.
- [ ] Code and UI review by the owner. `[REVIEW]`
- [ ] Cleanup.
- [ ] Release v0.1.0 (tag on `master`, codename chosen by the owner). `[DECISION]`
- [ ] Make the repository public (branch protection, GitHub-hosted runners; see "Release blockers").

### Not in v0.1

App store and M4, NFS, SSD cache (step 13), energy (step 14.1), resource history, the Files rework (layout of shares and the recycle bin), video thumbnails.

---

## Milestones

| Milestone               | Content                                             | Steps          |
| ----------------------- | --------------------------------------------------- | -------------- |
| M0 Foundation           | Repository, tooling, CI, dev environment            | 1, 2           |
| M1 Walking skeleton     | Daemon core, login, WebSocket, first window         | 3              |
| M2 Desktop              | Design, window manager, SDK, file manager           | 4, 5, 6        |
| M3 NAS core             | Pools, snapshots, users, shares, SSD cache          | 7 to 14        |
| M4 Apps                 | Manifest, containers, YARP routes, app template     | to be detailed |
| M5 Image and operations | ISO, installer, updates with rollback, self-healing | to be detailed |
| M5b Self-hosting        | Docker UI, VM MVP                                   | to be detailed |
| M6 Backup               | restic, ZFS replication                             | to be detailed |

## Release blockers

Every shortcut taken on purpose during development is listed here. The first release (and making the repository public) waits until every item is done. A new shortcut adds its item here in the same commit that takes it.

The first release, v1.0.0, carries the constellation codename Pyxis: the mariner's compass, and the box Erasmus put into Pandora's hands in place of the pithos.

- [x] File ownership (D89 in pithyx/pithyx#18, resolved in step 10; `FileOwners` with `fchown`/`fchownat` since the users PR): files and folders the daemon creates (uploads, copies, restored items, new folders) belong to root until M3 provisions Linux users. They must belong to the requesting user's Linux account (`fchownat`), so SMB, NFS and SFTP see the right owner.
- [ ] Trust step and TLS: development runs over plain HTTP on 7484. Ship HTTPS on 443 (80 redirects, 7484 as the fixed alternative) with the self-signed certificate from the install, the trust wizard and the "Trust this device" setup step (see the M5 items below); passkeys and the service worker switch on only once trusted.
- [ ] Branch protection: only the lefthook `pre-push` hook guards `master` and `dev` today (rulesets need a public repository or a paid plan). Run `tools/github/protect-branches.sh` (PR required, CI green, no force push) before or right when the repository goes public.
- [ ] CI runners: a self-hosted runner, if one is set up for the private repository, is replaced by GitHub-hosted runners before the repository goes public (`vars.CI_RUNNER` back to the default); integration tests on real ZFS then need an approved environment, and self-hosted runners never run code from forks.
- [ ] App permissions on the daemon: until the app registry (M4), the desktop broker checks SDK permissions only in the browser. No third-party app is installable before the daemon checks every permission itself (guardrail 1), including the install confirmation for elevated rights and the `host.full` warning (D71). The same holds for app logs (D261): until app tokens exist, the daemon takes the app id of `POST /api/apps/{app}/logs` from the desktop.
- [ ] The SDK demo app (`apps/demo`) is registered as a built-in app for the prototype; remove it from the app list (or make it an installable example) before the release.
- [ ] Recycle bin over SMB and NFS: hide `.pithyx-trash` (`veto files`) and route Samba's `vfs_recycle` into the same bin (step 12).
- [ ] Error code docs: every error code needs its entry on the website with a CI completeness check (`CLAUDE.md`, Errors); today codes are documented only through their i18n texts.
- [ ] Third-party notices: generate the list of bundled npm and NuGet packages with their licenses for the About page and check it in CI (Later milestones item); today only fonts, Tabler and React Aria are listed.
- [ ] `TopicHub` keeps one throttle per published topic forever (one per job and folder); release them for topics without subscribers.
- [ ] No real data on Pithyx pools before step 9 (encryption and key vault) is done: encryption can only be chosen when a pool is created, so a pool made earlier would have to be recreated. Encryption comes after v0.2 (decided on 2026-09-30), so v0.1 and v0.2 ship without it: their release notes and the pool wizard say that pools created with them stay unencrypted. The blocker holds for the release that brings step 9.
- [ ] UPS: check before the release whether NUT support (clean shutdown on power loss, pools exported and the key vault untouched) must ship with it (parked under "Later milestones").

---

## Step 1: Repository foundation

Effort: medium.

Goal: an empty but complete monorepo where every tool is configured, every check runs, and CI is green.

### 1.1 Decisions (made 2026-09-26)

- [x] Git host: GitHub, org `pithyx`, CI with GitHub Actions.
- [x] SDK license: MIT. Everything else is AGPL-3.0.
- [x] Contributor terms: DCO sign-off (`Signed-off-by` on every commit, checked in CI).
- [x] Frontend lint and format: ESLint + Prettier.

### 1.2 Git and repository basics

- [x] `git init` with default branch `master`, then create `dev` from it. (The folder was already initialized with `master` and the `origin` remote.)
- [x] Branch model: `master` is stable and only receives release PRs from `dev` (and hotfixes). `dev` is the integration branch for beta builds. Work happens in `feature/<name>`, `fix/<name>` and `hotfix/<name>`. PRs target `dev`. Hotfixes branch from `master` and merge back into `master` and `dev`.
- [ ] GitHub branch protection for `master` and `dev`: PR required, CI green, no force push. Blocked: rulesets are not available for private repositories on the Free org plan. `tools/github/protect-branches.sh` creates the ruleset; run it once the repository is public or the org is upgraded. Until then the lefthook `pre-push` hook refuses direct pushes to `master` and `dev` (only the first push that creates the branch is allowed).
- [x] `.gitignore` covering .NET (`bin/`, `obj/`), Node (`node_modules/`, `dist/`), IDEs (`.idea/`, `.vs/`, `.vscode/` except shared settings), OS files, local env files.
- [x] `.gitattributes`: `* text=auto eol=lf`, binary types marked as binary.
- [x] `.editorconfig`: UTF-8, LF, final newline, trim trailing whitespace, 4 spaces for C#, 2 spaces for TS/JSON/YAML/Markdown, C# naming and style rules.
- [x] `CLAUDE.md` in the root (already written). Once `daemon/` and `desktop/` exist, move the daemon-specific and frontend-specific sections into `daemon/CLAUDE.md` and `desktop/CLAUDE.md` so the root file keeps only project-wide rules. Claude Code loads the nested files when working in those folders.
- [x] `TODO.md` (this file) in the root.
- [ ] Logo: put the provided `logo.svg` into `desktop/public/` and `website/public/` (create the folders when those projects exist). It is the source for favicon and app icons. Done for `desktop/public/`; `website/public/` follows when `website/` exists.
- [x] `.claude/settings.json` with permission deny rules for destructive commands (`rm -rf /`, `dd of=/dev/*`, `mkfs*`, `wipefs`, `zpool destroy`, `zpool create`, `git push --force` to `master` or `dev`). Force pushes are denied for every branch, and every rule has a `sudo` variant. `zpool create` and `zpool destroy` are allowed only through `tools/dev-vm/testpool.sh`.
- [x] `tools/dev-vm/testpool.sh`: creates and destroys test pools (`pithyxtest*`, `autotrim=on`) and refuses any disk whose serial is not `VYXTEST01` to `VYXTEST06` (or not listed in `/etc/pithyx/test-disks` once that file exists).
- [x] Commit message check (commitlint or a small script): gitmoji plus Conventional Commits as defined in `CLAUDE.md`, run in lefthook and CI.

### 1.3 Legal and project files

- [x] `LICENSE`: full AGPL-3.0 text.
- [x] `sdk/LICENSE`: MIT.
- [x] Attribution notice per AGPL section 7(b) prepared as `NOTICE` (author credit that must be kept on the About page). Mark with `[REVIEW]`, it needs a legal check before the first public release.
- [ ] `[REVIEW]` Legal check of `NOTICE` before the first public release.
- [x] `README.md`: name, pronunciation, one-paragraph description, project status ("pre-alpha, not usable"), license, sections "Development" and "Contributing" as placeholders.
- [x] `CONTRIBUTING.md`: Conventional Commits, branch naming, PR checklist (mirrors "Before you commit" in CLAUDE.md), DCO sign-off with `git commit -s`.
- [x] `CHANGELOG.md`: Keep a Changelog format, empty `Unreleased` section.
- [x] `SECURITY.md`: how to report vulnerabilities privately, supported versions (none yet).

### 1.4 Daemon scaffold (.NET)

- [x] `global.json` pinning the .NET 10 SDK.
- [x] Solution file in `daemon/` (use `.slnx` if the pinned SDK supports it, otherwise `.sln`).
- [x] Projects:
  - `daemon/src/Pithyx.Daemon` (ASP.NET Core, the host and all modules as folders)
  - `daemon/tests/Pithyx.Daemon.Tests` (xUnit v3)
- [x] `Directory.Build.props`: `Nullable` enabled, `ImplicitUsings` enabled, `TreatWarningsAsErrors` true, `AnalysisLevel` latest-recommended, `EnforceCodeStyleInBuild` true, deterministic builds.
- [x] `Directory.Packages.props`: central package management.
- [x] Minimal `Program.cs` that starts Kestrel on port 7484 and serves `GET /api/health` returning `{ "status": "ok" }`.
- [x] One test that calls `/api/health` through `WebApplicationFactory`.
- [x] `dotnet format --verify-no-changes` passes.

### 1.5 Frontend scaffold (pnpm workspace)

- [x] Root `package.json` with pnpm workspaces: `desktop`, `sdk`, `packages/*`. Pin the pnpm version via `packageManager`.
- [x] `.nvmrc` or `engines` pinning the Node LTS version.
- [x] `tsconfig.base.json`: `strict`, `noUncheckedIndexedAccess`, `exactOptionalPropertyTypes`, `verbatimModuleSyntax`.
- [x] `desktop/`: Vite + React + TypeScript. Replace the template content with a single empty desktop surface. Enable the React Compiler.
- [x] `sdk/`: TypeScript library built with Vite library mode, exports an empty `pithyx` object for now, emits type declarations.
- [x] `packages/api-client/`: placeholder for the generated OpenAPI client.
- [x] ESLint flat config: typescript-eslint strict, react-hooks, React Compiler rules (part of `eslint-plugin-react-hooks` 6 and later; the separate `eslint-plugin-react-compiler` is deprecated), i18next plugin with "no literal strings in JSX".
- [x] Prettier config.
- [x] Vitest configured in `desktop` and `sdk`, one trivial test each.
- [x] Root scripts: `pnpm lint`, `pnpm typecheck`, `pnpm test`, `pnpm build`, `pnpm format:check`.

### 1.6 Repository checks

- [x] `tools/checks/` with a script that fails on:
  - em dashes (U+2014) anywhere in source files, config files and markdown
  - non-ASCII characters in code comments outside `i18n/`
  - generated files that are out of date (wired up later, stub for now)
- [x] Root script `pnpm check:repo` runs it.

### 1.7 Git hooks and CI

- [x] Pre-commit hooks with lefthook: format check, lint on staged files, repo checks. Keep them fast (under 5 s).
- [x] CI workflow `ci.yml`, triggered on PRs and pushes to `dev` and `master`, with jobs:
  - daemon: restore, build, test, `dotnet format --verify-no-changes`
  - frontend: install (frozen lockfile), lint, typecheck, test, build
  - repo: `pnpm check:repo`
  - dco: every commit in a PR carries `Signed-off-by`
- [x] Cache NuGet and pnpm stores in CI.
- [x] Commit: `🎉 chore: initialize repository` (one-time bootstrap commit directly on `master`).

**Done when:** a fresh clone runs `dotnet test`, `pnpm install`, `pnpm lint`, `pnpm test`, `pnpm build` and `pnpm check:repo` without errors, and CI is green on `dev` and `master`.

---

## Step 2: Development environment

Effort: medium.

Goal: anyone can go from a fresh clone to a running daemon and desktop in under 15 minutes, following only the README.

### 2.1 Dev VM

- [x] `tools/dev-vm/provision.sh`: idempotent script for a fresh Debian 13 VM. Installs .NET 10 SDK, Node LTS + pnpm, `zfsutils-linux` (contrib), `mdadm`, `btrfs-progs`, `smartmontools`, `samba`, `nfs-kernel-server`, `docker.io`, `qemu-system-x86`, `ovmf`. Enables contrib and non-free-firmware.
- [x] The script reproduces exactly the toolchain installed manually on dev01 for step 1: Microsoft package source for Debian 13 (`packages-microsoft-prod.deb` from `packages.microsoft.com/config/debian/13/`) with `dotnet-sdk-10.0` 10.0.401, NodeSource `node_24.x` with `nodejs` 24.21.0, and `corepack enable` so pnpm comes from `packageManager` (pnpm 12.6.0). Versions must match `global.json`, `.nvmrc` and `package.json`.
- [x] The script verifies it runs on Debian 13 and refuses otherwise.
- [x] The script sets the system locale to `en_US.UTF-8` (keyboard and time zone stay as they are) so tool output and logs match production.
- [x] The script writes `dev` to `/etc/pithyx/environment` and records the test disks by serial in `/etc/pithyx/test-disks`.
- [x] README section "Development / Dev VM": recommended VM spec (4 vCPU, 8 GB RAM, 32 GB system disk, six 10 GB data disks), how to run the provisioning script, recommendation to snapshot the VM before destructive tests.

### 2.2 Simulation mode

- [x] `ICommandRunner` interface with `ProcessCommandRunner` (real) and `FixtureCommandRunner` (reads recorded output from `daemon/tests/fixtures/`). Fixtures are recorded with `tools/fixtures/record.sh`.
- [x] Environment variable `PITHYX_SIMULATE=1` switches the daemon to `FixtureCommandRunner`. In simulation mode the daemon runs on any Linux, including WSL. Native Windows and macOS are not supported.
- [x] The simulated state is visible: `GET /api/health` reports `"mode": "simulated"`.

### 2.3 Dev workflow

- [x] Vite dev server proxies `/api` and `/ws` to the daemon. Target configurable via `PITHYX_DAEMON_URL` (default `http://localhost:7484`).
- [x] Development runs over plain HTTP on 7484. Production HTTPS is added later. Document this clearly so nobody ships it.
- [x] Root script `pnpm dev` starts the desktop; README documents `dotnet watch` for the daemon (locally in simulation mode or on the VM over Remote-SSH).
- [x] README section "Development": prerequisites, both workflows (simulation on the local machine, real on the VM), common problems.

**Done when:** following the README on a clean machine shows the desktop in the browser with "Connected to daemon (simulated)" within 15 minutes.

---

## Step 3: Walking skeleton (daemon core, login, first window)

Status: done (2026-09-26). The Proxmox detach test passed on dev01; only the real-hardware lsblk fixtures below are still open.

Effort: high for 3.1, 3.3, 3.4 and 3.6 (they define contracts everything else builds on), medium for the rest.

Goal: one real feature end to end, built on the final architecture. Everything later extends this.

### 3.1 Daemon host

- [x] Serilog with JSON output to console (journald in production) and a rotating file with size cap.
- [x] Request id middleware: accept or create a W3C `traceparent`, expose it as `requestId`, enrich every log entry.
- [x] Secret masking in logs (passwords, tokens, cookies).
- [x] Error system: `ErrorCodes` static class with constants, `PithyxException(code, params)`, middleware that maps it to RFC 9457 problem details with `code`, `params`, `detail`, `requestId`. Unknown exceptions map to `system.internal.unexpected`.
- [x] OpenAPI document generated by `Microsoft.AspNetCore.OpenApi`, served at `/api/openapi.json`. Scalar UI at `/api/docs` in development only.

### 3.2 Database

- [x] SQLite at a configurable path (default `/var/lib/pithyx/pithyx.db`, in simulation mode a local file). WAL mode, foreign keys on.
- [x] Migration runner: numbered `.sql` files embedded as resources, a `schema_version` table, runs at startup inside a transaction, refuses to start on a failed migration.
- [x] Migration `0001`: `users`, `sessions`.

### 3.3 Authentication

- [x] First-run setup: if no user exists, `POST /api/setup` creates the admin. Afterwards the endpoint returns `auth.setup.already_done`.
- [x] Passwords hashed with Argon2id. Parameters documented next to the code.
- [x] New passwords: 12 to 1024 characters and not in the offline list of common passwords (`daemon/src/Pithyx.Daemon/Auth/common-passwords.txt`, regenerated by `tools/passwords/update-common-passwords.sh`). No composition rules.
- [x] Login creates a session stored in SQLite and sets an HttpOnly, Secure (outside dev), SameSite=Strict cookie. Logout deletes it.
- [x] CSRF protection: SameSite=Strict plus a required custom header on state-changing requests.
- [x] Rate limiting on login. Implemented as a small fixed-window counter on `TimeProvider` instead of the ASP.NET Core rate limiter: only failed attempts count (5 per user and address, 30 per address, 5 minutes), and tests can move the clock.
- [x] Optional TOTP per user: enrollment with QR code, verification as a second step inside the same login card, recovery codes. Daemon side done in step 3 PR 3 (enroll, confirm, disable, login step, recovery codes); the QR code and the login card come with the desktop in 3.7.
- [x] User names are valid Linux names (`^[a-z_][a-z0-9_-]{0,31}$`), stored in lower case, and must not be a reserved system account (`auth.username.invalid`, `auth.username.reserved`). An optional display name may contain any printable characters.
- [x] Login and setup forms use `autocomplete="username"`, `current-password`, `new-password` and `one-time-code` so password managers autofill.
- [x] Recovery key: generated during setup, shown once, offered as a printable page. It is the only way to reset the admin password.
- [x] Passkeys are out of scope until the TLS decision in M5 is made. Do not design the auth flow around them.
- [x] Error codes: `auth.credentials.invalid`, `auth.session.expired`, `auth.rate.limited`, `auth.setup.already_done`, `auth.totp.invalid`, `auth.recovery.invalid`.

### 3.4 WebSocket and topics

- [x] `/ws` endpoint, authenticated by the session cookie.
- [x] Message protocol: `sub`, `unsub`, `snap`, `event`, `error`, each with the request id. JSON schema in code, types generated for TS. Added `ack` for `sub` and `unsub` (so a wildcard subscription without current matches is confirmed); `snap` carries the `id` of its subscription. Close code 4401 means the session ended.
- [x] Topic router with `+` and `#` wildcards, permission check per subscription, throttling per topic. Subscriptions are stored as patterns and matched on every publish, so topics created later reach wildcard subscribers; permissions are checked for every snapshot and every event.
- [x] First topic `system/info`: hostname, uptime, CPU load, memory. 1 Hz.

### 3.5 First real feature: disks

- [x] `LsblkParser`: pure static function, parses `lsblk -J -b -O` output. Fixture tests with real output from the dev VM (virtual disks). `tools/fixtures/record.sh` anonymizes serials and WWNs with stable pseudonyms.
- [ ] Owner: record `lsblk -J -b -O` fixtures from real hardware (plain SATA, NVMe, USB disk) with `tools/fixtures/record.sh lsblk-<case> lsblk -- lsblk -J -b -O` and add parser tests for them.
- [x] `SystemStateService`: holds the disk list in memory. Refreshes on udev events (listen via `udevadm monitor` as a long-running process or netlink) and at startup.
- [x] `GET /api/disks` reads from memory. Topic `storage/disks` pushes changes.

### 3.6 Generated contracts

- [x] TS client generated from `openapi.json` into `packages/api-client` (openapi-typescript + openapi-fetch).
- [x] Error codes exported from C# and generated into a TS union type.
- [x] CI drift check: regenerate and fail if the committed output differs.

### 3.7 Desktop skeleton

- [x] i18next with ICU, `i18n/en/*.json` and `i18n/de/*.json`, language from browser with manual override. Base from step 2: i18next with ICU, `i18n/{en,de}/desktop.json`, browser language detection; the manual override is still open.
- [x] CI check: every key exists in `en` and `de`, every error code has an `errors.<code>` key. Base from step 2: `desktop/src/i18n.test.ts` compares the `en` and `de` keys and parses every ICU message.
- [x] Design tokens as CSS variables with light and dark theme (placeholder values, final design in step 4).
- [x] Setup screen (first run) and login screen, functional but unstyled.
- [x] Minimal desktop with one fixed window that shows the disk list live via the WebSocket store.
- [x] The daemon serves the built desktop: static files plus `index.html` as the SPA fallback for every path outside `/api/` and `/ws` (API paths keep answering with problem details).
- [x] Error dialog: translated message from the error code, request id with a copy button.
- [x] TanStack Query for REST, a small Zustand store for WebSocket topics. Base from step 2: TanStack Query is set up and used for `/api/health`.

### 3.8 Tests

- [x] Parser fixture tests, auth flow tests (setup, login, wrong password, rate limit, logout), WebSocket subscription test.
- [x] One Playwright E2E test: setup admin, log in, see the disk list.
- [x] `provision.sh` installs what headless Chromium needs on the dev VM: `fonts-dejavu-core` (without any font, text renders invisible) and the Playwright browser libraries (`libgbm1`, `libxkbcommon0`, `libatk1.0-0t64`, `libatk-bridge2.0-0t64`, `libatspi2.0-0t64`, `libxcomposite1`, `libxdamage1`, `libxfixes3`, `libxrandr2`). They were installed manually on dev01 during step 2.

**Done when:** on the dev VM you can create the admin, log in, and see the disk list in a window. Detaching a virtual disk in Proxmox updates the list without a reload. A forced error shows a translated message with a request id that can be found in the logs. All checks are green.

---

## Step 4: Design pass (login, desktop, windows)

Status: done (2026-09-27). The design gallery stays as the reference; setup and sign-in use the design against the real daemon.

Effort: high.

Goal: an agreed look and feel before the window manager is built for real.

Direction from the owner:

- Login like a Windows terminal server session: username and password, TOTP as a second step in the same card, then a short welcome transition that only lasts as long as loading takes (no artificial delay).
- A desktop that feels like a real OS: wallpaper, taskbar (default position bottom, configurable), start menu with search, windows with live resizing.
- No system tray. A small status area instead: clock, running jobs, notifications, user menu.
- Personalization: wallpaper, taskbar position, accent color, light or dark.
- Desktop items: files and folders on the desktop (backed by a `Desktop` folder in the user's home share), app shortcuts, sticky notes as a widget.
- Original design. Nothing may resemble DSM icons, layouts or artwork.
- Starting palette (from the logo, refine in this step): primary petrol `#0E4A5A` / `#1A6E82` (dark theme `#5FB3C6` / `#3E93A7`), deep `#062A33`, accent amber `#E0A33A` used sparingly and never as text on light backgrounds, neutrals tinted toward petrol, semantic colors separate from the brand: success green, warning orange (distinct from the amber accent), danger red. The desktop accent color is user-configurable with petrol as default.

- [x] `[DECISION]` Styling approach (decided 2026-09-26): CSS Modules with CSS variables. `desktop/scripts/css-module-types.ts` generates exact class name types (gitignored `.css-types/`), so a typo is a type error.
- [x] `[DECISION]` Icon set (decided 2026-09-26): Tabler (`@tabler/icons-react`).
- [x] Typefaces (decided 2026-09-26): IBM Plex Sans for the UI, Sora for the word mark and large headings, JetBrains Mono for terminal, logs and paths. All SIL OFL 1.1.
- [x] Ship the official font files unmodified (IBM Plex has the Reserved Font Name "Plex", so do not subset or convert them ourselves), self-hosted, preloaded, with each license file next to the fonts and listed in third-party notices. `tools/fonts/fetch-fonts.sh` pins and verifies the sources; Sora upstream publishes WOFF2, so nothing is converted. `index.html` gets the preload links when the design is applied.
- [x] `[DECISION]` Headless component primitives (decided 2026-09-26): React Aria Components.
- [x] Produce static mockups for: setup, login (including the TOTP step and welcome transition), empty desktop, desktop with icons and a sticky note, desktop with three windows, window states (focused, unfocused, maximized, minimized), taskbar, start menu with search, notification, error dialog, mobile layout.
- [x] Derive final design tokens: colors (light and dark, based on the logo palette), type scale, spacing, radii, shadows, motion durations. Tokens live in `desktop/src/theme/`, accents are curated presets, and `contrast.test.ts` checks WCAG AA for every preset in both themes. The mockups are a dev-only gallery (`pnpm design`, `design.html`) that is not part of the production build.
- [x] `[REVIEW]` Owner approves the mockups and tokens. Iterate until approved. Round 2 (2026-09-27): overflow fixes and `pnpm design:test`, label pills, accent swatches, sample apps and notifications through i18n (notifications as code and params), recovery key in two lines, notes under windows, no rose preset, search field height, shorter start menu placeholder, real wallpaper previews, "Forgot password?", login heading, Files density, taskbar entries sized to content. Round 3 (2026-09-27, last round, the owner asked to finish the design pass with it): scene layout so the sticky note no longer sits on the Storage title bar plus a `design:test` check that desktop items never cover a title bar, short Files search placeholder, translated well-known places.
- [x] Apply the approved tokens and screens to setup and login. Done with the real flows against the daemon: setup, recovery key, sign-in, TOTP, recovery code, "Forgot password?" reset and the welcome transition, plus `index.html` font preloads; the placeholder `src/styles/tokens.css` and `desktop.css` are gone.

**Done when:** the owner has signed off on the design and setup and login use it.

---

## Step 5: Window manager and SDK

Status: 5.1, 5.2 and the window manager part of 5.4 in progress in the `feature/window-manager` draft PR (plan and decision log in its comments). 5.3 follows in its own PR after the SDK spec review.

Effort: high for 5.3, medium for the rest.

Goal: the desktop works like a desktop, and apps can be built against the SDK.

### 5.1 Window manager

- [x] Zustand store: windows by id, open, close, focus, z-order, minimize, maximize, restore. The rules are a pure reducer in `desktop/src/wm/state.ts`; the store only wraps it.
- [x] Drag via pointer events: write position to the DOM during the drag, commit to the store on `pointerup`. A transform once per animation frame, committed in `flushSync`.
- [x] While dragging or resizing, `pointer-events: none` on all window contents (iframes would swallow events).
- [x] Resize from all edges and corners, minimum sizes from the manifest. Minimum sizes come from the internal app registry until the manifest exists (5.3).
- [x] Snap to left or right half and maximize by dragging to the top edge.
- [x] Keyboard: focus cycling, close, maximize. Visible focus states. Alt+Shift shortcuts and a window menu with keyboard move and size modes.
- [x] Every window belongs to a `screen` (always 1 for now).
- [x] Size classes from `CLAUDE.md` (compact, medium, expanded): full-screen apps with app switcher on compact, maximized windows and split view on medium, free windows and snap zones (thirds on ultrawide) on expanded.
- [ ] SDK reports the window size class; sample apps use container queries. Waits for the SDK (5.3); the core apps already use container queries.
- [x] Touch: 44 px targets on coarse pointers, swipe to switch apps on compact, no hover-only actions. Plus the back gesture on compact.
- [x] Taskbar with running apps, start menu with search, notification toasts. (Toasts show the desktop's own messages and every new notification of the daemon, see "Release 0.1".)
- [x] Taskbar positions left and right (step 4 designed bottom and top only).

### 5.2 Layout memory

- [x] Migration: `desktop_layouts` per user and device with `expires_at`, persistent `window_preferences` per user and app, and `desktop_items` per user (shared across devices).
- [x] Desktop items: shortcuts and sticky notes on a grid with selection, drag, keyboard, menus, rename and removal, saved over REST with a version per item (migration 0004). Files and folders on the desktop come with the file manager (step 6), backed by the `Desktop` folder in the user's home share.
- [x] Device id stored in a long-lived cookie. "Get desktop from ..." lists the user's other devices and copies that layout.
- [x] Debounced save over the WebSocket, restore on login with validation (skip windows whose app, path or permission is gone). App ids are checked now; paths and permissions join the same check once apps have them.
- [x] Hourly cleanup job for expired layouts.

### 5.3 App runtime and SDK

Spec first, then implementation together with the sample apps. Freeze SDK 1.0 only after the file manager (step 6) runs on it.

Guardrails (set by the owner on 2026-09-27, binding like `CLAUDE.md`; the spec and every later SDK change follow them):

1. Apps never see cookies, the session or the desktop DOM. Third-party apps always run in the sandboxed iframe. Permissions are checked on the server.
2. Core apps use the same API as third-party apps; only the transport differs.
3. Permissions are declared in the manifest, confirmed by an administrator and revocable (`permissionRevoked`).
4. SemVer on the SDK interface. Deprecations last one major version.
5. Everything is async. Errors are RFC 9457 problems with a code and params.
6. Theme, accent, locale and density reach apps as CSS variables plus events. `@pithyx/ui` stays optional.
7. Apps control only their own windows. No focus stealing without a user action.
8. App backends are reached through YARP; their only identity source is `X-Pithyx-User`.
9. File access only through the SDK file API. File types for `openFile` are declared in the manifest.
10. Not in v1: app-to-app communication, background tasks without a window, widgets.
11. The SDK provides the locale and formatters, plus an optional i18n helper for ICU JSON. Apps may bring their own i18n.
12. Named storage requests in the manifest. An administrator maps them to real shares; the app never sees real paths.
13. Elevated rights are allowed, each named on its own (`docker.socket`, `host.network`, `device:<path>`, ..., `host.full`). They apply to the backend container only. Installing needs plain-language risk text plus password (and TOTP), by administrators only. New rights in an update need a new confirmation. A permanent badge, an audit log entry, revocable. Never sessions, password hashes or recovery keys of other users.

- [ ] Write the SDK specification as TypeScript interfaces with TSDoc: transports, handshake, versioning, error model, and the lifecycle below. `[REVIEW]` Draft in the `feature/sdk-spec` PR (types in `sdk/src/`, prototype `apps/demo`, decisions D54 to D66 and the guardrail review D68 to D78 in the PR); waits for the owner's review.
- [ ] After the spec review: daemon-backed app storage (`/api/apps/{id}/storage`, D61), the "not responding, close anyway?" dialog for `beforeClose` (D62), "Close all" in the app switcher asking guarded windows, relaunch of `single` apps sending `launch`, and the Vite dev server serving `apps/*` (only the production build does today).
- [ ] Lifecycle events: `launch` (with arguments, for example a file to open), `focus`, `blur`, `minimize`, `restore`, `beforeClose` (can be vetoed for unsaved changes), `beforeLogout` (last chance to save), `themeChange`, `localeChange`, `openFile` (file handler).
- [ ] Manifest fields for runtime behavior: `window.instances` (`single` or `multi`), `backend.lifecycle` (`always` or `onDemand`), `backend.idleTimeout`, `backend.resources` (memory, CPU), `backend.schedules`.
- [ ] Lifecycle events for updates and failures: `appUpdated` (reload required), `backendUnavailable`, `backendRestored`, `permissionRevoked`.
- [ ] One SDK, two transports: in-process for core apps, MessageChannel for third-party iframes. Core apps must not bypass the SDK.

- [ ] App manifest: JSON Schema as the source, C# and TS types generated, validation with clear error codes.
- [ ] App registry: core apps loaded with `import()` in the desktop context; third-party apps in iframes with `sandbox="allow-scripts allow-forms"` (no `allow-same-origin`), served from the same host.
- [ ] SDK transport: one `MessageChannel` per app instance, `init` handshake with SDK version, locale and theme tokens.
- [ ] SDK API v1: `window.setTitle`, `window.close`, `window.setSize`, `notify`, `session.save`, `session.restore`, `storage.get`, `storage.set` (daemon-backed, replaces `localStorage` in opaque-origin iframes), the file API (`files.pick`, `files.saveAs`, `files.read`, `files.write`, `files.url`, `files.list`, `files.open` with opaque file handles, served with step 6), formatters and the optional `@pithyx/sdk/i18n` helper.
- [ ] Permission broker in the desktop; app identity comes from the port. The broker only answers early; the daemon checks every permission again (guardrail 1), with `app.permission.denied`.
- [ ] Daemon side of the manifest permissions (M4): install dialog with confirmation, per-permission revoke pushed as `permissionRevoked`, named storage mapping to share folders, elevated rights with risk texts, password and TOTP confirmation, badge, audit log and the filtering Docker socket proxy (guardrail 13, D71). The `host.full` warning says plainly that it is root on the host and that the promise about other users' secrets then only covers the SDK API.
- [ ] Two sample apps: one core app in React, one third-party app in plain TypeScript that uses only `@pithyx/sdk`.
- [ ] SDK reference generated with TypeDoc.

### 5.4 Tests and budgets

- [ ] Unit tests for the window store and the SDK RPC layer. Window store done (`desktop/src/wm/*.test.ts`); the SDK RPC layer comes with 5.3.
- [x] Playwright: open, drag, resize, maximize, restore, close; layout survives a reload. Run at phone, tablet, laptop and ultrawide sizes with touch and mouse.
- [x] Accessibility checks (axe) in CI; keyboard-only walkthrough of login and desktop. axe (WCAG 2.2 AA) runs in E2E for sign-in, reset, desktop, start menu, window and account menus and the phone switcher; `e2e/tests/keyboard.spec.ts` signs in, opens an app, closes it and signs out with the keyboard only.
- [x] Performance budgets as CI tests: initial desktop bundle under 200 KB gzip, desktop usable after login under 1 s on repeat visit. The bundle budget runs in CI (`pnpm check:bundle`); it is split into sign-in and desktop since the #13 review. Sign-in keeps its 5 percent tolerance against `tools/checks/bundle-budget.json`; the desktop has a fixed limit of 150 KB gzip since 2026-09-29 (owner's decision: the timing budgets decide). `e2e/tests/budgets.spec.ts` holds the timing budgets against the production build on Fast 4G with 4x CPU slowdown: sign-in usable under 1.5 s cold, click feedback under 100 ms, desktop under 1 s after the click without Argon2, reload under 500 ms.

**Done when:** the plain TypeScript sample app opens in a window, sets its title, sends a notification and restores its state after a reload, and the owner confirms the desktop feels right. `[REVIEW]`

---

## Step 6: File manager

Effort: high.

Goal: the performance showcase. It must feel faster than DSM File Station.

- [x] Detailed task list to be written together with the owner before starting. `[DECISION]` The owner set the backend scope on 2026-09-27 (6.1) and approved the frontend plan the same day (6.2), with the double click as a switchable default action, Ctrl+A as "the whole folder except ..." and the SDK extension under its own decision number.

### 6.1 Backend (daemon)

Draft PR `feature/file-service`, decisions from D79 in its comments.

- [x] Shares before M3: `shares` and `share_access` (migration 0005), per-user access none, read or readWrite, administrators everything, `GET/POST/DELETE /api/shares`, `PUT /api/shares/{id}/access/{userId}`; a share the caller cannot use answers `fs.share.not_found`. Simulation seeds a share "Shared" in the data directory.
- [x] File service with access checks on every request and traversal protection by the kernel: every path is opened with `openat2(RESOLVE_BENEATH | RESOLVE_NO_MAGICLINKS)` below the share root, all later calls work on handles; `.` and `..` are refused.
- [x] Cursor pagination: natural order without a stat per entry, keyset cursors that survive changes, size and date orders; first page of 100,000 entries under 200 ms (xUnit budget, Release build as in CI; about 80 ms on dev01 since the first page is selected instead of sorting the whole folder, which took 124 to 226 ms depending on the JIT state of the process).
- [x] Range downloads with ETag and `Content-Security-Policy: sandbox`, streamed uploads into a hidden temporary file renamed into place, new folders, renames.
- [x] Jobs module (migration 0006) and copy, move and delete as jobs with progress, conflict policies and in-kernel copies (`copy_file_range`). On dev01 ZFS 2.3.9 did not clone blocks (`bcloneused` stayed 0, also for a plain `copy_file_range` outside Pithyx); find out why with M3.
- [x] Live changes of open folders over inotify (`files/<share>/<folder>`), watched only while subscribed, no access or open events, no polling.
- [x] Recycle bin per share (migration 0007): deleting moves items to the hidden `.pithyx-trash` at the share root (a rename, or copy and delete across file systems), with restore to the original place (missing folders recreated, conflicts renamed by default), deleting single items for good, emptying, and a retention per share set by administrators (default 30 days, 0 keeps items until emptied), enforced hourly. Deleting for good without the bin only with an explicit `permanent: true`.
- [ ] M3: hide `.pithyx-trash` from SMB and NFS (`veto files`) and let Samba's `vfs_recycle` put SMB deletes into the same bin with a row per item.
- [ ] Files are created as the daemon's user (root) until M3 provisions Linux users (release blocker, see "Release blockers").
- [ ] Resumable uploads (tus) on the same temporary-file mechanism.
- [ ] Files and folders on the desktop: the contents of the `Desktop` folder in the user's home share as desktop items on the same grid (positions stored per user in `desktop_items` with kind `file`), created, renamed and deleted as real files, opened in the file manager. The items UI from step 5 (selection, drag, menus) is ready for them.
- [ ] Scope: streamed listings over the WebSocket, client cache with stale-while-revalidate, inotify on open folders, prefetch on hover, virtualized list, Web Worker for sort and filter, optimistic actions, thumbnails, tus uploads, explorer keyboard controls, viewers and editor. Done in 6.2: cursor pages over REST (the approved plan; pages instead of a WebSocket stream), client cache with stale-while-revalidate, live changes on open folders, virtualized list, optimistic actions, keyboard controls. Open: prefetch on hover, Web Worker for filter, thumbnails, tus uploads, viewers and editor.
- [ ] Budgets as CI tests: previously visited folder under 16 ms, first chunk of a new folder under 100 ms (LAN, warm cache), 10,000 entries listed server-side under 50 ms (warm), steady 60 fps while scrolling 50,000 entries. The first two are E2E budgets since 6.2 (`budgets.spec.ts`); the daemon budgets the first page of 100,000 entries (6.1); 60 fps is measured by hand (too noisy for CI), see pithyx/pithyx#24.

### 6.2 Frontend (Files app)

Decisions from D92 in the PR's comments.

- [x] SDK file manager API (D92): `files.shares`, `files.list` with sort and total, `files.watch`/`unwatch` with `folderChanged`, `files.upload` with `uploadProgress` and no time limit, `files.download`, `files.createFolder`, `files.rename`, `files.run` for copy, move and delete as jobs with `jobChanged` and `jobs.cancel`, `trash.list`/`restore`/`delete`/`empty`, `ui.showError`. Whole folders are named as `{ all: true, except }`; the daemon expands them (`allIn` in `POST /api/files/operations`). The desktop broker passes every call to the REST API; the daemon checks the user's rights.
- [x] Files as a core app on the SDK's in-process transport, in its own chunk loaded with `import()` when its first window opens; the desktop bundle stays below its budget (D93).
- [x] Folder view: virtualized grid, cursor pages as the view scrolls, server-side sort by name, size or date, visited folders from the cache and revalidated in the background, live changes from other sessions and the file system.
- [x] Selection with click, Ctrl, Shift and the keyboard; Ctrl+A selects the whole folder as "everything except ..." without loading or sending its names (D96).
- [x] Double click, Enter or tap runs the default action: folders open, files download for now; one function decides it, so viewers can take over later (D95).
- [x] Upload with the button or by dropping files from the system, with progress; downloads through the browser.
- [x] New folder and rename in the row, copy, cut and paste (also between Files windows), drag and drop onto folders (Ctrl or Alt copies), delete into the recycle bin (Delete) or for good after asking (Shift+Delete), all optimistic with rollback; a name conflict asks once (skip, replace, keep both) and runs the job again.
- [x] Job and upload progress in the status bar with cancel; errors in the desktop's error dialog with the request id.
- [x] Recycle bin per share: origin, deletion and expiry, restore, delete for good and empty after asking, retention shown.
- [x] Current folder saved with the window (`session.save`) and restored after a reload; the sidebar becomes a sheet in narrow windows.
- [x] Tests: unit tests for natural order, selection, live changes on loaded pages, paths and free names; E2E for listing, upload, download, rename, copy, move, conflict, recycle bin, live changes, a whole-folder selection, keyboard, axe on laptop and phone, and the two navigation budgets.
- [ ] Prefetch folders on hover.
- [ ] Filter in the current folder in a Web Worker.
- [ ] Thumbnails, viewers and the editor; the default action then opens files in a viewer.
- [ ] Drop onto the breadcrumb and onto shares in the sidebar.

---

## M3: NAS core (steps 7 to 14)

Ordered by risk: what can lose data comes first and gets the strongest tests. Decisions D100 to D111 are logged in the M3 planning PR (pithyx/pithyx#25); the owner's answers are binding. Every step lists how it is tested on dev01 with the VYXTEST disks, what must never happen, and how CI guards it.

Rules for every M3 step:

- Destructive storage actions touch only disks that `DiskGuard` (step 7) allows. On dev01 that means the VYXTEST disks listed in `/etc/pithyx/test-disks`; test pools are still cleaned up with `tools/dev-vm/testpool.sh destroy` and `wipe`.
- Before a storage PR is merged, its destructive suite runs on dev01 and the result goes into the PR (`[REVIEW]` by the owner for steps 8, 9 and 12).
- No real data goes on a Pithyx pool before step 9 is done (see "Release blockers").
- Every page ships with its step. Storage (pools, disks, datasets and volumes, snapshots, SSD cache, spindown, health and maintenance) lives in the Storage app; the Settings app holds only system settings (users and groups, shared folders and rights, network, energy profiles, updates, personalization, about).

---

## Step 7: Storage safety foundation

Effort: high.

Goal: the daemon sees real pools on dev01 and cannot write to a disk it must not touch. Nothing in M3 writes to a disk before this step is done.

### 7.1 Real mode on dev01

- [x] A dev systemd unit runs the daemon as root in real mode on dev01 (simulation stays the default for E2E and local development). `/etc/pithyx/environment` decides `dev` or `prod`; a missing file means `prod`. (`tools/dev-vm/pithyx-dev.service` with `install-dev-daemon.sh`; the owner installs it, since agents may not install system units.)
- [x] `IStorageBackend` (one of the two allowed abstractions) with ZFS as its first implementation; mdadm and btrfs come later through the same interface (D111).
- [x] Parsers for `zpool status -j`, `zpool list -j`, `zfs list -j` and `zfs get -j` (OpenZFS 2.3 JSON output) as pure functions with fixtures recorded on dev01 with `tools/fixtures/record.sh` (single disk, mirror, raidz1, degraded, resilvered, scrub paused and finished, no pools). Encrypted pools are recorded in step 9, exported pools with the import in step 8.
- [x] `SystemStateService` keeps pools and datasets in memory, refreshed from `zpool events -f` (streamed, no polling) and at startup. `GET /api/storage/pools` reads from memory; topic `storage/pools` pushes changes.

### 7.2 Disk guard and command plans

- [x] `DiskGuard` as a pure decision function over the lsblk tree, the mount table and `/etc/pithyx/*`:
  - The system disk (anything holding `/`, `/boot`, `/boot/efi`, swap or `/var/lib/pithyx`) is always refused, with no override.
  - In `dev`, only disks whose serial is listed in `/etc/pithyx/test-disks` are allowed.
  - In `prod`, a disk must be empty (no partitions, no filesystem or RAID signatures, no ZFS labels, not part of an imported pool). Otherwise the user must confirm with the disk's serial number.
- [x] Command plans: every destructive action is first built as a list of exact commands, shown to the user, logged with the request id, and only then run as a job. The job re-checks `DiskGuard` right before each command, because disks can change in between. (Step 7 builds, shows and tests plans; `CommandPlanRunner` gets its endpoint and job in step 8, so nothing destructive runs yet.)
- [x] Pools are never imported automatically, never with `-f`, and never as a side effect of recovery ("Recovery never modifies pools automatically"). Import is an explicit action in step 8.

### 7.3 Settings app shell

- [x] Settings as a core app on the SDK (in-process transport, its own chunk loaded with `import()`), with pages Storage, Users, Shares, Network and Energy that later steps fill. Administrators only; the start menu and the desktop link to it. (Owner's rule: only finished pages ship, so step 7 has Storage alone. Its administrator calls are the internal `internal.admin.storage.*` methods of `desktop/src/apps/sdk/internal.ts`, not part of `@pithyx/sdk`.)

### 7.4 CI (D109)

- [x] `CLAUDE.md` (Tests) says integration tests run on GitHub-hosted runners with file-backed vdevs and loop devices, and that the destructive suite runs on dev01 before a storage PR is merged.
- [x] A `storage` CI job that runs only when storage code changes (path filter in `changes`): installs `zfsutils-linux` on the Ubuntu runner, creates pools on sparse files and loop devices, and runs the integration tests against them. Each run is a fresh VM, so nothing survives it.

### Test on dev01

- Pools created with `testpool.sh` appear in the API and the topic without a reload; `zpool offline` on a test disk updates the state live.
- Every command plan against `sda` (the system disk) or a disk that is not listed as a test disk is refused, also with a correct serial typed as confirmation.
- A plan for a VYXTEST disk lists the exact `zpool` commands; nothing runs until it is confirmed.

### Must never happen

- A write to the system disk or, on dev01, to any disk that is not a VYXTEST disk.
- `zpool create`, `zpool labelclear`, `wipefs` or a partition change that did not pass `DiskGuard` right before it ran.
- Importing a pool automatically or with `-f`.

### CI

- Property tests of `DiskGuard` over generated lsblk trees (system disk anywhere in the tree, partitions, signatures, pool members, serial mismatch, duplicate serials).
- Golden tests of every command plan (exact argument lists).
- Parser fixture tests; the `storage` job on file-backed pools.

**Done when:** the daemon on dev01 runs in real mode, shows the test pools live, refuses every destructive plan outside the VYXTEST disks, and the `storage` CI job is green.

---

## Step 8: Pools, datasets and snapshots

Status: done (pithyx/pithyx#29, merged 2026-09-29 after the owner tried it on dev01). The findings of that test are listed under "Release 0.1".

Effort: high.

Goal: pools are created, maintained and watched from the UI, with snapshots that users can restore from, and nothing is lost by a wrong click.

### 8.1 Pools

- [x] Create a pool: single disk, mirror, raidz1, raidz2, raidz3; a stripe only with a warning that one failed disk loses everything. Defaults `ashift=12`, `compression=lz4`, `xattr=sa`, `acltype=posixacl`, `atime=off`, `normalization=formD`.
- [x] The create dialog always asks about encryption and explains that it can only be chosen when the pool is created (step 9 adds the key handling; until then encryption is offered only once step 9 is done, and the dialog says so).
- [x] Dataset layout (D100, decided): the pool root holds no data; Pithyx datasets live under `<pool>/pithyx/` (`shares/<name>`, `homes/<user>`, `apps/<id>`, `system`), mounted at `/<pool>/<share>`, `/<pool>/homes/<user>` and `/<pool>/.pithyx/...` (the paths in `daemon/CLAUDE.md`).
- [x] Import an existing pool only as an explicit action, first read-only with a preview of its datasets; export. ("Import a pool…" in the Storage app scans on request, imports read-only first, and the pool's card then imports it for use or exports it again; every pool card can export.)
- [x] Scrub, on demand and on a schedule (default monthly).
- [x] Replace a disk, offline and online a disk, add a vdev (with a warning when it lowers redundancy; adding is irreversible), raidz expansion (OpenZFS 2.3). (All in the Storage app: "Add disks…" on each pool card with the redundancy warnings and the typed name where needed, "Expand with a disk…" on raidz vdevs with the expansion's progress on the card.)
- [x] Destroy a pool only after typing its name, with a list of what it holds.
- [x] Block cloning (D90): find out why `copy_file_range` cloned no blocks on dev01 (OpenZFS 2.3.9), whether `zfs_bclone_enabled` is off because of the known block cloning data corruption bug, and enable it only on an OpenZFS version with the fix, with a test on dev01. (It is on by default in 2.3.9 and works: a Files copy on a pool share cloned 8 MB on dev01. The step 6 test ran on the ext4 system disk, which cannot clone.)

### 8.2 Health and warnings

- [x] SMART with `smartctl -n standby` (never wakes a disk), SMART self-tests on a schedule (short weekly, long monthly by default), skipped while a disk sleeps.
- [x] Persistent notifications plus a desktop banner for: pool DEGRADED or FAULTED, SMART failures and warnings (reallocated or pending sectors, failed self-test), scrub errors, and fill level (warning at 80 percent, critical at 90 percent). They stay until the cause is gone; the user can mute but not delete them.
- [x] Capabilities are detected: in a VM without SMART, the UI explains why there is no SMART data instead of showing an error.

### 8.3 Datasets and volumes

- [x] Create, rename, set quota and reservation, compression and record size. Delete only when empty or after typing its name. (Creating datasets happens through shared folders. In the Storage app's Datasets section; rename, quota, reservation, compression, record size and delete are command plans (`dataset.rename`, `dataset.set`, `dataset.destroy`); delete always needs the full name typed and lists every child and snapshot that goes. The pool root, the layout datasets and what Pithyx finds by name (homes, apps, system) are never renamed or deleted; datasets holding a share or a held snapshot are not deleted.)
- [x] Volumes (zvols) for VMs later (M5b); created with the same guard. (`volume.create` below `<pool>/pithyx/volumes`, sparse or thick, 16 KiB blocks; deleted with `dataset.destroy`.)
- [x] The dataset API has no share assumptions, so M4 uses it for app datasets (see "Interfaces for M4"). (`dataset.set` takes app datasets; renaming and deleting them stays with M4's own lifecycle, since apps find them by name.)

### 8.4 Snapshots

- [x] Manual snapshots and schedules with retention (hourly, daily, weekly, monthly), named `pithyx-<schedule>-<utc>`.
- [x] Pithyx deletes only snapshots it created (D104, decided); snapshots created by the user or another tool are never pruned.
- [x] Holds for snapshots that must stay (pre-update snapshots in M4, snapshots a backup still needs in M6).
- [x] Restore a file or folder from a snapshot in Files ("Previous versions", read from `.zfs/snapshot`); rollback of a whole dataset only after typing its name, and never silently discarding newer snapshots (ZFS refuses without `-r`; Pithyx never passes `-r` without listing what goes).
- [x] Retention is a pure function tested with time series, including clock jumps (NTP corrections, a box that was off for months).

### 8.5 Storage app

- [x] Pools with layout, disks, health and fill level; datasets; snapshots with schedules; every destructive action through a command plan dialog. (Pool, disk, dataset and volume actions go through plan dialogs; deleting a snapshot and rolling back are the plans `snapshot.destroy` and `snapshot.rollback` with the same preview, the rollback after typing the dataset name and with every newer snapshot listed, checked again under the snapshot lock when it runs.)
- [x] The Disks app from step 3 became the Storage app (owner's decision on 2026-09-28): an administrator app with its own icon and chunk, in the Pithyx menu and on administrators' desktops, with sections for pools (wizard, repair and maintenance), disks (guard verdict and SMART), datasets and volumes, snapshots, and health and maintenance. The Settings page Storage is gone; Settings points to the app.

### Test on dev01

- Create a raidz1 on VYXTEST01 to VYXTEST03 from the UI; detach VYXTEST02 in Proxmox, see DEGRADED with a notification and a banner; replace it with VYXTEST04 and watch the resilver.
- Snapshot schedules with a moved clock; restore a deleted file from "Previous versions".
- Fill a small dataset past 80 and 90 percent and see the warnings.
- Clean up with `testpool.sh destroy` and `wipe`.

### Must never happen

- A pool created without the encryption question.
- A snapshot deleted that Pithyx did not create.
- A rollback that discards newer snapshots without listing them and a typed confirmation.
- Destroying a pool or dataset without its typed name.
- Adding a vdev that lowers redundancy without a warning.
- Retention that deletes every snapshot after a clock jump.
- A health check that wakes a sleeping disk.

### CI

- The `storage` job: create every layout on file vdevs, snapshot, retention, replace a vdev, scrub, import and export.
- Retention as a pure function with time series; golden command plans; parser fixtures for degraded and resilvering pools.

**Done when:** the dev01 scenario above runs from the UI, no command touched a disk that is not a VYXTEST disk, and the owner has tried it on dev01. `[REVIEW]`

---

## Step 9: Encryption and key vault

Effort: high. Moved after v0.2 (owner's decision on 2026-09-30, see "Release 0.1").

Goal: pools can be encrypted, unlock after a power loss if the owner wants that, and nobody gets at the data without the key. Real data goes on Pithyx pools only after this step.

- [ ] ZFS native encryption per pool (D106, decided): the encryption root is `<pool>/pithyx`, so everything Pithyx stores on the pool is encrypted with one key. Encryption per share can follow later.
- [ ] After creating an encrypted pool, the key must be downloaded or printed and its storage confirmed before the pool can be used, like the recovery key in setup.
- [ ] Unlock manually with the key, automatically from the key vault on the system disk (root only, mode 0600, never in a support bundle), or from a USB key. The UI explains that the key vault protects against stolen disks, not against a stolen box.
- [ ] A locked or missing pool stops its shares; the UI says why. The same state is published for M4, where apps with mounts on it do not start.
- [ ] Change the key (`zfs change-key`) only after the new key is saved and confirmed; the old key keeps working until then.
- [ ] Secrets (keys, passphrases) are masked in every log and never written to the pool itself.

### Test on dev01

- An encrypted mirror on VYXTEST05 and VYXTEST06; reboot with the key vault: unlocked; without it: locked, shares off, a clear message.
- USB key simulated with a loop image; a wrong key is refused; after `zfs change-key` both a reboot and a manual unlock work with the new key only.

### Must never happen

- An encrypted pool whose key the user has not confirmed storing.
- A key in a log, a support bundle or on the pool.
- A key vault file readable by anyone but root.
- A key change that invalidates the old key before the new one is stored.

### CI

- Secret masking tests for every key path; the `storage` job with a passphrase file (create, lock, unlock, change key); unit tests of the lock states.

**Done when:** after a reboot and a simulated power loss the pool is unlocked or locked exactly as configured, data is unreadable without the key, and the owner has tried it on dev01. `[REVIEW]`

---

## Step 10: Users and groups as Linux accounts

Effort: high.

Goal: every Pithyx user is a real Linux and Samba account, and files belong to the user who created them (resolves D89).

- [ ] Migration: `groups`, `group_members`, and uid/gid allocation from fixed ranges (D103, decided): users 20000 to 29999, groups 30000 to 39999, apps 40000 to 49999 (reserved for M4). Numbers are never reused, not even after a user is deleted. Done for users (migration 0013, `id_allocations`, `UidAllocator`); groups follow.
- [ ] `/etc/login.defs` (`UID_MIN`, `UID_MAX`, `GID_MIN`, `GID_MAX` and the `SYS_*` values) is set so `adduser` and `useradd` outside Pithyx never allocate in 20000 to 49999; the reconciler checks this at startup and warns when it changed. The check and warning are done (`LoginDefs`); setting the values is the image's job (M5), dev01 still has Debian's `UID_MAX 60000`.
- [x] Reconciler (users; groups follow with groups): SQLite is the source of truth; the daemon runs `useradd`, `usermod`, `userdel`, `groupadd` and friends only for accounts in its ranges. A name that already exists in `/etc/passwd` or `/etc/group` but is not managed by Pithyx is refused (the static reserved list in `CredentialRules` only covers known system accounts).
- [ ] Shell `nologin` unless the user has the SSH login right (step 11). Every account gets `nologin` for now.
- [ ] Samba accounts (`pdbedit`) get their password whenever the daemon has it in plain text: setup, sign-in, password change, admin reset.
- [x] File ownership (release blocker D89): the file service sets owner and group with `fchownat` on everything it creates (uploads, copies, restored items, new folders).
- [ ] A home dataset per user (D105, decided) under `<pool>/pithyx/homes/<user>`, with the `Desktop` folder that step 5's desktop items use. The dataset on the first pool (owner the user, mode 0700) is done; the `Desktop` folder follows.
- [ ] Optional user quotas (`userquota@<user>`) on shares and on the home dataset.
- [ ] Settings page Users (the standalone `UsersPage` with create, edit, password reset and delete exists; it is registered in Settings once the new Settings shell is in dev): users and groups, create, disable, delete (with a choice for the home data: keep, move to a share, delete after typing the name), admin password reset, quotas.

### Test on dev01

- Create a user: `id`, `getent`, `pdbedit -L` show it; upload a file: owner and group are the user's; delete the user: its uid stays reserved and a new user gets another one.
- `adduser` outside Pithyx allocates below 20000.

### Must never happen

- An account outside the Pithyx ranges changed or deleted.
- A uid or gid reused, so that a new user inherits old files.
- The last administrator removed or demoted.
- Home data deleted without an explicit choice.
- Linux and Samba accounts out of step after a restart.

### CI

- The reconciler as a pure diff (wanted against actual `passwd`, `group`, Samba state) with table tests; an integration test that runs the real commands on the runner (a fresh VM per run).

**Done when:** Linux and Samba accounts follow SQLite after every change and restart, every file the daemon creates belongs to the requesting user, and the D89 release blocker is checked off.

---

## Step 11: Permission model

Effort: high.

Goal: the permission model from `CLAUDE.md`, checked on every request, and the same on disk for every protocol.

- [ ] Grants to users and groups; effective permissions are the union of all grants; administrators have everything; there are no deny rules.
- [ ] Resources: shares (none, read, readWrite), protocols (SMB, NFS, SFTP; WebDAV later), apps (allowed or not), special rights (administrator, desktop login, SSH login, reboot and shutdown).
- [ ] The grant subject is general (`user`, `group`, and `app` in M4), and the resolver takes a principal, so M4 needs no rework (see "Interfaces for M4").
- [ ] The resolver is a pure function with exhaustive and property tests. It is used on every request and every topic; revoking a right is pushed over the WebSocket, and sessions without the desktop login right end.
- [ ] Migration from `share_access` (per user, step 6) to grants.
- [ ] Enforcement on disk (D102, decided): generated Linux groups per share (`vs-<id>-r`, `vs-<id>-rw`) and POSIX ACLs on the share dataset, so SMB, NFS, SFTP, the file service and later apps see the same rights. Pithyx is the only source of truth: ACLs are never edited over SMB (step 12), and the reconciler reports every difference between grants and the file system as a warning.
- [ ] Optional user quotas from step 10 are part of the share settings.
- [ ] Settings pages: grants on the Users page (per user and group) and on the Shares page (per share). (Settings > Shared folders sets per-user access from `share_access` today; its rights editor becomes the grant editor.)

### Test on dev01

- Users A (readWrite through a group), B (read) and C (none): the file service, `getfacl` and `sudo -u` access agree for each of them; removing A from the group takes effect on the next request and on disk.

### Must never happen

- A combination of groups that grants more than the union of its grants.
- A revoked right still working after the next request.
- ACLs that differ from the grants without a warning.
- A user without the administrator right reaching an administrator endpoint.

### CI

- Table tests of every combination of grants; an endpoint matrix (every endpoint against every role); golden tests of the ACL and group generator.

**Done when:** the endpoint matrix is green and after every reconcile the file system matches the grants.

---

## Step 12: Shares over SMB, NFS and SFTP

Effort: high.

Goal: shares are datasets reachable over SMB, NFS and SFTP with exactly the rights the file service enforces, and one recycle bin for all of them.

- [ ] A share is its own dataset (D101, decided) under `<pool>/pithyx/shares/<name>`, with its own snapshots, quota and recycle bin. The folder shares from step 6 exist only in development and are created again as datasets (no migration of real data). (The dataset part is done, pulled forward: Settings > Shared folders creates a share as its own dataset through a command plan, with a description and per-user rights, and deletes it after its name is typed with a choice for the data (keep, or destroy the dataset with every snapshot listed); registering a folder works only on dev and in simulation. SMB, NFS and SFTP are open.)
- [ ] SMB: `smb.conf` generated from SQLite with the "Managed by pithyx" header; manual overrides in include files that are never touched; checked with `testparm` before every reload.
  - SMB3 only, signing required, encryption optional per share, `fruit` for macOS, `shadow_copy2` so snapshots appear as "Previous versions".
  - ACL editing over SMB is off (`nt acl support = no` and matching settings): permissions change only in Pithyx (D102).
  - `veto files` hides `.pithyx-trash`; `vfs_recycle` moves SMB deletes into the same recycle bin with a row per item (release blocker).
  - Samba's passdb is owned by root, mode 0600, and never part of a support bundle.
  - The Users page shows which users have no SMB password yet (they never signed in on the web) and asks them to sign in once.
- [ ] NFS (D107, decided): NFSv4.2 with `sec=sys`, a list of allowed clients per share, `root_squash`, off by default, and a warning that NFS trusts the client's user ids. Kerberos later.
- [ ] SFTP (D108, decided): OpenSSH `internal-sftp` in a chroot with a bind-mount view of the shares the user may use, only with the SFTP right and independent of the SSH login right.
- [ ] Settings page Shares: protocols, allowed clients, recycle bin retention, snapshot visibility, quotas. (The base page exists as Settings > Shared folders: list with pool, space, description and rights, create, edit, delete. Step 12 extends it with protocols, allowed clients, recycle bin settings, snapshot visibility and quotas; step 11 replaces its per-user rights with grants to users and groups.)

### Test on dev01

- `smbclient` as users A, B and C: read, write and no access as granted; a delete over SMB lands in the recycle bin and can be restored in Files; "Previous versions" lists snapshots; `smbcacls` cannot change permissions.
- An NFS mount over localhost with a foreign uid is squashed; `sftp` as A shows exactly A's shares.

### Must never happen

- A half-written or invalid `smb.conf` or `exports` loaded.
- A manual include file overwritten.
- A share reachable over a protocol the user has no right for.
- `.pithyx-trash` visible over any protocol.
- Deleting a share deletes its data without an explicit choice.
- An NFS export without `root_squash`.
- The Samba passdb readable by anyone but root or included in a support bundle.

### CI

- Golden files for every generated config, validated with `testparm` and `exportfs` on the runner; an SMB integration test against `smbd` on the runner with three users.

**Done when:** SMB, NFS and SFTP give users A, B and C exactly the rights the file service gives them, deletes over SMB share the recycle bin, the SMB recycle bin release blocker is checked off, and the owner has tried it on dev01. `[REVIEW]`

---

## Step 13: SSD cache with a recommendation

Effort: medium.

Goal: SSDs speed up pools where it helps, and the UI says when it does not.

- [ ] L2ARC (read cache), SLOG (synchronous writes) and special vdevs (metadata and small blocks, which also keep listings off sleeping disks).
- [ ] A recommendation as a pure function of the disks, the pool layout, ARC statistics and the share of synchronous writes, with the reason in plain language ("Your ARC hit rate is 97 percent; a read cache would not help").
- [ ] Special vdevs and SLOGs only as mirrors; adding what cannot be removed (a special vdev on raidz) only after typing the pool name.
- [ ] In the Storage app, on the pool (with the recommendation), not in Settings.

### Test on dev01

- Add and remove an L2ARC; add a mirrored special vdev on VYXTEST05 and VYXTEST06. The virtual disks all report as non-rotational, so the recommendation is tested with fixtures.

### Must never happen

- A special vdev or SLOG without a mirror (losing it loses the pool).
- An irreversible vdev added without the typed confirmation.

### CI

- A decision table for the recommendation; golden command plans; the `storage` job adds and removes cache vdevs on file vdevs.

**Done when:** recommendation, adding and removing work on dev01 and the protection rules are tested.

---

## Step 14: Energy and network

Effort: medium.

Goal: the box sleeps when it can, the user sees what keeps it awake, and network changes can never lock the owner out.

### 14.1 Energy

- [ ] Spindown per pool with hd-idle, off by default; set in the Storage app on the pool.
- [ ] Snapshot schedules and SMART self-tests follow spindown: a sleeping pool is not woken for a snapshot of unchanged data (a snapshot is taken when the pool is awake anyway or has changed), and idle-check (`tools/dev-vm/idle-check.sh`) verifies that no Pithyx schedule wakes a disk.
- [ ] Power profiles (performance, balanced, efficient) set the CPU governor and EPP; SATA ALPM, PCIe ASPM and USB autosuspend stay opt-in.
- [ ] A diagnostic that shows what keeps disks awake (process and file, through fanotify on the pool mounts and `/proc/<pid>/io`) and CPU package C-state residency.
- [ ] Capabilities are detected: in a VM without spindown or C-states, the page explains why instead of showing errors.

### 14.2 Network (D110, decided)

- [ ] systemd-networkd configuration generated from SQLite: DHCP or static, IPv4 and IPv6, DNS, host name.
- [ ] Every change applies with a 60 second confirmation timer and rolls back automatically when it is not confirmed.
- [ ] On dev01 only a dedicated test NIC listed in `/etc/pithyx/test-nics` is ever changed, never the management NIC. Blocked until the owner has added the second NIC to dev01.

### 14.3 Settings pages Energy and Network

- [ ] Both pages in the Settings app (power profiles, the wake-up diagnostic and the network with the confirmation countdown); spindown per pool is in the Storage app.

### Test on dev01

- Change the test NIC and let the timer run out: it rolls back; confirm: it stays.
- The diagnostic names a process that writes to a test pool.
- Spindown and C-states only with fixtures (dev01 is a VM); a hardware test on the owner's N305. `[REVIEW]`

### Must never happen

- Pithyx waking a disk itself (SMART, logs, listings, schedules).
- A network change without the rollback timer.
- The management NIC of dev01 changed.
- An option known to cause instability on by default.

### CI

- Golden tests of the networkd generator; the rollback timer as a state machine with a fake clock; parsers for sysfs, hd-idle and fanotify events.

**Done when:** a network change rolls back after 60 seconds without confirmation, the diagnostic finds what keeps a disk awake, and idle-check shows no disk woken by Pithyx on an idle box.

---

## Interfaces for M4

M3 builds these so M4 (apps) uses them without rework. M4 itself is not part of M3.

- Named storage (SDK guardrail 12): M3 provides shares as datasets with grants and an internal daemon API that resolves a share and a relative path to a mount path. M4 adds `storage_mappings` (app, storage id, share, subpath, access) on top and bind-mounts the folder into the backend at `/pithyx/storage/<id>`. A window's effective access is the lesser of the manifest's access and the user's share right; the backend's access comes from the share groups.
- Users: `X-Pithyx-User` carries the stable, Linux-valid user name and the user id. Each app backend runs as its own uid and gid from the app range that M3 reserves (40000 to 49999, same allocator), mapped through userns-remap. An app reaches shares only as a member of the share groups from step 11, never with a user's uid.
- Datasets: one per app under `<pool>/pithyx/apps/<id>`, mounted at `/<pool>/.pithyx/apps/<id>`, with a quota. Before every update a snapshot `pithyx-preupdate-<version>` with a hold, and a rollback when the update fails. M3's dataset, quota, snapshot and hold API carries no share assumptions for this.
- Permissions: grant subjects are `user`, `group` and later `app`; "app allowed" is already a resource in M3. M4 adds manifest permissions as their own table but uses the same resolver, the same checks on every request and the same push on revoke.
- Availability: M3 publishes when a pool or dataset is locked or missing; M4 refuses to start apps with mounts on it and shows why.

---

## Later milestones

M3 is detailed in steps 7 to 14. M4 to M6 get their own detailed steps once M3 is done. Write them into this file first and get them reviewed before starting. `[REVIEW]`

Known requirements for later milestones:

- Website, first task once `website/` exists: an error code reference generated from `i18n/en/errors.json` (and the translated files), plus a CI check that every code in `ErrorCodes` has a doc entry. Until then error codes are documented only through their i18n texts.
- Website user docs for shares and files from step 6: the Files app (double click downloads files for now, Ctrl+A selects the whole folder, the keyboard shortcuts F2, Delete, Shift+Delete, Ctrl+C/X/V, Backspace and Alt+Up, dropping files from the system to upload, Ctrl or Alt while dropping to copy instead of move, the conflict question, progress and cancel in the status bar), the recycle bin per share (restore, empty, retention, that deleting for good is only explicit), the upload behavior (the file appears only when complete, existing names are never replaced without asking), conflict choices for copy and move (skip, overwrite merges folders, rename to "name (2)"), that symlinks are shown but never copied, and the share rules for administrators (existing folder, no system folders, no overlapping shares).
- Website user docs for storage from steps 7 and 8: the Storage app (only administrators see it), what each disk verdict means (system disk, pool member, mounted, not a test disk on a development machine, has data and needs its serial, empty), that a pool preview probes the disks again and lists the exact commands, and why a disk with data needs its serial typed.
- Step 8 follow-ups that did not block the review: a live topic for snapshots (the Snapshots section reloads its list after each action and on reopening, but a snapshot a schedule takes while the section is open appears only then); `storage.scrub.finished` as an info notification when a scrub ends without errors; re-record the hand-written fixtures (`zfs-list-snapshots`, `smartctl-handwritten-*`, `zpool-scrub-handwritten-*`) on real hardware with SMART (dev01 is a VM without it).
- Website user docs for pool import, export and growing a pool from step 8: that a scan for importable pools reads every disk and may wake a sleeping one, that a pool is always imported read-only first (not mounted, not remembered after a restart) so its datasets can be checked, which datasets the read-write import leaves unmounted and why (system folders, hiding a mount, duplicate mount points, locked keys) and that those get `canmount=noauto`, when a forced import is needed (pool last used by another system) and why the name is typed, what exporting makes unavailable, and for adding a vdev and RAID-Z expansion what each warning means (lowers redundancy, replication mismatch, irreversible, no redundancy) and that existing data keeps its old parity ratio after an expansion.
- Website user docs for the Resource Monitor: what each memory part means (programs, ZFS cache that gives memory back, page cache), that CPU in the process list is the share of all cores, that the history covers only the time a monitor was open (nothing is measured while nobody watches), why disk temperatures are not shown there (they would wake sleeping disks), and that virtual machines usually have no temperature sensors. Also a design gallery screen for it (the gallery's sample app sends no events yet).
- Files thumbnails and viewer, follow-ups: video thumbnails (a frame, needs ffmpeg) and HEIC photos (needs a HEIF codec) show icons for now; optional light syntax highlighting for code in the viewer; pdf.js standard fonts and CMaps for PDFs that do not embed their fonts (they show with substitute fonts); design gallery screens for the tile view and the viewer. Website user docs: the tile view, where thumbnails are kept and that only images on screen are read, what the viewer shows and that HTML and SVG never run, "Open with", and the 1 MB limit for text.
- Website user docs for the Log Center: what the sources are (the daemon, `system/<service>` from the journal, `app/<id>`), that current logs stay on the system disk capped at 4 GB and are there without a pool, what the archive does and that it wakes the pool's disks about once a day, how search matches (message, fields, an exact request id), the export formats, and that the daemon's request log stays in the log files only. For app developers: `app.log` and its limits in the generated SDK reference. Also a design gallery screen for the Log Center.
- Website user docs for passwords: at least 8 characters, well-known passwords are refused, what the strength meter's reasons mean, that weak passwords are allowed with a warning and that there are no character class rules.
- Website user docs for users: that every user is a Linux account with a uid that is never reused, that deleting a user keeps its files and home folder, why a name can be refused (a system account has it), that the last administrator stays and nobody deletes themselves, and where the home folder is.
- Website user docs for the desktop from step 5: desktop items (selecting with a frame, Ctrl and Shift, moving by dragging and with Move, F2, Delete, Shift+F10, the note grip and its arrow keys, long press on touch, the 200 item and 4000 character limits, and that note text changed on two devices is kept on a second note), window menu and the Alt+Shift shortcuts (and that they do not apply in text fields), snapping and thirds on ultrawide screens, taskbar position, layout memory per device with its 90-day expiry and "Get desktop from ..." (which shows each device's browser, system, screen size and last use), that only the tab in use saves the layout, the phone app switcher, swipe and back gesture, and the `pithyx_device` cookie (what it is, that it holds no personal data, and that it is `Secure` on HTTPS).

- Self-hosted CI runner: set up by the owner (label `pithyx-ci`, repository variable `CI_RUNNER`) and used since D269. The ZFS, disk and account integration tests do not run on it; they run on dev01 (`tools/dev-vm/integration.sh`). Open: a throwaway VM per job for those tests, if dev01 is ever not enough.
- Third-party notices: generate the list of bundled NuGet and npm packages with their licenses (for example libsodium ISC, NSec MIT, Otp.NET MIT) for the About page, and check it in CI. It starts from the hand-written `desktop/public/third-party-notices.json` (fonts, Tabler, React Aria), which does not list transitive packages yet.
- `[DECISION]` Server-side rendered texts (email, SSH MOTD, push) in the recipient's language need culture data. Decide between keeping `InvariantGlobalization` (small, no libicu, but only invariant formatting and a hand-written plural rule for English and German in `IcuMessageFormatter`) and enabling ICU culture data (libicu in the image, about 30 MB, correct number, date and plural rules for every language). Today the daemon only renders the English `detail` fallback of problem details.
- M5: the system on redundant disks (owner's question in pithyx/pithyx#29). Today the system lives on one disk, and the disk guard never lets a pool use it: a failed system disk stops the box until it is reinstalled and the config backup restored (the pools survive). Proposal: the installer offers root on ZFS as a mirror of two or more disks (`rpool` with a small boot partition on each, like the system partition other NAS systems spread over every disk), the rest of those disks usable as the data pool, and a second disk can be attached to the system mirror later. The guard then treats the system pool's disks as system disks as it does now, and only the plans for the system mirror may touch them. `[DECISION]` for M5: root on a ZFS mirror of the chosen disks (recommended), or a system partition on every data disk. Superseded by "System layout v0.1" and its v0.2+ decision under "Release 0.1".
- M5: web-based installer instead of a TUI. Boot from USB, open the setup page in a browser, choose the system disk, the system installs itself and continues setup in the same page. The console only shows the address. The installer announces itself as `pithyx-setup.local`; an installed box gets the default host name `pithyx` and answers as `pithyx.local`. Detailed under "Web installer v0.1".
- M5: TLS as decided in `CLAUDE.md`: self-signed by default, trust wizard (local CA download with per-OS instructions), optional own domain with Let's Encrypt DNS-01. Passkeys and the service worker switch on once trusted.
  - Chromium does not use its HTTP cache for responses with a certificate error (measured in the #13 review): on the untrusted default every reload downloads the whole desktop again (about 1.1 s instead of 0.4 s on Fast 4G with 4x CPU). After sign-in on an untrusted certificate the desktop shows a dismissible hint that leads to the trust wizard and names faster reloads next to passkeys; the reload budget holds only once trusted. Add an E2E check that a trusted certificate serves `/assets/*` from the disk cache.
  - Kestrel negotiates HTTP/2 over TLS by default (ALPN); plain HTTP in development is HTTP/1.1 only, so the build keeps the sign-in path to few requests. Verify h2 on 443 and 7484 once TLS lands, and send `pithyx_device` with `Secure` there (it already does on HTTPS requests).
- M5: trust step. An optional third setup step "Trust this device" after the administrator and the recovery key: the box generates a local CA whose Name Constraints allow only its host name, its mDNS name (`<host>.local`) and its local IP addresses, and the step offers it for download with per-OS instructions (the same content as the trust wizard in settings). Reason: Chromium caches nothing for a page whose certificate is not trusted (D43 in pithyx/pithyx#13), so every reload downloads the whole desktop, and passkeys and the service worker stay off until the certificate is trusted. The Name Constraints keep an installed CA from being usable for any other site if the box is compromised. Skippable, and the desktop hint from the TLS item above stays for users who skip it.
  - `[DECISION]` Pull the trust step (with the self-signed certificate and the local CA it needs) forward before M5, or build it with the rest of TLS in M5? Pulling it forward makes every later performance measurement reflect what users see; keeping it in M5 avoids building certificate handling twice if the installer changes how the box learns its names and addresses.
- M5: SSH toggle in settings with the logo MOTD. The shell prompt is `λ pithyx ~ ›`.
- UPS (NUT): shut down cleanly on power loss (stop shares and apps, export pools, never touch the key vault), with a USB or network UPS; check before the release (see "Release blockers").

---

## Decision log

Decisions up to D268 are recorded in the pull requests that made them. From D269 on they are listed here as well: the choice, the alternatives and why.

- **D269** (low, 2026-09-30) CI runs on the runner named by the repository variable `CI_RUNNER` (`runs-on: ${{ vars.CI_RUNNER || 'ubuntu-latest' }}`), the owner's self-hosted runner without Docker and root. The `storage` job (real ZFS, `modprobe`, `sudo`) left GitHub CI; its tests run on dev01 through `tools/dev-vm/integration.sh`, with the sparse files in `/dev/shm` so the host's worn SSD sees no writes. Update of the same day, by the owner: the self-hosted runner (`dedi-01`, label `pithyx-ci`) is the CI; dev01 has too few resources for the performance gate and E2E and runs only build and test checks before a push and the ZFS integration tests. `tools/ci-local.sh` stays as an emergency fallback for a runner that cannot run a job at all, never as a replacement for a red job. The pnpm jobs failed on the runner because pnpm 12 needs `libatomic.so.1`; the owner installs `libatomic1` there, and the workflows get no workaround. Alternatives: keep the storage job on GitHub-hosted runners (blocked by the billing lock and gone for good with a private runner) or give the runner root (rejected: a CI job must never be able to touch the host's disks).
- **D270** (medium, 2026-09-30) A disk that returns after it was pulled is reopened (`zpool online`) before a suspended pool is resumed (`zpool clear`), and both give up after 30 seconds so the pool reads go on. Seen on dev01 with `scsi_debug` disks: while ZFS still holds the deleted device, `zpool clear` waits in the kernel, and behind it `zpool destroy`, `zpool list` and even writes to ZFS module parameters wait until a reboot. The disk pull tests therefore run only with `integration.sh --disk-pulls` on a freshly booted dev VM. Alternatives: run them in every integration run (rejected: one bad run blocks ZFS on dev01 until a reboot) or drop them (rejected: they are the only automated check of the owner's procedure). Open: why the transaction group waits although the disk is back; to be traced on a fresh boot.
- **D271** (low, 2026-09-30) Downloads run through `fetch` into a Blob so the taskbar can show their progress and cancel them; the file is saved from the Blob under its name. Above 4 GB (by `Content-Length`) the browser's own download takes over, because browsers keep large Blobs on disk only up to a limit. Alternatives: a service worker that streams to disk (needs a secure context, which the untrusted default is not) or the File System Access API (secure context and Chromium only).
- **D272** (low, 2026-09-30) After loading and reloading, nothing has focus; the start button takes the focus only when the last window closes or minimizes. Keyboard users reach it with Tab. Alternative: keep focusing it on load (rejected by the owner: a focused button after every reload looks like a stuck state).
- **D273** (low, 2026-09-30) dev01 has one dev instance: `pithyx-dev.service` on port 7484 with the one database `/var/lib/pithyx/pithyx.db`, always running dev from the main checkout (`install-dev-daemon.sh` fast-forwards it to `origin/dev`; `--from` is gone) and deployed after every merge. The database is reset only with `tools/dev-vm/reset-dev-db.sh`, and `tools/dev-vm/seed-dev-accounts.sh` sets up the owner (`gabriel`, credentials and recovery key in `~gabriel/.config/pithyx-dev/owner.env`, mode 600) and the test accounts `e2e-admin` and `e2e-user` (`e2e.env`, once the users API exists) after every reset and deploy. The owner account is never changed. A fresh state or an unmerged branch runs on a short-lived instance on port 7485 in a `mktemp -d` directory (`tools/dev-vm/start.sh`). Alternatives: switching the unit between databases or checkouts (rejected: the owner could not sign in or watch while an agent worked) or a second permanent instance (rejected: two states to keep apart on a 4-core VM).
- **D274** (low, 2026-09-30) In dev environments (dev01, other dev VMs, CI) the agent needs no permission for anything: packages, services, configuration, kernel modules, reboots, pools and arrays, databases, every file including `CLAUDE.md`. The hard limits are no push or merge to `master`, no delete, force-push or rewrite of `origin/master` and `origin/dev`, and on dev01 `sda`, the worn host SSD, pve02, the home network, the runner server and secrets. `.claude/settings.json` keeps only deny rules for pushes to `master` and force-pushes or deletes of `dev`; the owner's `autoMode` settings on dev01 tell the auto-mode classifier the same. Alternatives: ask before every system change (rejected by the owner: it blocks the work and saves nothing on a throwaway VM) or keep the raw disk command denies (rejected: `testpool.sh` and the classifier cover the test disk limit, and scsi_debug disks need `wipefs` and `zpool create`).
- **D275** (low, 2026-09-30) `tools/ci-local.sh` is removed, by the owner: dev01 is too weak to build and test, so builds, lint, typecheck, unit tests, E2E and the performance gate run only on the self-hosted runner, and the agent pushes and goes on while the runner works. dev01 keeps what needs ZFS, root or real disks (`tools/dev-vm/integration.sh`, which builds the daemon for it) and the deploy of `dev`. Same day: `docs/dev/HANDOFF.md` is removed and its state, open work and pitfalls live in `CLAUDE.md`, so agents read one file. Alternatives: keep the script as an emergency fallback (rejected: it loaded dev01 for results that did not count anyway) or run only the unit tests locally (rejected: the runner does the same in minutes).
- **D276** (low, 2026-09-30) `tools/dev-vm/integration.sh` stops `pithyx-dev.service` for the test run and always starts it again (owner's choice). The dev daemon manages every pool it sees: on dev01 it created cache datasets in the throwaway test pools, held their mounts busy, and with D270 it would also reconnect and clear their pulled disks, which broke the tests and risks the ZFS wedge. Alternatives: a daemon setting that ignores pools by name prefix, set only in the dev unit (rejected: a test switch in the product) or tolerant tests (rejected: unreliable results).
- **D277** (medium, 2026-09-30) Every machine has exactly one Pithyx installation, by the owner. On dev01 that is `pithyx-dev.service` on 7484: real mode, the database on the system disk (`/var/lib/pithyx/pithyx.db`, as on a box), the pools on the test disks. `tools/dev-vm/start.sh` (a second instance on 7485) is gone, and a daemon that would fall back to `.pithyx-dev/` (no `PITHYX_DB`) on a machine with `/etc/pithyx/environment` exits with code 78; before, a `dotnet run` from the checkout ran in real mode on the real disks with a second database in `.pithyx-dev/`. Branches are checked by the runner and `integration.sh` before the merge and on 7484 after it. Simulation stays for the unit tests and E2E on the runner, each with its own throwaway database. Alternatives: branches on 7484 before the merge (rejected: a branch migration makes the dev database unreadable for `dev`, exit code 65) or keep the second instance (rejected by the owner: two states to tell apart).
- **D278** (low, 2026-09-30) The E2E suite (with the performance gate and the design checks) runs only when asked, by the owner: the label `e2e` on a pull request, a manual workflow run, and every pull request or push to `master`. It took five to eight minutes of a single runner that runs one job at a time, for every small change. Every pull request still runs `changes`, `repo` and `dco`, and `daemon` and `frontend` when their files changed; the bundle size check stays in `frontend`. The agent adds the label for larger changes (user flows, desktop shell, sign-in, setup, SDK transport, API contract, loading path, several merges in a row) and runs it by hand on `dev` before the owner verifies on 7484. Pushes to `dev` no longer run E2E. Alternatives: E2E on every push to `dev` after the merge (rejected: it still holds the only runner slot) or split the suite into a fast smoke set (possible later; the tests share one daemon and run with one worker).
- **D279** (low, 2026-09-30) The rename from the first working title to Pithyx is finished. The GitHub repository was renamed, so old URLs and `#` references redirect to `pithyx/pithyx` with the same numbers; the repository now links only to `pithyx/pithyx` (NOTICE, README, SECURITY, the About attribution, `protect-branches.sh`, the references in this file), and the repository check allows only the `VYXTEST` serials, which are set in the hypervisor. The text of older D entries stays as written. On dev01 the old `/etc` directory, cache folders, backups with old databases and build leftovers are gone. Alternatives: keep the old links (rejected: they work only as long as GitHub keeps the redirect) or rename the test disks (rejected: their serials come from the hypervisor, outside the agent's reach).
- **D280** (low, 2026-09-30) The self-hosted CI becomes several runners on the owner's host, one job each: general runners (label `pithyx-ci`) sharing some cores, and one E2E runner (label `pithyx-e2e`) on cores of its own. The E2E job runs on `vars.CI_RUNNER_E2E` and falls back to `vars.CI_RUNNER`. One runner ran every job of every pull request one after another while most of its six cores idled; the timing budgets of the performance gate need a runner that no parallel build disturbs. Alternatives: more general runners only (rejected: parallel builds next to the E2E suite make the budgets flaky) or ephemeral runners per job (later: they need a token service to register each one). Same day, to shorten the runs: the daemon job leaves out the `StorageIntegration` tests instead of starting and skipping them, the sign-off check is a step of the repo job instead of a job of its own (three heavy jobs per pull request, one per general runner), and the E2E job builds only the daemon, not the whole solution with its tests. 2026-10-01: the daemon's timing budget (`Category=TimingBudget`) moves from the daemon job to the E2E job; on a general runner next to the frontend build its first page took 350 to 590 ms against a budget of 200 ms.
