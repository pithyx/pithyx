# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `pnpm check:bundle` holds two bundle budgets (gzip) against `tools/checks/bundle-budget.json`: the sign-in bundle (everything `index.html` loads before the first render, at most 200 KB) and the desktop bundle that signing in adds. Each fails the build when it grows more than 5 percent over its baseline. It runs in CI after the build.
- E2E tests run with reduced motion and cover the window manager at phone, tablet, laptop and ultrawide sizes, with mouse, touch and keyboard. They check WCAG 2.2 AA with axe and fail on uncaught page errors.
- Performance budgets as an E2E test against the production build on Chrome's "Fast 4G" with a four times slower CPU (median of five fresh sign-ins): sign-in screen usable within 1.5 s from a cold cache, visible feedback within 100 ms of clicking "Sign in", desktop usable within 1 s of the click without the Argon2 time, and within 500 ms of a reload. `node tools/perf/measure.ts --url <address>` prints the same measurements, unthrottled and throttled, for any running daemon.
- Layout memory: the desktop saves its windows, stacking order, focus and taskbar position per user and device over the WebSocket (new client message `layout`, debounced by one second, flushed before sign-out and when the page goes away) and restores them after sign-in or a reload. Windows of apps that are gone are skipped with a notice. Windows are shown within the current screen but keep their saved position, so they return to it when the screen grows again. Signing out forgets every window, size and notice of the user in that tab.
- A long-lived HttpOnly cookie `vyxis_device` identifies each browser profile; it is set when a session starts and, for a browser that lost it, with the WebSocket handshake. "Get desktop from ..." in the account menu lists the user's other devices (browser and system from the User-Agent) and copies a layout to this device (`GET /api/desktop`, `GET /api/desktop/devices`, `POST /api/desktop/layout/copy`).
- Closing a window on a large screen remembers its size for the app's next window, for the user on every device (`PUT /api/desktop/preferences/{appId}`).
- Saved layouts expire 90 days after their last save; an hourly job deletes them. Layouts hold at most 50 windows and 16 KB of app data. New error codes `desktop.layout.invalid`, `desktop.layout.too_large` and `desktop.device.not_found`.
- Migration 0002 adds `desktop_layouts`, `window_preferences` and `desktop_items` (the desktop items UI follows later).
- Notification toasts for desktop events, dismissable, gone after eight seconds.
- Phones: apps run full screen; the app switcher in the taskbar shows every open app (tap to switch, close one or all), a horizontal swipe along the taskbar switches to the next or previous app, and the Android back gesture or browser back returns to the home screen without leaving the desktop, however many apps are open.
- Tablets: windows open maximized; snapping from the window menu or the keyboard puts two apps side by side.
- Windows move by their title bar and resize from every edge and corner with live content, never below the app's minimum size and always with part of the title bar reachable. Dragging to a side edge snaps to a half (thirds on workspaces at least 2400 px wide), dragging to the top edge maximizes, a snapped or maximized window gets its free size back when pulled away, and a double click on the title bar maximizes or restores. A preview shows the snap target.
- Window menu behind the app icon in the title bar (restore, move, size, minimize, maximize, snap left or right, close) with a keyboard move and size mode (arrow keys, Shift for single pixels, Enter, Escape), and the shortcuts Alt+Shift+Left/Right (snap), Alt+Shift+Up (maximize), Alt+Shift+Down (restore, then minimize), Alt+Shift+PageDown/PageUp (next or previous window) and Alt+Shift+Space (window menu). The shortcuts do not apply inside text fields, where macOS uses Option+Shift with the arrow keys to select by word. When no window is left, keyboard focus moves to the start button instead of the page. Keyboard actions are announced to screen readers.
- The taskbar can sit at the bottom, top, left or right (account menu, "Taskbar position"); phones keep it at the bottom.
- Window manager: windows open from desktop shortcuts and the start menu (search with Enter), come to the front on click, minimize and restore from the taskbar, maximize and close from the title bar. Single-instance apps focus their existing window. Windows keep their DOM position when their stacking order changes, so embedded frames never reload.
- Core apps "Disks" (the live disk list) and "About Vyxis" (license, the attribution required by `NOTICE`, third-party notices). Apps load only when their first window opens.
- Setup and sign-in use the step 4 design: one card on the wallpaper with the server name, the setup in two steps (administrator, recovery key in two lines with copy and print), the TOTP step in the same card, a recovery code fallback, "Forgot password?" with the recovery key reset that shows the new key once, and a welcome card that stays only until the desktop's live connection is up. A language select below the card switches between automatic, English and German. Submit buttons show a spinner and their progress label ("Signing in ...") on the click and ignore further presses; after a failed submit the first invalid field gets focus and the errors are announced to screen readers.
- `GET /api/setup` returns the server's `hostname`, shown on the setup and sign-in screens.
- Fast sign-in: the daemon serves the desktop build precompressed (Brotli, gzip), hashed assets are cached as immutable. The sign-in screen is one small chunk with native form controls (138 KB gzip instead of 193 KB); it prefetches the desktop chunk while it is open, and the WebSocket opens while that chunk loads. A reload of a signed-in browser shows the desktop right away from a local session hint while the daemon confirms the session, and requests for the setup state and the session start from `index.html` before the scripts run.
- Requests that check a password answer with `Server-Timing: argon2;dur=<ms>`.
- After sign-in the desktop shows the wallpaper, the taskbar with clock and account menu (two-factor authentication, language, sign out, daemon status). The desktop code loads only after sign-in.
- React Aria components follow the language picked in Vyxis, and the build keeps only their English and German strings (`@react-aria/optimize-locales-plugin`).
- End-to-end tests cover setup, recovery key confirmation, TOTP sign-in, the password reset with the recovery key, the language select and phone sign-in.
- Design tokens for the desktop (`desktop/src/theme/`): colors defined once for light and dark with `light-dark()`, seven accent presets with petrol as default, type scale, spacing, radii, elevation and motion. A Vitest check enforces WCAG AA contrast for every preset in both themes.
- Self-hosted fonts shipped unmodified from their official releases (IBM Plex Sans, Sora, JetBrains Mono) with their licenses, fetched and verified by `tools/fonts/fetch-fonts.sh`, and `desktop/public/third-party-notices.json`.
- UI primitives on React Aria Components with CSS Modules and Tabler icons, and presentational desktop shell components (wallpaper, taskbar, start menu, windows, desktop icons, sticky notes, notifications, app switcher, sign-in layout).
- Generated CSS Module class name types (`desktop/scripts/css-module-types.ts`), so an unknown class name is a type error.
- Developer-only design gallery (`pnpm design`, `design.html` on port 5174) with the step 4 mockups at phone, tablet and laptop width in light and dark. It is not part of the production build.
- `pnpm design:test` checks every design gallery screen at phone, tablet and laptop width in English and German for horizontal overflow (document scroll width, elements past the right edge, sideways scrolling containers) and for window title bars covered by desktop items such as sticky notes. It runs in CI.
- `pnpm design:shots` renders every design gallery screen at phone, tablet and laptop size in light and dark, in English and German, as full-size PNGs into `tools/design-shots/out/` (basis for visual regression tests).
- Monorepo scaffold: .NET 10 daemon with a health endpoint, React desktop, SDK and API client packages.
- Tooling: EditorConfig, ESLint, Prettier, Vitest, xUnit, lefthook hooks, repository checks and CI.
- Project files: AGPL-3.0 license (SDK under MIT), attribution notice, contributing guide, security policy.
- Dev VM provisioning script (`tools/dev-vm/provision.sh`) for Debian 13: toolchain, ZFS (DKMS), storage and container tools, `en_US.UTF-8` locale, environment and test disk markers.
- `tools/dev-vm/testpool.sh wipe <serial>...` clears ZFS labels and partitions from test disks after `destroy`; it refuses disks that are not test disks or belong to an imported pool.
- Shell scripts are checked with ShellCheck in the pre-commit hook and CI.
- Daemon command runner (`ICommandRunner`) that always runs tools with `LC_ALL=C.UTF-8`, logs every call and kills commands on timeout.
- Simulation mode (`VYXIS_SIMULATE=1`): the daemon answers commands from recorded fixtures (`tools/fixtures/record.sh`). `GET /api/health` reports `mode` (`real` or `simulated`).
- Desktop shows the daemon connection status, translated into English and German (i18next with ICU, TanStack Query).
- Vite dev server proxies `/api` and `/ws` to the daemon (`VYXIS_DAEMON_URL`), and `pnpm dev` starts the desktop.
- The pre-commit hook caches ESLint and Prettier results and lints staged files without type information, so a typical commit stays under 5 s. `pnpm lint` and CI run the full type-checked rules.
- README section "Development": simulation and dev VM workflows, test pools, common problems.
- Daemon logs are structured JSON (Serilog) on stdout and in a size-capped rotating file. Every entry carries the request id; passwords, tokens, cookies and other secrets are masked, including sensitive command arguments.
- Every API response carries an `X-Request-Id` header (the W3C trace id; an incoming `traceparent` is honored), and external commands receive it as `TRACEPARENT`.
- API errors are RFC 9457 problem details with a stable `code`, `params`, an English `detail` and the `requestId`. Error texts live in `i18n/{en,de}/errors.json`.
- OpenAPI document at `/api/openapi.json` and, in development, an API reference at `/api/docs`.
- Generated TypeScript API client (`@vyxis/api-client`) with the error code union, CSRF header middleware and `VyxisApiError`; `pnpm generate` and a drift check keep it in sync with the daemon.
- Problem details for bare status codes (404, 405) apply only under `/api/`; other paths are left to the desktop.
- Golden test (`tools/i18n/icu-golden.json`) that renders every error message and formatter edge case with the daemon's and the desktop's ICU implementation and requires identical output.
- SQLite database (WAL, foreign keys) at `/var/lib/vyxis/vyxis.db` (`VYXIS_DB`). Numbered SQL migrations run at startup, each in its own transaction, after a `VACUUM INTO` backup of an existing database; the daemon refuses to start if a migration fails or the database is newer than the daemon.
- First-run setup (`/api/setup`) creates the administrator and a recovery key that is shown once. User names must be valid Linux user names and are stored in lower case; an optional display name may contain any characters.
- Login with Argon2id password hashes (libsodium), session cookies (HttpOnly, SameSite=Strict, 7 days idle, 30 days at most), logout and `/api/auth/session`.
- Optional TOTP two-factor authentication with ten single-use recovery codes, as a second login step.
- Password reset only with the recovery key; it ends all sessions, turns TOTP off and issues a new recovery key.
- New passwords are checked against an offline list of the most used passwords from breach data (`auth.password.too_common`); there are no composition rules.
- German texts use the informal "du".
- Failed login, TOTP and recovery attempts are rate limited (`auth.rate.limited` with `Retry-After`).
- State-changing API requests require the `X-Vyxis-Request: 1` header (CSRF protection); the generated client sends it.
- Every API endpoint requires a session unless it is explicitly public.
- WebSocket at `/ws` for server-pushed data: `sub`/`unsub` with MQTT-style `+` and `#` wildcards, `ack`, `snap` (current state) and `event` (changes) messages with request ids, per-topic permission checks and throttling. Only same-origin connections with a valid session are accepted; logout and password reset close them with code 4401.
- Fixed table of WebSocket close codes shared by daemon and client: 1001 daemon restart and 1008 client too slow (reconnect with backoff and restore subscriptions), 4401 session ended (go to login, also when the session expires while connected) and 4403 origin or permission rejected (no reconnect). Rejections are sent as close codes after the handshake so browsers can tell them from network errors. An open WebSocket keeps its session alive up to the absolute 30-day limit.
- Topic `system/info` (host name, uptime, load, CPU, memory from `/proc`), published once per second only while someone is subscribed.
- Desktop walking skeleton: first-run setup with the one-time recovery key (copy and print), sign-in with an optional TOTP step or recovery code, a desktop with a live "Disks" window, two-factor setup with a QR code, sign-out, a language switch (automatic, English, German) and an error dialog that shows the translated message and the request id with a copy button.
- The desktop keeps one WebSocket per tab, restores subscriptions after reconnecting with backoff and returns to the login screen when the session ends.
- The daemon serves the built desktop with `index.html` as the fallback for every path outside `/api/` and `/ws`; hashed assets are cached forever.
- End-to-end tests with Playwright (setup, disks, sign-out, wrong password with request id, German interface, TOTP), run in CI.
- Disk list: `GET /api/disks` and the topic `storage/disks` (snapshot plus added, changed and removed disks). The daemon reads `lsblk` at startup and after udev block device events, never on a request and never by polling.
- `ICommandRunner` can stream the lines of long-running commands such as `udevadm monitor`; simulation replays recorded streams.
- `tools/fixtures/record.sh` replaces serial numbers and WWNs in recorded JSON with stable pseudonyms.
- The generated client includes the WebSocket message types and the data type of every topic.
