---
title: 'Events, window context and launch arguments'
description: 'Read app.context, follow the desktop events, handle launch arguments and notification clicks, and save window state.'
status: ready
audience: developer
sidebar:
  order: 3
---

`app.context` holds everything the desktop told the window at start: the user, language, theme, size class, launch arguments, permissions, backend and storage state. The SDK keeps it current, and `app.on(event, handler)` tells the app when something changed. `app.answer` replies to the two questions the desktop asks before a window goes away. This guide shows how a window starts, reacts and saves its state.

## Example

<!-- example: sdk/events-and-context.ts -->

```ts
// A viewer that follows its window: launch arguments, notification clicks, visibility, the connection, saved window
// state, unsaved changes and updates.
import { connect, type LaunchArgs, type PithyxApp } from '@pithyx/sdk';

interface WindowState {
  view: 'inbox' | 'archive';
  scrollTop: number;
}

const app: PithyxApp = await connect();
const state: WindowState = { view: 'inbox', scrollTop: 0, ...(await restoreState(app)) };
let dirty = false;
let poll: ReturnType<typeof setInterval> | undefined;

/** What this window saved before a reload, or on another device before "Get desktop from ...". */
async function restoreState(app: PithyxApp): Promise<Partial<WindowState>> {
  const saved = await app.session.restore();
  return typeof saved === 'object' && saved !== null ? saved : {};
}

function isView(value: unknown): value is WindowState['view'] {
  return value === 'inbox' || value === 'archive';
}

/** At start from the context, later from `launch` when a `single` app is launched again. */
function handleLaunch(launch: LaunchArgs) {
  if (launch.notification !== undefined) {
    // The user clicked a notification while no window of the app was open.
    openFromNotification(launch.notification.data, launch.notification.action);
  } else if (isView(launch.args)) {
    state.view = launch.args;
  }
  render();
}

function openFromNotification(data: unknown, action: string | null) {
  app.log.info('Opened from a notification', { action });
  if (typeof data === 'object' && data !== null && 'view' in data && isView(data.view)) {
    state.view = data.view;
  }
}

function startPolling() {
  poll ??= setInterval(() => void refresh(), 30_000);
}

function stopPolling() {
  clearInterval(poll);
  poll = undefined;
}

async function refresh() {
  if (app.context.online) {
    await app.window.setTitle(state.view === 'inbox' ? 'Inbox' : 'Archive');
  }
}

async function markDirty(value: boolean) {
  dirty = value;
  await app.window.setDirty(value);
}

async function save() {
  await app.session.save(state);
  await markDirty(false);
}

function render() {
  document.body.dataset.view = state.view;
  document.body.dataset.size = app.context.sizeClass;
  document.scrollingElement?.scrollTo(0, state.scrollTop);
}

handleLaunch(app.context.launch);
app.on('launch', handleLaunch);
app.on('notificationAction', ({ data, action }) => {
  openFromNotification(data, action);
  render();
});

// Minimizing does not hide the frame for the browser: visibilityChange is the signal to pause work nobody sees.
app.on('visibilityChange', ({ visible }) => {
  if (visible) {
    startPolling();
  } else {
    stopPolling();
  }
});
app.on('connectionChange', ({ online }) => {
  if (online) {
    void refresh();
  }
});
app.on('sizeClassChange', render);

// Asked only while setDirty(true), because the app calls setDirty; the desktop waits 5 seconds.
app.answer('beforeClose', () => ({ allow: !dirty }));
// Sign-out, shutdown, restart or an update of Pithyx: cannot be refused, 2 seconds to save.
app.answer('beforeUnload', async () => {
  await save();
  return {};
});
app.on('appUpdated', ({ version }) => {
  app.log.info('New version installed', { version });
  if (!dirty) {
    location.reload();
  }
});

addEventListener('scroll', () => {
  state.scrollTop = document.scrollingElement?.scrollTop ?? 0;
});
await markDirty(false);
if (app.context.visible) {
  startPolling();
}
render();
```

## Loading

Until your app connects, its window shows a placeholder that the desktop draws: your icon and name, and after 300 ms a moving bar. You write no code for it. While your backend starts (`backend.status` is `starting`, for example on the first open after it idled out), a line says so: "Starting <name> ..." in the user's language, or your own `window.loading.text` from the manifest. A backend that cannot start says why instead (`backend.reason`): it waits for an administrator to map a required folder, or its pool is locked or missing.

If your app first loads data and would show an empty window, set `window.loading.untilReady` to `true` and call `app.window.ready()` once there is something to see. The desktop removes the placeholder after 30 seconds in any case, also when `ready` never comes. Without `untilReady`, `ready()` does nothing. `pithyx dev` shows the same placeholder.

```json
"window": {
  "instances": "single",
  "defaultSize": { "width": 800, "height": 600 },
  "minSize": { "width": 360, "height": 400 },
  "loading": { "text": { "en": "Opening your lists ...", "de": "Deine Listen werden geöffnet ..." }, "untilReady": true }
}
```

## The context

Reading `app.context` needs no permission and no call. The SDK updates these fields before it calls your handlers: `theme` (`themeChange`), `language`, `locale`, `timeZone` (`localeChange`), `sizeClass` (`sizeClassChange`), `permissions` (`permissionsChange`), `visible` (`visibilityChange`), `online` (`connectionChange`), `logLevel` (`logLevelChange`), `backend.status` and `backend.reason` (`backendStatus`) and `storage` (`storageChange`). Read the field instead of keeping your own copy.

- `instance` is the id of this window, unique per desktop tab.
- `user` has `id`, `name`, `displayName`, `groups`, `isAdmin` and `twoFactor`. It is for display; a backend must use the proxy's headers instead.
- `sizeClass` is `compact` below 600 px, `medium` up to 1023 px and `expanded` above.
- `capabilities` lists the methods the desktop serves; `app.supports(method)` reads it.

## Launch arguments

`app.context.launch` says why the window opened: `{ args, file?, notification? }`. A window the user opened from the desktop or the Store has `{ args: null }`. A click on one of the app's notifications while no window of the app was open starts it with `launch.notification` (`notification`, `action`, `data`). For an app with `"instances": "single"` in the manifest, launching it again focuses the user's window on this device and sends the new arguments as the `launch` event.

`launch.file` and the `openFile` event belong to file handlers. When the user chooses your app in "Open with" in the Files app for a file whose extension a `fileHandlers` entry of your manifest names, a new window gets `launch.file`, and an open window of a `single` app gets `openFile` with the file. Both are handles you read with `files.read` (and write with `files.write` if the user may); an app without `files.read` gets no file. Open the file in your `openFile` handler, not only at start.

## Notifications

A click on a notification or one of its buttons while a window of the app is open brings that window to the front and sends `notificationAction` with the notification id, the button's `id` (or `null` for the notification itself) and the `data` you passed to `notify`. The window that sent the notification gets it if it is still connected, otherwise the top-most window of the app.

The desktop adds its own **Open** button to every notification of an app, next to your `actions`. Do not name an action "Open"; give the buttons what they do instead ("Reply", "Show the list").

## Events

| Event                          | When                                                                                                                                 |
| ------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------ |
| `focus`, `blur`                | the window gained or lost the focus                                                                                                  |
| `minimize`, `restore`          | the window was minimized or shown again                                                                                              |
| `visibilityChange`             | the window can or cannot be seen: minimized, or the browser tab hidden                                                               |
| `connectionChange`             | the desktop lost or regained the connection to the box                                                                               |
| `sizeClassChange`              | the window crossed 600 or 1024 px                                                                                                    |
| `themeChange`, `localeChange`  | see [Languages and theming](/docs/sdk/guides/i18n-and-theming/)                                                                      |
| `permissionsChange`            | see [Permissions](/docs/sdk/guides/permissions/)                                                                                     |
| `storageChange`                | a storage mapping changed or its pool was locked or came back                                                                        |
| `backendStatus`                | the backend is `stopped`, `starting`, `running`, `updating` or `unavailable`, with `reason` `storage` or `pool` when it cannot start |
| `logLevelChange`               | an administrator switched debug logging for the app                                                                                  |
| `appUpdated`                   | another version is installed (an update or a rollback); reload to use it, after saving                                               |
| `launch`, `notificationAction` | see above                                                                                                                            |

`app.on` returns a function that removes the handler. Handlers run in the order they were added. Events the app does not know are ignored, so new events never break an app.

## Saving state

- **`app.session.save(state)`** stores up to 16 KB of JSON for this window with the desktop layout. `app.session.restore()` returns it after a reload and on another device after "Get desktop from ...". Larger state fails with [`app.storage.too_large`](/docs/reference/errors/#app.storage.too_large).
- **`beforeClose`** is asked when the user closes the window. Answer `{ allow: false }` to keep it open, for example to ask about unsaved changes, then call `app.window.close()` yourself, which does not ask again. Once the app called `window.setDirty`, the desktop asks only while the window is dirty and closes a clean window at once. The desktop waits 5 seconds and then offers to close the window anyway; a handler that throws allows the close.
- **`beforeUnload`** comes before a sign-out, a shutdown, a restart or a Pithyx update (`reason`). It cannot be refused; the desktop waits 2 seconds.

## Edge cases

- Events that happen while the window connects are not replayed; the init context already holds their result.
- `visibilityChange` is the signal to pause timers and animations: a minimized window's frame stays visible to the browser.
- After `connectionChange` with `online: false`, calls that need the box fail until it is back. Do not retry in a loop; wait for `online: true`.
- `app.window.setTitle` takes at most 200 characters; a longer title fails with [`sdk.request.invalid`](/docs/reference/errors/#sdk.request.invalid). Window calls act only on the calling window.
- The mock desktop of `pithyx dev` always starts with `launch: { args: null }`; send `launch`, `notificationAction` or any other event with your own data from its panel.

## Error codes

[`app.storage.too_large`](/docs/reference/errors/#app.storage.too_large), [`sdk.request.invalid`](/docs/reference/errors/#sdk.request.invalid), [`sdk.request.timeout`](/docs/reference/errors/#sdk.request.timeout), [`sdk.transport.closed`](/docs/reference/errors/#sdk.transport.closed).
