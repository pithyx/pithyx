# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

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
- Placeholder design tokens (light and dark) as CSS variables.
- End-to-end tests with Playwright (setup, disks, sign-out, wrong password with request id, German interface, TOTP), run in CI.
- Disk list: `GET /api/disks` and the topic `storage/disks` (snapshot plus added, changed and removed disks). The daemon reads `lsblk` at startup and after udev block device events, never on a request and never by polling.
- `ICommandRunner` can stream the lines of long-running commands such as `udevadm monitor`; simulation replays recorded streams.
- `tools/fixtures/record.sh` replaces serial numbers and WWNs in recorded JSON with stable pseudonyms.
- The generated client includes the WebSocket message types and the data type of every topic.
