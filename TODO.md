# TODO

Step-by-step plan for building the Vyxis foundation. Read `CLAUDE.md` before starting and follow it at every step.

## How to use this file

- Work top to bottom. One step at a time.
- A step is finished only when every item is checked and its "Done when" criteria are met.
- `[DECISION]` means the owner must choose before you continue. Present the options with a short recommendation and stop.
- `[REVIEW]` means the owner must look at the result before you continue. Stop and ask.
- Check items off in the commit that completes them. One step usually equals one or a few commits.
- If a step turns out to be wrong or incomplete, propose a change to this file instead of silently deviating.
- Start every step in plan mode: read the relevant sections of `CLAUDE.md`, write a short plan, then implement.
- Each step and some sub-steps carry an effort level. `high` means extended thinking for design-defining work that is expensive to change later. `medium` is the default. `low` is mechanical work.

## Milestones

| Milestone               | Content                                             | Steps          |
| ----------------------- | --------------------------------------------------- | -------------- |
| M0 Foundation           | Repository, tooling, CI, dev environment            | 1, 2           |
| M1 Walking skeleton     | Daemon core, login, WebSocket, first window         | 3              |
| M2 Desktop              | Design, window manager, SDK, file manager           | 4, 5, 6        |
| M3 NAS core             | Pools, snapshots, users, shares, SSD cache          | to be detailed |
| M4 Apps                 | Manifest, containers, YARP routes, app template     | to be detailed |
| M5 Image and operations | ISO, installer, updates with rollback, self-healing | to be detailed |
| M5b Self-hosting        | Docker UI, VM MVP                                   | to be detailed |
| M6 Backup               | restic, ZFS replication                             | to be detailed |

---

## Step 1: Repository foundation

Effort: medium.

Goal: an empty but complete monorepo where every tool is configured, every check runs, and CI is green.

### 1.1 Decisions (made 2026-09-26)

- [x] Git host: GitHub, org `vyxis`, CI with GitHub Actions.
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
- [x] `tools/dev-vm/testpool.sh`: creates and destroys test pools (`vyxtest*`, `autotrim=on`) and refuses any disk whose serial is not `VYXTEST01` to `VYXTEST06` (or not listed in `/etc/vyxis/test-disks` once that file exists).
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
  - `daemon/src/Vyxis.Daemon` (ASP.NET Core, the host and all modules as folders)
  - `daemon/tests/Vyxis.Daemon.Tests` (xUnit v3)
- [x] `Directory.Build.props`: `Nullable` enabled, `ImplicitUsings` enabled, `TreatWarningsAsErrors` true, `AnalysisLevel` latest-recommended, `EnforceCodeStyleInBuild` true, deterministic builds.
- [x] `Directory.Packages.props`: central package management.
- [x] Minimal `Program.cs` that starts Kestrel on port 8994 and serves `GET /api/health` returning `{ "status": "ok" }`.
- [x] One test that calls `/api/health` through `WebApplicationFactory`.
- [x] `dotnet format --verify-no-changes` passes.

### 1.5 Frontend scaffold (pnpm workspace)

- [x] Root `package.json` with pnpm workspaces: `desktop`, `sdk`, `packages/*`. Pin the pnpm version via `packageManager`.
- [x] `.nvmrc` or `engines` pinning the Node LTS version.
- [x] `tsconfig.base.json`: `strict`, `noUncheckedIndexedAccess`, `exactOptionalPropertyTypes`, `verbatimModuleSyntax`.
- [x] `desktop/`: Vite + React + TypeScript. Replace the template content with a single empty desktop surface. Enable the React Compiler.
- [x] `sdk/`: TypeScript library built with Vite library mode, exports an empty `vyxis` object for now, emits type declarations.
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

- [ ] `tools/dev-vm/provision.sh`: idempotent script for a fresh Debian 13 VM. Installs .NET 10 SDK, Node LTS + pnpm, `zfsutils-linux` (contrib), `mdadm`, `btrfs-progs`, `smartmontools`, `samba`, `nfs-kernel-server`, `docker.io`, `qemu-system-x86`, `ovmf`. Enables contrib and non-free-firmware.
- [ ] The script reproduces exactly the toolchain installed manually on dev01 for step 1: Microsoft package source for Debian 13 (`packages-microsoft-prod.deb` from `packages.microsoft.com/config/debian/13/`) with `dotnet-sdk-10.0` 10.0.401, NodeSource `node_24.x` with `nodejs` 24.21.0, and `corepack enable` so pnpm comes from `packageManager` (pnpm 12.6.0). Versions must match `global.json`, `.nvmrc` and `package.json`.
- [ ] The script verifies it runs on Debian 13 and refuses otherwise.
- [ ] The script sets the system locale to `en_US.UTF-8` (keyboard and time zone stay as they are) so tool output and logs match production.
- [ ] The script writes `dev` to `/etc/vyxis/environment` and records the test disks by serial in `/etc/vyxis/test-disks`.
- [ ] README section "Development / Dev VM": recommended VM spec (4 vCPU, 8 GB RAM, 32 GB system disk, six 10 GB data disks), how to run the provisioning script, recommendation to snapshot the VM before destructive tests.

### 2.2 Simulation mode

- [ ] `ICommandRunner` interface with `ProcessCommandRunner` (real) and `FixtureCommandRunner` (reads recorded output from `daemon/tests/fixtures/`).
- [ ] Environment variable `VYXIS_SIMULATE=1` switches the daemon to `FixtureCommandRunner`. In simulation mode the daemon runs on Windows, macOS and Linux.
- [ ] The simulated state is visible: `GET /api/health` reports `"mode": "simulated"`.

### 2.3 Dev workflow

- [ ] Vite dev server proxies `/api` and `/ws` to the daemon. Target configurable via `VYXIS_DAEMON_URL` (default `http://localhost:8994`).
- [ ] Development runs over plain HTTP on 8994. Production HTTPS is added later. Document this clearly so nobody ships it.
- [ ] Root script `pnpm dev` starts the desktop; README documents `dotnet watch` for the daemon (locally in simulation mode or on the VM over Remote-SSH).
- [ ] README section "Development": prerequisites, both workflows (simulation on the local machine, real on the VM), common problems.

**Done when:** following the README on a clean machine shows the desktop in the browser with "Connected to daemon (simulated)" within 15 minutes.

---

## Step 3: Walking skeleton (daemon core, login, first window)

Effort: high for 3.1, 3.3, 3.4 and 3.6 (they define contracts everything else builds on), medium for the rest.

Goal: one real feature end to end, built on the final architecture. Everything later extends this.

### 3.1 Daemon host

- [ ] Serilog with JSON output to console (journald in production) and a rotating file with size cap.
- [ ] Request id middleware: accept or create a W3C `traceparent`, expose it as `requestId`, enrich every log entry.
- [ ] Secret masking in logs (passwords, tokens, cookies).
- [ ] Error system: `ErrorCodes` static class with constants, `VyxisException(code, params)`, middleware that maps it to RFC 9457 problem details with `code`, `params`, `detail`, `requestId`. Unknown exceptions map to `system.internal.unexpected`.
- [ ] OpenAPI document generated by `Microsoft.AspNetCore.OpenApi`, served at `/api/openapi.json`. Scalar UI at `/api/docs` in development only.

### 3.2 Database

- [ ] SQLite at a configurable path (default `/var/lib/vyxis/vyxis.db`, in simulation mode a local file). WAL mode, foreign keys on.
- [ ] Migration runner: numbered `.sql` files embedded as resources, a `schema_version` table, runs at startup inside a transaction, refuses to start on a failed migration.
- [ ] Migration `0001`: `users`, `sessions`.

### 3.3 Authentication

- [ ] First-run setup: if no user exists, `POST /api/setup` creates the admin. Afterwards the endpoint returns `auth.setup.already_done`.
- [ ] Passwords hashed with Argon2id. Parameters documented next to the code.
- [ ] Login creates a session stored in SQLite and sets an HttpOnly, Secure (outside dev), SameSite=Strict cookie. Logout deletes it.
- [ ] CSRF protection: SameSite=Strict plus a required custom header on state-changing requests.
- [ ] Rate limiting on login with ASP.NET Core rate limiter.
- [ ] Optional TOTP per user: enrollment with QR code, verification as a second step inside the same login card, recovery codes.
- [ ] Login and setup forms use `autocomplete="username"`, `current-password`, `new-password` and `one-time-code` so password managers autofill.
- [ ] Recovery key: generated during setup, shown once, offered as a printable page. It is the only way to reset the admin password.
- [ ] Passkeys are out of scope until the TLS decision in M5 is made. Do not design the auth flow around them.
- [ ] Error codes: `auth.credentials.invalid`, `auth.session.expired`, `auth.rate.limited`, `auth.setup.already_done`, `auth.totp.invalid`, `auth.recovery.invalid`.

### 3.4 WebSocket and topics

- [ ] `/ws` endpoint, authenticated by the session cookie.
- [ ] Message protocol: `sub`, `unsub`, `snap`, `event`, `error`, each with the request id. JSON schema in code, types generated for TS.
- [ ] Topic router with `+` and `#` wildcards, permission check per subscription, throttling per topic.
- [ ] First topic `system/info`: hostname, uptime, CPU load, memory. 1 Hz.

### 3.5 First real feature: disks

- [ ] `LsblkParser`: pure static function, parses `lsblk -J -b -O` output. Fixture tests with real output from the dev VM (at least: plain SATA, NVMe, USB disk, virtual disk).
- [ ] `SystemStateService`: holds the disk list in memory. Refreshes on udev events (listen via `udevadm monitor` as a long-running process or netlink) and at startup.
- [ ] `GET /api/disks` reads from memory. Topic `storage/disks` pushes changes.

### 3.6 Generated contracts

- [ ] TS client generated from `openapi.json` into `packages/api-client` (openapi-typescript + openapi-fetch).
- [ ] Error codes exported from C# and generated into a TS union type.
- [ ] CI drift check: regenerate and fail if the committed output differs.

### 3.7 Desktop skeleton

- [ ] i18next with ICU, `i18n/en/*.json` and `i18n/de/*.json`, language from browser with manual override.
- [ ] CI check: every key exists in `en` and `de`, every error code has an `errors.<code>` key.
- [ ] Design tokens as CSS variables with light and dark theme (placeholder values, final design in step 4).
- [ ] Setup screen (first run) and login screen, functional but unstyled.
- [ ] Minimal desktop with one fixed window that shows the disk list live via the WebSocket store.
- [ ] Error dialog: translated message from the error code, request id with a copy button.
- [ ] TanStack Query for REST, a small Zustand store for WebSocket topics.

### 3.8 Tests

- [ ] Parser fixture tests, auth flow tests (setup, login, wrong password, rate limit, logout), WebSocket subscription test.
- [ ] One Playwright E2E test: setup admin, log in, see the disk list.

**Done when:** on the dev VM you can create the admin, log in, and see the disk list in a window. Detaching a virtual disk in Proxmox updates the list without a reload. A forced error shows a translated message with a request id that can be found in the logs. All checks are green.

---

## Step 4: Design pass (login, desktop, windows)

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

- [ ] `[DECISION]` Styling approach: CSS Modules with CSS variables (default, zero runtime) or Tailwind.
- [ ] `[DECISION]` Icon set (for example Lucide or Tabler).
- [x] Typefaces (decided 2026-09-26): IBM Plex Sans for the UI, Sora for the word mark and large headings, JetBrains Mono for terminal, logs and paths. All SIL OFL 1.1.
- [ ] Ship the official font files unmodified (IBM Plex has the Reserved Font Name "Plex", so do not subset or convert them ourselves), self-hosted, preloaded, with each license file next to the fonts and listed in third-party notices.
- [ ] `[DECISION]` Headless component primitives: Radix UI or React Aria for accessible menus, dialogs and focus handling.
- [ ] Produce static mockups for: setup, login (including the TOTP step and welcome transition), empty desktop, desktop with icons and a sticky note, desktop with three windows, window states (focused, unfocused, maximized, minimized), taskbar, start menu with search, notification, error dialog, mobile layout.
- [ ] Derive final design tokens: colors (light and dark, based on the logo palette), type scale, spacing, radii, shadows, motion durations.
- [ ] `[REVIEW]` Owner approves the mockups and tokens. Iterate until approved.
- [ ] Apply the approved tokens and screens to setup and login.

**Done when:** the owner has signed off on the design and setup and login use it.

---

## Step 5: Window manager and SDK

Effort: high for 5.3, medium for the rest.

Goal: the desktop works like a desktop, and apps can be built against the SDK.

### 5.1 Window manager

- [ ] Zustand store: windows by id, open, close, focus, z-order, minimize, maximize, restore.
- [ ] Drag via pointer events: write position to the DOM during the drag, commit to the store on `pointerup`.
- [ ] While dragging or resizing, `pointer-events: none` on all window contents (iframes would swallow events).
- [ ] Resize from all edges and corners, minimum sizes from the manifest.
- [ ] Snap to left or right half and maximize by dragging to the top edge.
- [ ] Keyboard: focus cycling, close, maximize. Visible focus states.
- [ ] Every window belongs to a `screen` (always 1 for now).
- [ ] Size classes from `CLAUDE.md` (compact, medium, expanded): full-screen apps with app switcher on compact, maximized windows and split view on medium, free windows and snap zones (thirds on ultrawide) on expanded.
- [ ] SDK reports the window size class; sample apps use container queries.
- [ ] Touch: 44 px targets on coarse pointers, swipe to switch apps on compact, no hover-only actions.
- [ ] Taskbar with running apps, start menu with search, notification toasts.

### 5.2 Layout memory

- [ ] Migration: `desktop_layouts` per user and device with `expires_at`, persistent `window_preferences` per user and app, and `desktop_items` per user (shared across devices).
- [ ] Device id stored in a long-lived cookie. "Get desktop from ..." lists the user's other devices and copies that layout.
- [ ] Debounced save over the WebSocket, restore on login with validation (skip windows whose app, path or permission is gone).
- [ ] Hourly cleanup job for expired layouts.

### 5.3 App runtime and SDK

Spec first, then implementation together with the sample apps. Freeze SDK 1.0 only after the file manager (step 6) runs on it.

- [ ] Write the SDK specification as TypeScript interfaces with TSDoc: transports, handshake, versioning, error model, and the lifecycle below. `[REVIEW]`
- [ ] Lifecycle events: `launch` (with arguments, for example a file to open), `focus`, `blur`, `minimize`, `restore`, `beforeClose` (can be vetoed for unsaved changes), `beforeLogout` (last chance to save), `themeChange`, `localeChange`, `openFile` (file handler).
- [ ] Manifest fields for runtime behavior: `window.instances` (`single` or `multi`), `backend.lifecycle` (`always` or `onDemand`), `backend.idleTimeout`, `backend.resources` (memory, CPU), `backend.schedules`.
- [ ] Lifecycle events for updates and failures: `appUpdated` (reload required), `backendUnavailable`, `backendRestored`, `permissionRevoked`.
- [ ] One SDK, two transports: in-process for core apps, MessageChannel for third-party iframes. Core apps must not bypass the SDK.

- [ ] App manifest: JSON Schema as the source, C# and TS types generated, validation with clear error codes.
- [ ] App registry: core apps loaded with `import()` in the desktop context; third-party apps in iframes with `sandbox="allow-scripts allow-forms"` (no `allow-same-origin`), served from the same host.
- [ ] SDK transport: one `MessageChannel` per app instance, `init` handshake with SDK version, locale and theme tokens.
- [ ] SDK API v1: `window.setTitle`, `window.close`, `window.setSize`, `notify`, `session.save`, `session.restore`, `storage.get`, `storage.set` (daemon-backed, replaces `localStorage` in opaque-origin iframes).
- [ ] Permission broker in the desktop; app identity comes from the port.
- [ ] Two sample apps: one core app in React, one third-party app in plain TypeScript that uses only `@vyxis/sdk`.
- [ ] SDK reference generated with TypeDoc.

### 5.4 Tests and budgets

- [ ] Unit tests for the window store and the SDK RPC layer.
- [ ] Playwright: open, drag, resize, maximize, restore, close; layout survives a reload. Run at phone, tablet, laptop and ultrawide sizes with touch and mouse.
- [ ] Accessibility checks (axe) in CI; keyboard-only walkthrough of login and desktop.
- [ ] Performance budgets as CI tests: initial desktop bundle under 200 KB gzip, desktop usable after login under 1 s on repeat visit.

**Done when:** the plain TypeScript sample app opens in a window, sets its title, sends a notification and restores its state after a reload, and the owner confirms the desktop feels right. `[REVIEW]`

---

## Step 6: File manager

Effort: high.

Goal: the performance showcase. It must feel faster than DSM File Station.

- [ ] Detailed task list to be written together with the owner before starting. `[DECISION]`
- [ ] Scope: streamed listings over the WebSocket, client cache with stale-while-revalidate, inotify on open folders, prefetch on hover, virtualized list, Web Worker for sort and filter, optimistic actions, thumbnails, tus uploads, explorer keyboard controls, viewers and editor.
- [ ] Budgets as CI tests: previously visited folder under 16 ms, first chunk of a new folder under 100 ms (LAN, warm cache), 10,000 entries listed server-side under 50 ms (warm), steady 60 fps while scrolling 50,000 entries.

---

## Later milestones

M3 to M6 get their own detailed steps once step 6 is done. Write them into this file first and get them reviewed before starting. `[REVIEW]`

Known requirements for later milestones:

- M3: energy settings: spindown per pool (via hd-idle, off by default), power profiles, and a diagnostic that shows what keeps disks awake (process, file) and CPU package C-state residency.
- M3: permission model from `CLAUDE.md` (groups, users, shares, protocols, apps, special rights), pool encryption with key vault, SFTP instead of plain FTP.
- M5: web-based installer instead of a TUI. Boot from USB, open the setup page in a browser, choose the system disk, the system installs itself and continues setup in the same page. The console only shows the address.
- M5: TLS as decided in `CLAUDE.md`: self-signed by default, trust wizard (local CA download with per-OS instructions), optional own domain with Let's Encrypt DNS-01. Passkeys and the service worker switch on once trusted.
- M5: SSH toggle in settings with the logo MOTD.
