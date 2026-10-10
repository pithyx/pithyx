---
title: '@pithyx/sdk'
description: 'The app API: identity, files, storage, the backend, events, logs and permissions.'
status: draft
audience: developer
sidebar:
  order: 2
---

`@pithyx/sdk` is the API an app uses to talk to the desktop: `connect()` returns a `PithyxApp` object with the user and window context, notifications, per-user storage, shared folders, the backend, events and logs. It works in any framework. Every method is permission-checked by the daemon, and every failure rejects with a `PithyxError` that carries a stable code.

## Getting started

```ts
import { connect } from '@pithyx/sdk';

const app = await connect();
console.log(app.context.user.displayName);
```

`connect()` waits for the desktop's `pithyx:connect` message with one `MessagePort` from `window.parent`. If nothing arrives within 10 seconds (for example because the page was opened outside the desktop), it rejects with `sdk.transport.closed`. Options: `themeRoot` (the element that receives theme properties, `null` to skip), `timeout` (call limit in milliseconds) and `origin` (the box address used for backend URLs).

- **Theme.** The SDK sets the CSS custom properties `--pithyx-color-background`, `-surface`, `-surface-raised`, `-hover`, `-selected`, `-text`, `-text-muted`, `-border`, `-accent`, `-accent-text`, `-focus`, `-success`, `-warning`, `-danger`, plus `--pithyx-font-ui`, `--pithyx-font-mono`, `--pithyx-font-size`, `--pithyx-radius`, `--pithyx-space` and `--pithyx-control-height`. It also sets `color-scheme`, `data-pithyx-theme` and `data-pithyx-density` and follows theme changes.
- **Timeouts.** A call fails with `sdk.request.timeout` after 15 seconds; `files.upload` has no time limit.
- **Constants.** `SDK_VERSION`, `PROTOCOL_VERSION` and `MIN_PROTOCOL_VERSION`.
- `app.supports(method)` tells whether the desktop knows a method. `app.on(event, handler)` returns an unsubscribe function.

## Guides

- [Files and app storage](/docs/sdk/guides/files-and-storage/)
- [Calling the backend](/docs/sdk/guides/backend/)
- [Events, window context and launch arguments](/docs/sdk/guides/events-and-context/)
- [Permissions and permissionsChange](/docs/sdk/guides/permissions/)
- [Languages and theming in an app](/docs/sdk/guides/i18n-and-theming/)
- [Logging and errors](/docs/sdk/guides/logging-and-errors/)
- [Testing with pithyx dev and the mock desktop](/docs/sdk/guides/testing/)

## App and user

`app.context` is kept current by events and holds the app (`id`, `version`), the window instance, the user (`id`, `name`, `displayName`, `groups`, `isAdmin`, `twoFactor`), `language`, `locale` (for example `de-AT`), `timeZone`, `theme`, `sizeClass` (`compact` below 600 px, `medium` up to 1023 px, `expanded`), launch arguments, `permissions`, `capabilities`, the box (`name`, `version`), `visible`, `online`, `logLevel`, the backend status and endpoints, and the state of each storage request.

- `app.window`: `setTitle` (up to 200 characters), `close`, `setSize`, `setDirty(true)` (asks the app before closing) and `requestAttention`. They act on the app's own window only.
- `app.notify({ title, body?, level?, actions?, data?, tag? })` needs `notifications`. The title is 1 to 200 characters, the body up to 1000, at most 2 actions. Returns `{ id }`.
- `app.setBadge(count | null)`, `app.openUrl(url)` (http, https or mailto, in a new tab), `app.clipboard.writeText`, `app.contextMenu.show` and `app.ui.showError(error)`.
- `app.directory.users()` and `groups()` need `directory.read`.
- `app.session.save(state)` and `restore()` keep up to 16 KB of JSON across reloads and move with the desktop layout.
- `app.format` has `date`, `time`, `dateTime`, `relative`, `number`, `bytes` and `list`, following the user's locale and time zone.
- `@pithyx/sdk/i18n`: `createI18n(app, { en, de, ... })` returns `{ language, t(key, params) }` with ICU messages. It picks the exact locale, then the base language, then `en`.

## Files and storage

**Per-user storage.** `app.storage.get(key)`, `set(key, value)`, `remove(key)` and `keys()`. Keys are up to 128 characters, values up to 64 KB of JSON, and each user gets 1 MB per app. This replaces `localStorage`, which a sandboxed app cannot use. Errors: [`app.storage.key_invalid`](/docs/reference/errors/#app.storage.key_invalid), [`app.storage.too_large`](/docs/reference/errors/#app.storage.too_large).

**Handed-over files** (`files.read` and `files.write`): `app.files.pick({ multiple?, extensions? })`, `saveAs(suggestedName, blob)`, `read(ref)`, `write(ref, blob)` and `url(ref)` (valid for 5 minutes). A `FileRef` has `ref`, `name`, `type`, `size`, `modified` and `writable`. Picking and saving must follow a user action.

**Folders** (`FileLocation` is `{ storage: id }` for a manifest storage request or `{ share: id }` for a shared folder, which needs `files.shares`): `shares()`, `list(location, path, { cursor, limit, sort, order })` (default 100 entries, at most 1000), `watch`, `upload`, `download`, `createFolder`, `rename`, `run(operation)` for copy, move and delete as a job (delete goes to the recycle bin unless `permanent`), `versions` and `restoreVersion`. Uploading over an existing name fails with `fs.entry.exists`. `app.trash` lists, restores, deletes and empties the recycle bin, and `app.jobs.cancel(id)` cancels a job.

## Backend

- `app.backend.fetch(path, init)` is `fetch` with the bearer token added; it resolves with any status and rejects with `sdk.backend.unreachable` when the box cannot be reached.
- `app.backend.url(path)` returns a URL with `?pithyx-token=`.
- `app.backend.socket(path)` opens a WebSocket with a one-time ticket (`wss` on HTTPS, `ws` on HTTP).
- `app.backend.token()` returns the token and its expiry.
- Each accepts `{ endpoint: 'name' }`; the default is `api`. An unknown endpoint is `sdk.backend.endpoint_unknown`. Endpoint names in the SDK may be longer than the 32 characters the box accepts in a manifest; use up to 32.
- `app.context.backend.status` and the `backendStatus` event report `stopped`, `starting`, `running`, `updating` or `unavailable`.

See [Backends](/docs/developers/apps/backend/) for routes and headers.

## Events

Subscribe with `app.on(name, handler)`: `launch`, `focus`, `blur`, `minimize`, `restore`, `themeChange`, `localeChange`, `sizeClassChange`, `openFile`, `appUpdated` (reload to use the new version), `visibilityChange`, `connectionChange`, `backendStatus`, `permissionsChange` (the complete list), `storageChange`, `notificationAction`, `logLevelChange`, `sharesChanged`, `folderChanged`, `uploadProgress` and `jobChanged`.

Answer queries with `app.answer(query, handler)`:

- `beforeClose` returns `{ allow: boolean }`. It is asked only while `setDirty(true)`; the desktop waits 5 seconds and then offers to close anyway.
- `beforeUnload` (reason `logout`, `shutdown`, `restart` or `update`) cannot be refused and gets 2 seconds.

## Logs

`app.log.debug`, `info`, `warn`, `error` and `log(message, data?)` never throw. Every entry also goes to the browser console. `info`, `warn` and `error` go to the daemon; `debug` only while an administrator switched debug logging on for the app (`context.logLevel`). Entries appear in the Log Center under the source `app/<id>` with the signed-in user.

A message is cut at 4000 characters; `data` holds up to 20 fields with keys up to 100 and values up to 1000 characters. Entries are batched and rate limited (a burst of 200, then 20 per second; the rest is dropped). Never log secrets.

## Errors

Failed calls reject with `PithyxError` (`code`, `params`, `detail`, `requestId`). `isProblem(value)` tests for the shape. Daemon problems pass through unchanged. SDK transport codes are `sdk.method.unknown`, `sdk.request.invalid`, `sdk.request.timeout`, `sdk.transport.closed`, `sdk.protocol.unsupported`, `sdk.transfer.cancelled`, `sdk.backend.endpoint_unknown` and `sdk.backend.unreachable`. Apps translate codes themselves; `detail` is an English fallback. Common daemon codes are `app.permission.denied`, `app.permission.not_requested`, `app.token.invalid`, `app.backend.unavailable` and the `fs.*` codes. See the [error reference](/docs/reference/errors/).
