# Desktop

Rules for `desktop/`. The root `CLAUDE.md` applies as well.

## Responsive and accessible

- One UI for every screen: phone, tablet, laptop, desktop, ultrawide. No separate mobile app.
- The desktop has three size classes by viewport width: compact (below 600 px: full-screen apps, app switcher), medium (600 to 1023 px: maximized windows by default, side-by-side split), expanded (1024 px and up: free windows, snap zones including thirds on ultrawide).
- Apps adapt to their window size, not the device. Use CSS container queries. The SDK reports the size class of the window.
- Input: detect pointer type, not device. Touch targets at least 44 px on coarse pointers. Every hover interaction has a touch or keyboard equivalent. Respect safe-area insets and the on-screen keyboard.
- Accessibility: WCAG 2.2 AA. Full keyboard operation, visible focus, screen reader labels, contrast checked in CI, `prefers-reduced-motion` respected.
- Playwright runs the key flows at 390x844 (phone), 820x1180 (tablet), 1440x900 (laptop) and 3440x1440 (ultrawide), with touch and mouse.

## Performance

- The UI never blocks on the network. Show cached data first, then reconcile.
- Directory listings stream in chunks over the WebSocket. Never wait for a full listing.
- Long lists are virtualized. Heavy sorting and filtering run in a Web Worker.
- Thumbnails and other derived data load asynchronously and never delay a listing.
- User actions are optimistic and roll back on error.
- No spinner before 300 ms.
- Apps are loaded with dynamic `import()` when opened, never at login. Initial desktop bundle stays below 200 KB gzip.
- High-frequency state (live stats, job progress) goes through Zustand with narrow selectors. No React context for data that changes often.
- Use the React Compiler instead of hand-written `useMemo` and `useCallback`.
- No `useEffect` for derived state or data fetching. REST data goes through TanStack Query, live data through the WebSocket store. `useEffect` is only for syncing with things outside React.
- Core apps run in the desktop context. Only third-party apps use iframes.
- Performance budgets are CI tests. A regression of more than 5 percent on any tracked metric fails the build.

## Desktop layout

- There are no persistent terminal-server sessions. The server only remembers the layout: open windows, position, size, screen and small app data such as the current folder.
- Layouts are stored per user and device. Desktop items (wallpaper, icons, files, sticky notes) are per user and shared across devices.
- A user can take over the layout of another of their devices ("Get desktop from ..."). There is no live sync of window positions between devices.
- The taskbar defaults to the bottom and can be moved.
- Layouts live in SQLite with `expires_at`. No Redis.
- Layouts never contain permissions. Every access is checked live. On restore, skip windows whose app, path or permission no longer exists.
- Layouts carry a schema version. Saves are debounced over the WebSocket.
- App data in a layout is capped at 16 KB.
