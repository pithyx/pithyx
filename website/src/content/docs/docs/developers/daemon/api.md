---
title: 'API'
description: 'The REST and WebSocket API, authentication, topics and the generated client.'
status: ready
audience: developer
sidebar:
  order: 2
---

The daemon exposes a REST API under `/api` and one WebSocket at `/ws`. Both belong to the same session: you sign in with a cookie, send a CSRF header on every write, and subscribe to topics for live data. The OpenAPI document is generated from the code and is the source of the TypeScript client.

## REST

The OpenAPI document is `packages/api-client/openapi.json` ("Pithyx API", version `v1`). A running daemon serves it anonymously at `GET /api/openapi.json`. There is no `/api/v1` prefix. The endpoints are grouped by tag: App API, Apps, Auth, Desktop, FileServices, Files, Installer, Jobs, Logs, Notifications, Permissions, Setup, Shares, Storage, Store, System and Users. See the [API reference](/docs/reference/api/).

Not part of the OpenAPI document: the WebSocket at `/ws`, WebDAV at `/webdav/`, the app frontends and backend proxy under `/apps/<id>/` and the desktop's static files.

Conventions:

- JSON uses camelCase, enums are strings and all properties are always present (nullable ones as `null`).
- Dates are ISO 8601 in UTC.
- Every response has an `X-Request-Id` header. Errors are RFC 9457 problem details; see [Errors, tracing and logging](/docs/developers/daemon/errors-and-logging/).
- `GET /api/health` is anonymous and returns `{ "status": "ok", "mode": ... }`.
- Layout and desktop item updates use optimistic concurrency with `If-Match`.
- Statistics topics are throttled to 1 Hz.

Apps use a separate surface, `/api/app/**`, which accepts only app host tokens, never the session cookie. See [Apps overview](/docs/developers/apps/overview/).

## Authentication and CSRF

1. `POST /api/auth/login` returns the user, or a two-factor challenge that is valid for 5 minutes.
2. `POST /api/auth/login/totp` completes the challenge with an authenticator code or a recovery code.
3. The response sets the cookie `pithyx_session` (HttpOnly, `SameSite=Strict`, path `/`). The `Secure` flag is set only when the request itself is HTTPS, because plain HTTP by address must work.

The server stores only the SHA-256 of the session token. A session expires after 7 days without use or 30 days in total. An open WebSocket keeps it alive and is checked hourly.

Every request under `/api` that is not `GET`, `HEAD` or `OPTIONS` must carry the header `X-Pithyx-Request: 1`, including the login. Without it the daemon answers with [`auth.csrf.header_missing`](/docs/reference/errors/#auth.csrf.header_missing). An expired or missing session gives 401 [`auth.session.expired`](/docs/reference/errors/#auth.session.expired).

Only failed sign-in attempts count against the rate limit: 5 per client address and user name and 30 per client address in a fixed 5-minute window. The answer is 429 [`auth.rate.limited`](/docs/reference/errors/#auth.rate.limited) with a `Retry-After` header. The same limiter protects the file protocols.

Passwords use Argon2id (64 MiB, 3 passes) and need at least 8 characters. Pithyx has no personal access tokens for scripts; a script signs in like the desktop does. Passkeys are not built.

## WebSocket and topics

`GET /ws` upgrades an authenticated request. A browser's `Origin` must match the host, otherwise the daemon closes with 4403; clients without an `Origin` header are accepted.

Client messages are `sub` and `unsub` with an id and a topic. The server answers with `ack`, then `snap` (the full state, once) and `event` (changes), or `error` with a code. The largest client message is 16 KB. A client that cannot keep up (outbox of 256 messages) is closed with 1008.

| Close code | Meaning          | Client action        |
| ---------- | ---------------- | -------------------- |
| 1000       | Normal closure   | none                 |
| 1001       | Daemon restart   | reconnect            |
| 1008       | Client too slow  | reconnect            |
| 1009       | Message too big  | reconnect            |
| 4401       | Session ended    | show the sign-in     |
| 4403       | Forbidden        | stop                 |

Topic patterns may use `+` for one level and `#` for the rest. Permissions are checked for every snapshot and event. Topics today: `system/info`, `system/resources`, `system/processes`, `system/network`, `system/updates`, `system/ups`, `storage/disks`, `storage/pools`, `storage/alerts`, `storage/snapshots`, `shares/list`, `jobs`, `jobs/+`, `notifications`, `session/permissions`, `files/+/+`, `thumbnails/+/+`, `apps`, `apps/admin`, `apps/+/context` and `store/admin`.

## Generated client

The package `@pithyx/api-client` (private, not published) is generated from the OpenAPI document. Building the daemon writes `openapi.json`; `pnpm generate` then regenerates `src/generated/schema.ts`, `error-codes.ts` and `close-codes.ts` and the SDK's manifest types. Never edit generated files; on a merge conflict take one side and regenerate. `pnpm check:repo` fails when they are stale.

The client wraps `openapi-fetch`, adds the CSRF header and throws `PithyxApiError` (status, code, params, request id).

## Stability

The REST and WebSocket API, the SDK API, the manifest schema and the error codes are stable contracts: a breaking change needs a major version. They follow SemVer from 1.0.0 on.
