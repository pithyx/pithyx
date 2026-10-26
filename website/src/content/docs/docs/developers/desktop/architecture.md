---
title: 'Desktop architecture'
description: 'The React web desktop: shell, window manager, core apps and how it talks to the daemon.'
status: ready
audience: developer
sidebar:
  order: 1
---

The desktop is a React 19 single-page app in `desktop/` (Vite 8, TypeScript, zustand, TanStack Query, React Aria Components). It talks to the daemon through the generated REST client and one WebSocket, runs without elevated rights, and hosts both the built-in core apps and third-party apps in sandboxed windows.

## Overview

- **Stack.** React 19 with the React Compiler (no hand-written memoisation), Vite 8, zustand for the window manager, TanStack Query for REST data, i18next with ICU messages for texts.
- **Source folders** in `desktop/src`: `shell`, `wm`, `desktop`, `items`, `apps`, `realtime`, `api`, `platform`, `theme`, `design`, `errors`, `auth`, `installer`, `network` and `ui`.
- **Startup** (`main.tsx`): apply the stored theme and appearance, create the query client, load the translations, render the app.
- **Loading path.** The sign-in screen is a small chunk. The desktop chunk loads when you sign in (or immediately when a session hint exists), and the sign-in screen prefetches it. The desktop is deliberately not behind `lazy()` and `Suspense`. Each core app loads on first use, never at sign-in.
- **Installer.** The installer interface is loaded only on the live system.
- **Design gallery.** `desktop/design.html` (`pnpm design`, port 5174) shows every screen for layout checks. It is for development only.
- **Dev server.** It proxies `/api` and `/ws` to the daemon and rewrites the `Origin` header of WebSocket upgrades, because the daemon accepts only its own origin.
- **Secure contexts.** The desktop works on plain HTTP by address. ESLint refuses `crypto.randomUUID`, `crypto.subtle`, the Clipboard API, service workers, WebAuthn, notifications, `window.open` and similar APIs outside `desktop/src/platform/browser.ts`, which offers fallbacks (`randomId`, `copyText`, `openInNewTab`). `deviceTrust` registers the service worker (`desktop/service-worker.js`, written as `sw.js` with a cache name per build) and so tells whether the device trusts the box; passkeys (`createPasskey`, `getPasskey`, with the JSON conversion in `platform/webauthnJson.ts` loaded on first use) wait for that.
- **Texts.** ESLint also refuses string literals in JSX (`i18next/no-literal-string`) and imports of icon libraries other than through `ui/icons.ts`.

## Shell and window manager

The shell (`desktop/src/shell`) has the taskbar, the Pithyx menu (start menu), app switcher, windows, wallpaper, notifications, the transfers button and sticky notes. The taskbar can sit at the bottom, top, left or right and is saved with the layout.

The window manager is a zustand store (`useDesktop`). Every change goes through `dispatch(action)` and a pure `reduce()`.

- **Size classes** by viewport width: compact below 600 px, medium from 600 to 1023 px, expanded from 1024 px. Workspaces of 2400 px or more offer snapping to thirds and two-thirds.
- **Placement** of a window is free, maximized or snapped (left, right, thirds, two-thirds).
- **Limit:** 50 windows. The daemon enforces the same limit.
- **Instances.** An app declares `single` or `multi`. Opening a `single` app again focuses the user's window on this device and passes launch arguments.
- **Keyboard.** All window shortcuts use Alt+Shift because browsers and operating systems do not claim it: Space for the window menu, Left and Right to snap, Up to maximize, Down to restore or minimize, Page Up and Page Down to cycle windows.
- **Layout memory.** The layout (windows, positions, order, taskbar) is saved per user and device in the daemon (`PUT /api/desktop/layout` with `If-Match`), debounced by 1 second, only by the visible tab, flushed when the page is hidden. Windows of apps you may no longer open are skipped on restore. Limits: 50 windows, 16 KB of window arguments, 48 KB for the whole layout. "Get desktop from" copies the layout of another device.
- **Appearance.** Accent and wallpaper are saved per user and shared across devices; the light or dark choice is saved per browser.
- **Desktop items** (shortcuts to apps, files and folders, and notes) are saved per user through `/api/desktop/items`; shortcuts to files follow renames and moves made through the daemon (D382).

## Core apps

Core apps are listed in `desktop/src/apps/registry.tsx`; their ids are in `apps/ids.ts` and mirrored in the daemon (`Apps/CoreApps.cs`).

| App id                | Name             | Who sees it                            | Instances |
| --------------------- | ---------------- | -------------------------------------- | --------- |
| `org.pithyx.files`    | Files            | granted users                          | multi     |
| `org.pithyx.storage`  | Storage          | administrators                         | single    |
| `org.pithyx.settings` | Settings         | administrators                         | single    |
| `org.pithyx.monitor`  | Resource Monitor | administrators                         | single    |
| `org.pithyx.logs`     | Log Center       | administrators                         | single    |
| `org.pithyx.viewer`   | Viewer           | granted users, opened with a file only | multi     |
| `org.pithyx.about`    | About Pithyx     | everyone                               | single    |
| `org.pithyx.store`    | Store            | administrators                         | single    |

Core apps use the same `@pithyx/sdk` API as third-party apps, but in the desktop's own context: `CoreSdkApp` connects the window to the host through an in-process pair of endpoints instead of a `MessageChannel`. The app gets the same `PithyxApp` object. Core apps may also use extra internal host methods that are not part of the public SDK. Third-party apps run in an `<iframe sandbox="allow-scripts allow-forms">` without `allow-same-origin`; their identity comes from the message port, never from message data.

To add a core app: add its id to `apps/ids.ts` and the daemon's `CoreApps.cs`, add an entry to `registry.tsx`, add `apps.<id>` to `i18n/en/desktop.json` and `i18n/de/desktop.json` (a test enforces this), and write a window component that wraps `CoreSdkApp`.

## Data and realtime

- **REST.** One client, `api = createApiClient()`, from `@pithyx/api-client`. `unwrap(await api.GET(...))` throws a `PithyxApiError` with code, params, request id and status. The query client retries failed queries up to 3 times, and an `auth.session.expired` error sends the user back to the sign-in.
- **WebSocket.** One `RealtimeConnection` per tab at `/ws`. Subscriptions are reference-counted and sent again after every reconnect. `useTopic(topic)` returns the latest snapshot (undefined until the first one arrives); `realtime.listen` handles members of topic families such as one folder.
- **Reducers.** `reducers.ts` has one reducer per topic and is typed by the generated topic list, so a new daemon topic fails the build until the desktop handles it.
- **Reconnect.** The delay is `min(30 s, 0.5 s * 2^attempt)`, randomized to 50 to 100 percent. Close code 4401 sends the user to the sign-in, 4403 stops, the others reconnect. See [API](/docs/developers/daemon/api/).
- **Errors.** Error messages from the socket go to the error dialog. Request ids are written to the console, never shown.
- **Listings.** Folder listings load in cursor pages over REST. Pithyx does not stream listings over the WebSocket or sort in a Web Worker. Files reads the first page of a folder ahead when the mouse rests on it for 150 ms, so a double click shows cached rows and revalidates them like a visited folder.
