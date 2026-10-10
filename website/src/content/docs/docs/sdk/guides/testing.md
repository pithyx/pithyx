---
title: 'Testing with pithyx dev and the mock desktop'
description: 'Run an app in the mock desktop of pithyx dev, change user, theme, permissions and events from its panel, and unit-test app code against a fake desktop.'
status: ready
audience: developer
sidebar:
  order: 7
---

There is no development mode on the box: you develop and test on your own machine. `pithyx dev` (or `vite` with the Pithyx plugin) runs a mock desktop that opens your frontend in the box's sandbox with the box's headers, speaks the real SDK protocol and proxies your backend with the same headers and tokens as the box. Its panel switches the user, theme, language, permissions and backend state and sends any event. For code without a browser, the SDK's in-process transport connects your app code to a fake desktop in a unit test.

## Run the mock desktop

```sh
npx pithyx dev                                       # the built frontend in dist/, port 7400
npx pithyx dev --endpoint api=http://localhost:8080  # your own backend server instead of Docker
npx pithyx dev --no-docker                           # no backend; its endpoints answer 503
```

Open `http://127.0.0.1:7400/`. After a change, rebuild the frontend and reload; with the Vite plugin, `vite` serves the same mock desktop with hot reload. All options are on the [CLI page](/docs/sdk/cli/#pithyx-dev).

## What the panel changes

- **User:** alice (administrator, group family), bob (groups family and office) and carol (no groups). Switching asks the app `beforeUnload` with `logout`, ends the old user's tokens and opens the window afresh.
- **Theme, accent, locale and time zone:** sends `themeChange` or `localeChange`. Locales are `en-US`, `en-GB`, `de-DE`, `de-AT`, `fr-FR` and `ja-JP`.
- **Size class:** `compact`, `medium` or `expanded`, with `sizeClassChange`.
- **Online and debug log:** `connectionChange` and `logLevelChange`.
- **Backend status and storage states:** `backendStatus` and `storageChange`.
- **Permissions:** one switch per permission of the manifest, each sends `permissionsChange`.
- **Window:** close (asks `beforeClose`), reload, `focus`, `blur`, open a file (`openFile`), the four `beforeUnload` reasons, and minimize from the title bar.
- **Events:** every event with editable sample data, to send what the box would.
- **Log:** method calls, events, notifications and your `app.log` entries (`debug` only while debug logging is on).

## Where the mock differs from the box

| Area           | Mock desktop                                                                             | Box                                                                        |
| -------------- | ---------------------------------------------------------------------------------------- | -------------------------------------------------------------------------- |
| `app.storage`  | in the browser of the mock desktop, per mock user; checks 64 KB per value, no 1 MB total | in the daemon, per user, 64 KB per value and 1 MB in total                 |
| Files          | `pick`, `saveAs`, `read` and `write` on picked files                                     | `shares`, `list`, `open`, `read`, `write`, `upload`, `createFolder`        |
| `ui.showError` | written to the log                                                                       | not served to third-party apps                                             |
| `session.save` | kept in the tab's session storage, no size check                                         | with the desktop layout, at most 16 KB                                     |
| Backend status | the panel only sends the event; the proxy keeps forwarding                               | a stopped backend wakes for a request; updating or unavailable answers 503 |
| Launch         | always `{ args: null }`; send `launch` from the panel                                    | `{ args: null }`, or `launch.notification` after a notification click      |
| Tokens         | valid 5 minutes, tickets 30 seconds and once, as on the box                              | the same, and checked against the session and the app's grant on every use |

Check `app.supports(method)` before every call that may be missing, so the app falls back the same way in both.

## Unit tests against a fake desktop

`createApp` connects app code over any transport end; `inProcessPair` gives two connected ends in one process, and `serveApp` from `@pithyx/sdk/host` is the desktop's side of the protocol. The desktop uses it for every window; in a test it lets you serve exactly the methods and context you want, with the SDK's own parameter and permission checks. This test runs with `node --test` and tests the `people` function of the [permissions guide](/docs/sdk/guides/permissions/):

<!-- example: sdk/testing.test.ts -->

```ts
// A unit test without a browser: the app's code talks to a fake desktop over the SDK's in-process transport, with the
// same protocol, parameter checks and permission checks as a real window. Run with `node --test`.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  createApp,
  inProcessPair,
  themeTokenNames,
  type AppContext,
  type PithyxApp,
  type Theme,
} from '@pithyx/sdk';
import { serveApp, type AppConnection, type MethodHandlers } from '@pithyx/sdk/host';
import { people } from './permissions.ts';

/** A context like the desktop's init message, for a user without directory.read. */
function testContext(permissions: string[]): Omit<AppContext, 'capabilities'> {
  const theme: Theme = {
    scheme: 'light',
    density: 'comfortable',
    tokens: Object.fromEntries(themeTokenNames.map((name) => [name, '0'])) as Theme['tokens'],
  };
  return {
    app: { id: 'com.example.board', version: '1.0.0' },
    instance: 'test-1',
    user: { id: 2, name: 'bob', displayName: 'Bob', groups: [], isAdmin: false, twoFactor: false },
    language: 'en',
    locale: 'en-US',
    timeZone: 'UTC',
    theme,
    sizeClass: 'expanded',
    launch: { args: null },
    session: null,
    permissions,
    box: { name: 'test', version: '0.1.0' },
    visible: true,
    online: true,
    logLevel: 'info',
    backend: null,
    storage: [],
    host: { version: '1.0.0', protocol: 1 },
  };
}

/** Connects app code to a fake desktop that serves only the given methods. */
async function fakeDesktop(
  permissions: string[],
  methods: MethodHandlers,
): Promise<{ app: PithyxApp; desktop: AppConnection }> {
  const [appEnd, desktopEnd] = inProcessPair();
  const desktop = serveApp(desktopEnd, { context: testContext(permissions), methods });
  const app = await createApp(appEnd, { themeRoot: null });
  return { app, desktop };
}

const directory: MethodHandlers = {
  'directory.users': () => [
    { id: 1, name: 'alice', displayName: 'Alice', groups: [] },
    { id: 2, name: 'bob', displayName: 'Bob', groups: [] },
  ],
};

await test('without directory.read only the signed-in user can be picked', async () => {
  const { app } = await fakeDesktop([], directory);
  assert.deepEqual(await people(app), [{ id: 2, name: 'bob', displayName: 'Bob' }]);
});

await test('a granted directory.read lists everyone, a revoked one falls back', async () => {
  const { app, desktop } = await fakeDesktop(['directory.read'], directory);
  assert.equal((await people(app)).length, 2);

  desktop.emit('permissionsChange', { permissions: [] });
  // Events arrive asynchronously, like over a MessagePort.
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.deepEqual(app.context.permissions, []);
  assert.equal((await people(app)).length, 1);
});
```

- Pass `themeRoot: null` to `createApp` outside a browser.
- Events and answers arrive asynchronously, like over a `MessagePort`: wait a tick after `desktop.emit`.
- A method you do not serve fails with `sdk.method.unknown` and is missing from `capabilities`, as on an older desktop.
- `@pithyx/sdk/host` is the desktop's side; use it only in tests, never in the app.

## Edge cases

- Opening the frontend directly in a tab fails: `connect()` waits 10 seconds for a desktop and then rejects with [`sdk.transport.closed`](/docs/reference/errors/#sdk.transport.closed).
- The mock serves the box's Content-Security-Policy, so a request to another server without `network.internet` fails in the mock as it would on the box.
- `pithyx dev` needs a valid `pithyx.json`. A backend runs with `docker compose up --wait --build` from `backend/compose.dev.yaml` if it exists, otherwise from `backend/compose.yaml`, with each endpoint's port published on `127.0.0.1` for the proxy; Ctrl+C runs `docker compose down` and keeps volumes.
- Test on the box before you publish: install the package as an administrator with **Install from file** on the **Installed** tab of the Store (see [Your first app](/docs/developers/apps/first-app/)), and check the calls the mock does not serve.

## Error codes

[`sdk.method.unknown`](/docs/reference/errors/#sdk.method.unknown), [`sdk.transport.closed`](/docs/reference/errors/#sdk.transport.closed), [`app.token.invalid`](/docs/reference/errors/#app.token.invalid), [`app.backend.unavailable`](/docs/reference/errors/#app.backend.unavailable), [`request.route.not_found`](/docs/reference/errors/#request.route.not_found).
