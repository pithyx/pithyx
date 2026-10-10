---
title: 'Errors, tracing and logging'
description: 'Error codes and problem details, request ids, structured logs and masking secrets.'
status: draft
audience: developer
sidebar:
  order: 3
---

Every failed API call returns an RFC 9457 problem document with a stable error code, and every request carries a request id that ties the response, the logs and the external commands together. Logs are structured JSON and stay on the box; secrets are masked before they are written.

## Error codes

A code has the form `area.object.reason`: lowercase, dots between the parts, underscores inside words, for example `fs.file.not_found`. Codes are constants in `daemon/src/Pithyx.Daemon/Errors/ErrorCodes.cs`; each one has an `[ErrorStatus]` attribute with its HTTP status. The TypeScript union is generated from them. Never rename or remove a code without a major version. The full list is in the [error reference](/docs/reference/errors/).

To add a code:

1. Add the constant with `[ErrorStatus(...)]` in `ErrorCodes.cs`.
2. Add the text to `i18n/en/errors.json` and `i18n/de/errors.json`.
3. Run `pnpm generate`.

Throw `new PithyxException(code, params, inner)` in code that has a request. Middleware writes a problem with `WriteProblemAsync(context, code)`. A status without a code falls back to a generic one: 400 `request.body.invalid` (with the invalid field names in `params.fields`), 404 `request.route.not_found`, 405 `request.method.not_allowed`, 415 `request.media_type.unsupported`, 416 `fs.range.not_satisfiable`, anything else `system.internal.unexpected`.

## Problem details

The body has the content type `application/problem+json` and these members: `type` (default `about:blank`), `title`, `status`, `detail`, `code`, `params` and `requestId`.

- `detail` is the English text of the code with its parameters filled in. It is a fallback for logs and scripts.
- The API never returns translated text. A client translates `code` with `params`; see [Translations](/docs/developers/desktop/i18n/).
- A code the daemon does not know is replaced by `system.internal.unexpected`.
- Only paths under `/api` answer bare status codes with problem details.
- Unexpected exceptions are logged as errors; expected ones (a `PithyxException`) as information.

## Request ids

The request id is the W3C trace id (32 hexadecimal characters) of the request's `Activity`, not a UUID. Every HTTP request, every WebSocket message, every job and every background refresh has one.

- It is returned in the `X-Request-Id` header and in `requestId` of a problem.
- External commands receive it as the `TRACEPARENT` environment variable. Proxied app backends receive it as the `traceparent` header.
- The desktop never shows it to users; it goes to the browser console and to the logs.
- To find everything about one failed request, search the logs for its id.

## Structured logs

The daemon logs with Serilog as compact JSON, with the rendered message, to three places:

- the console (journald on a box),
- `daemon.json` in the log directory (`/var/log/pithyx`, or `PITHYX_LOG_DIR`), rolling at 20 MiB with 10 files kept,
- the Log Center store `logs.db`.

The default level is Information; ASP.NET Core and the proxy log from Warning. Each entry has the property `RequestId`.

The Log Center keeps entries in memory (20,000 entries) and writes them in batches every second. Sources are `daemon`, `system/<unit>` (read from the journal) and `app/<id>` (app logs and container output). Defaults: 30 days retention (1 to 365 days), 4 GiB maximum size, an optional daily archive copy to an app pool. Endpoints are `GET /api/logs`, `/api/logs/sources`, `/api/logs/export` and, for administrators, `GET` and `PUT /api/logs/settings`. Nothing is sent anywhere.

App log entries are rate limited per app: a burst of 200, then 20 per second.

## Secrets

The logger replaces the value of any property whose name matches `password`, `passphrase`, `secret`, `token`, `cookie`, `authorization`, `recovery_key` or `totp` (case-insensitive, at any depth) with `***`. In text values it masks `pithyx-token=`, `pithyx-ticket=` and bearer credentials. Command arguments listed as sensitive are masked and standard input is never logged. Passwords are stored as Argon2id hashes and sessions as SHA-256.

Pithyx does not have support bundles yet.
