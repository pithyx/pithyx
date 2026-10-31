---
title: 'Logging and errors'
description: 'Write to the Log Center with app.log, handle PithyxError codes, retry what may pass and show errors in the user language.'
status: ready
audience: developer
sidebar:
  order: 6
---

`app.log` writes the app's log to the Log Center, where administrators see it next to the system's logs, and to the browser console. Every failed SDK call rejects with a `PithyxError` that carries a stable code (`area.object.reason`), its `params`, an English `detail` and, when the daemon was involved, a `requestId`. Decide on the code, never on the text: retry what may pass by itself, and show the rest in the user's language.

## Example

<!-- example: sdk/logging-and-errors.ts -->

```ts
// Logging to the Log Center, telling retryable failures from final ones, and showing errors in the user's language.
import { isProblem, PithyxError, type PithyxApp } from '@pithyx/sdk';
import { createI18n, type I18n } from '@pithyx/sdk/i18n';

/** Failures that may pass by themselves: the desktop or the box was busy, restarting or starting the backend. */
const RETRYABLE = new Set([
  'sdk.request.timeout',
  'sdk.backend.unreachable',
  'sdk.box.unreachable',
  'app.backend.unavailable',
  'auth.rate.limited',
]);

function codeOf(error: unknown): string {
  return error instanceof PithyxError || isProblem(error) ? error.code : 'unknown';
}

function wait(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/** Runs `work` up to `attempts` times, waiting 1, 2, 4 ... seconds (or what the box asks for) between tries. */
export async function withRetry<T>(
  app: PithyxApp,
  work: () => Promise<T>,
  attempts = 4,
): Promise<T> {
  for (let attempt = 1; ; attempt++) {
    try {
      return await work();
    } catch (error) {
      const code = codeOf(error);
      if (!RETRYABLE.has(code) || attempt >= attempts) {
        throw error;
      }
      const after = error instanceof PithyxError ? Number(error.params.retryAfterSeconds) : NaN;
      const delay = Number.isFinite(after) ? after * 1000 : 1000 * 2 ** (attempt - 1);
      app.log.debug('Retrying', { code, attempt, delay });
      await wait(delay);
    }
  }
}

/** A backend answer as a value, or its problem as a PithyxError, so withRetry sees the 503 of a stopped backend. */
export async function backendJson<T>(app: PithyxApp, path: string): Promise<T> {
  const response = await app.backend.fetch(path);
  if (response.ok) {
    return (await response.json()) as T;
  }
  const body: unknown = await response.json().catch(() => null);
  throw new PithyxError(
    isProblem(body)
      ? body
      : { code: 'app.backend.unavailable', params: {}, detail: `HTTP ${String(response.status)}` },
  );
}

// The app's own texts for the codes it expects, nested by the dots of the code; a sandboxed app has no access to
// the desktop's texts.
const i18n: (app: PithyxApp) => I18n = (app) =>
  createI18n(app, {
    en: {
      errors: {
        app: {
          backend: {
            unavailable: 'The service is starting or not running. Try again in a moment.',
          },
          permission: { denied: 'An administrator has not allowed this for the app.' },
          storage: { too_large: 'Too much data to save (limit {maxKb} KB).' },
        },
        sdk: { backend: { unreachable: 'The box cannot be reached. Check the connection.' } },
        unknown: 'Something went wrong.',
      },
    },
  });

/** Logs a failure with its code and request id, and shows it: the desktop's dialog where it exists, else own text. */
export async function report(
  app: PithyxApp,
  error: unknown,
  show: (text: string) => void,
): Promise<void> {
  const code = codeOf(error);
  const requestId = error instanceof PithyxError ? (error.requestId ?? null) : null;
  app.log.error('Action failed', { code, requestId });
  if (app.supports('ui.showError')) {
    await app.ui.showError(error);
    return;
  }
  const t = i18n(app);
  const params = error instanceof PithyxError ? error.params : {};
  // t() returns the key itself for a code without a text.
  const text = t.t(`errors.${code}`, params);
  show(text === `errors.${code}` ? t.t('errors.unknown') : text);
}

export async function loadDashboard(app: PithyxApp, show: (text: string) => void) {
  try {
    const stats = await withRetry(app, () => backendJson<{ files: number }>(app, '/stats'));
    app.log.info('Dashboard loaded', { files: stats.files });
  } catch (error) {
    await report(app, error, show);
  }
}
```

## Logging

- `app.log.debug`, `info`, `warn`, `error` and `log` (the same as `info`) take a message and optional `data`, a flat object of strings, numbers, booleans and `null`. They return at once and never throw.
- Every entry appears in the browser console at its level. `info`, `warn` and `error` also go to the daemon; `debug` only while an administrator switched on debug logging for the app (`context.logLevel` is `debug`, `logLevelChange` follows it).
- In the Log Center entries appear under the source `app/<id>` with the signed-in user. Output of the backend's containers lands in the same source. A line on stdout is info, a line on stderr a warning, and a line that is a JSON object with a string `level` (`debug` or `trace`, `info`, `warn` or `warning`, `error`, `fatal` or `critical`) keeps that level. Such a JSON line shows its `message` (or `msg`) as the message and its other fields (at most 32, each cut to 1024 characters) as data; a W3C `traceparent` field ties it to the request, so searching the request id finds it next to the daemon's lines. A `debug` line is kept only while an administrator turned debug messages on for the app. So log errors of a backend to stderr or as JSON with a `level`; plain `console.log` output is always info.
- The SDK collects entries for 250 ms and sends up to 100 at a time.

**Limits:** a message is cut at 4000 characters; `data` keeps 20 fields with keys of up to 100 and values of up to 1000 characters, all turned into text. The SDK buffers at most 500 entries and drops newer ones while it is full; the daemon accepts bursts of 200 and then 20 per second for each app, all its users together, drops the rest and reports the dropped count once a minute as one warning. Never log passwords, tokens or file contents.

## Errors

`PithyxError` has `code`, `params`, `detail` and `requestId`, and `toProblem()` for the plain object. `isProblem(value)` tests any value for the shape. Codes come from two places:

- **The daemon**, unchanged: for example `app.permission.denied`, `app.storage.too_large`, `fs.entry.exists`, `app.token.invalid`. The full list with texts is the [error reference](/docs/reference/errors/).
- **The SDK** for failures between the app and the desktop: [`sdk.method.unknown`](/docs/reference/errors/#sdk.method.unknown), [`sdk.request.invalid`](/docs/reference/errors/#sdk.request.invalid), [`sdk.request.timeout`](/docs/reference/errors/#sdk.request.timeout), [`sdk.transport.closed`](/docs/reference/errors/#sdk.transport.closed), [`sdk.protocol.unsupported`](/docs/reference/errors/#sdk.protocol.unsupported), [`sdk.transfer.cancelled`](/docs/reference/errors/#sdk.transfer.cancelled), [`sdk.backend.endpoint_unknown`](/docs/reference/errors/#sdk.backend.endpoint_unknown), [`sdk.backend.unreachable`](/docs/reference/errors/#sdk.backend.unreachable) and [`sdk.box.unreachable`](/docs/reference/errors/#sdk.box.unreachable). The list is exported as `sdkErrorCodes`.

`app.backend.fetch` resolves for every HTTP status. The proxy's own failures (401, 503) carry a problem body with a code; turn them into a `PithyxError` as the example does. What your backend returns is up to you.

## Retry or not

| Code                                                                         | Meaning                                                                          | Retry                                         |
| ---------------------------------------------------------------------------- | -------------------------------------------------------------------------------- | --------------------------------------------- |
| [`sdk.request.timeout`](/docs/reference/errors/#sdk.request.timeout)         | the desktop did not answer within 15 seconds (not for transfers and dialogs)     | a few times, with a growing pause             |
| [`sdk.backend.unreachable`](/docs/reference/errors/#sdk.backend.unreachable) | the request did not reach the box                                                | after `connectionChange` with `online: true`  |
| [`sdk.box.unreachable`](/docs/reference/errors/#sdk.box.unreachable)         | a call the desktop passes on to the box (files, settings) did not reach it       | after `connectionChange` with `online: true`  |
| [`app.backend.unavailable`](/docs/reference/errors/#app.backend.unavailable) | the backend is updating, failed, or did not start in 30 s                        | when `backendStatus` says `running`, or later |
| [`auth.rate.limited`](/docs/reference/errors/#auth.rate.limited)             | too many tokens at once                                                          | after `params.retryAfterSeconds`              |
| [`app.token.invalid`](/docs/reference/errors/#app.token.invalid)             | session ended or access to the app gone (the SDK already tried a new token once) | no; the user reopens the window               |
| [`app.permission.denied`](/docs/reference/errors/#app.permission.denied)     | the app lacks `params.permission`                                                | no; wait for `permissionsChange`              |
| [`sdk.transport.closed`](/docs/reference/errors/#sdk.transport.closed)       | no desktop connected, or the connection to it is gone                            | no                                            |
| [`sdk.method.unknown`](/docs/reference/errors/#sdk.method.unknown)           | the desktop does not serve this call                                             | no; check `app.supports` first                |
| [`sdk.request.invalid`](/docs/reference/errors/#sdk.request.invalid)         | the parameters are wrong, for example a key over 128 characters                  | no; a bug in the app                          |
| `fs.*`, `app.storage.*`                                                      | the request itself is refused                                                    | no; tell the user                             |

A timed-out call may still have happened on the box. Retry only calls that are safe to repeat, such as reads and `upload` with `overwrite`.

## Showing errors

The app translates the codes it expects with its own texts, using `params` for the placeholders, and falls back to a general message. `detail` is English and meant for logs. Log the `requestId` instead of showing it: it lets an administrator find the request in the Log Center.

`app.ui.showError(error)` shows the desktop's own error dialog with the translated text. The desktop translates the daemon's and the SDK's codes; for a code of your own it shows the error's `detail`, so give it one in the user's language (`new PithyxError({ code: 'lists.list.full', params: {}, detail: t('errors.listFull') })`). The request id goes to the browser console, never into the dialog. Check `app.supports('ui.showError')` as the example does: an older desktop may not serve it and answers [`sdk.method.unknown`](/docs/reference/errors/#sdk.method.unknown). The mock desktop of `pithyx dev` serves it and writes the code to its log.

## Edge cases

- `app.log` before `connect()` resolved is not possible: there is no `app` yet. Use `console` until then.
- Entries reach the daemon only if the desktop serves `log.write`; otherwise they stay in the console.
- `connect()` itself rejects with `sdk.transport.closed` when no desktop connects within 10 seconds, for example when the page is opened directly in a tab, and with `sdk.protocol.unsupported` when the desktop does not speak the SDK's protocol.
