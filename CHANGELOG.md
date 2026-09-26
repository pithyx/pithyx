# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Monorepo scaffold: .NET 10 daemon with a health endpoint, React desktop, SDK and API client packages.
- Tooling: EditorConfig, ESLint, Prettier, Vitest, xUnit, lefthook hooks, repository checks and CI.
- Project files: AGPL-3.0 license (SDK under MIT), attribution notice, contributing guide, security policy.
- Dev VM provisioning script (`tools/dev-vm/provision.sh`) for Debian 13: toolchain, ZFS (DKMS), storage and container tools, `en_US.UTF-8` locale, environment and test disk markers.
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
