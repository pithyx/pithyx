# Desktop

Rules for `desktop/`. The root `CLAUDE.md` applies as well.

- Docs (D340): a visible change (a screen, a button, a shortcut, a setting, a message) checks the matching pages under `website/src/content/docs/` (`user/` for everyone, `admin/` for administrators, `developers/desktop/` for internals) and updates or adds them in English and German in the same commit; a likely question goes into the FAQ (`website/CLAUDE.md`).

## Responsive and accessible

- One UI for every screen: phone, tablet, laptop, desktop, ultrawide. No separate mobile app.
- The desktop has three size classes by viewport width: compact (below 600 px: full-screen apps, app switcher), medium (600 to 1023 px: maximized windows by default, side-by-side split), expanded (1024 px and up: free windows, snap zones including thirds on ultrawide).
- Apps adapt to their window size, not the device. Use CSS container queries. The SDK reports the size class of the window.
- Input: detect pointer type, not device. Touch targets at least 44 px on coarse pointers. Every hover interaction has a touch or keyboard equivalent. Respect safe-area insets and the on-screen keyboard.
- Accessibility: WCAG 2.2 AA. Full keyboard operation, visible focus, screen reader labels, contrast checked in CI, `prefers-reduced-motion` respected.
- Playwright runs the key flows at 390x844 (phone), 820x1180 (tablet), 1440x900 (laptop) and 3440x1440 (ultrawide), with touch and mouse.

## Performance

- The UI never blocks on the network. Show cached data first, then reconcile.
- Directory listings come in cursor pages over REST (step 6.2, pages instead of a WebSocket stream); live changes of open folders come over the WebSocket. Never wait for a full listing.
- Long lists are virtualized. A Web Worker for heavy sorting and filtering is planned, not built yet.
- Thumbnails and other derived data load asynchronously and never delay a listing.
- User actions are optimistic and roll back on error.
- No spinner before 300 ms.
- Apps are loaded with dynamic `import()` when opened, never at login. The sign-in bundle stays below 200 KB gzip and within 5 percent of its recorded baseline; the desktop bundle loaded after sign-in stays below 150 KB gzip.
- High-frequency state (live stats, job progress) goes through Zustand with narrow selectors. No React context for data that changes often.
- Use the React Compiler instead of hand-written `useMemo` and `useCallback`.
- No `useEffect` for derived state or data fetching. REST data goes through TanStack Query, live data through the WebSocket store. `useEffect` is only for syncing with things outside React.
- Core apps run in the desktop context. Only third-party apps use iframes.

## Shared UI

- Components shared by the desktop, core apps and third-party apps come from `@pithyx/ui` (`packages/ui`, MIT): Button, TextField, SearchField, Controls, Dialog, Menu, ContextMenu, Tooltip, with `UiProvider({ locale })` and its own en and de texts. `desktop/src/ui` moves there in A5; the desktop consumes it from source through the `pithyx-source` condition, so the bundle budget still applies. Desktop-only components stay in `desktop/`.
- `tokens.css` in `@pithyx/ui` is the only token file (usable without React); the contrast test checks it.
- The desktop draws everything an iframe cannot do for itself: app context menus, badges, attention, the clipboard and `openUrl` (through `platform/browser.ts`), the default icon (the box glyph on a neutral tile).
- Performance budgets are CI tests. The timing budgets (`e2e/tests/budgets.spec.ts`) are the deciding gate; a regression of more than 5 percent on any tracked timing fails the build. They run with the E2E suite, so a change on the loading path, the sign-in or the bundle gets the label `e2e` (D278); the bundle size check runs in every frontend job. Do not trade code clarity for a few bytes of bundle size while the desktop stays within its limit.

## Desktop layout

- There are no persistent terminal-server sessions. The server only remembers the layout: open windows, position, size, screen and small app data such as the current folder.
- Layouts are stored per user and device. Desktop items (wallpaper, icons, files, sticky notes) are per user and shared across devices.
- A user can take over the layout of another of their devices ("Get desktop from ..."). There is no live sync of window positions between devices.
- The taskbar defaults to the bottom and can be moved.
- Layouts live in SQLite with `expires_at`. No Redis.
- Layouts never contain permissions. Every access is checked live. On restore, skip windows whose app, path or permission no longer exists.
- Layouts carry a schema version. Saves are debounced and go over REST (`PUT /api/desktop/layout`) with the layout's version as `If-Match`; only the visible, focused tab saves.
- App data in a layout is capped at 16 KB.

## Design (D394)

Direction F of the design pass (#227): structured and calm, laid out like a classic desktop, with Pithyx's own colors, type, logo and radii. These rules bind every screen of the desktop and the core apps and the patterns of `@pithyx/ui`; third-party apps get them as guidance (`/docs/developers/apps/design/`). A screen not rebuilt yet keeps its look until its wave of #227; a new screen, or one a change reworks, follows them.

### Shell

- Taskbar: full width, at the bottom unless the user moved it. From the start: the Pithyx button, one button per open window with icon and title (the focused one underlined in the accent), pinned apps as icons; at the end the status area, notifications, clock with date.
- The status area follows the user's rights: administrators see the box (pool state, CPU, network) as small chips that open the matching app; other users see only what is theirs (uploads, their jobs).
- Pithyx menu: attached to the taskbar at the Pithyx button, never floating in the middle of the screen. A narrow rail (account, home, pictures, personal settings, sign out, power only with the right), a searchable app list grouped by area (System only for administrators, Apps, You), and a tile area: administrators get status tiles (pools, system, updates), other users "For you" (unread, due), then recent files and the user's folders with their access level. Search finds apps, files and settings.
- Desktop: icons in a column from the top left, white labels with a text shadow on the wallpaper (never a label background that looks selected), sticky notes. No status widgets for users who are not administrators.
- Windows: title bar with app icon, title and the caption buttons on the right; focus shows through the title and the shadow, not a colored border.

### App frame

- An app with more than one view uses one frame: a navigation column on the left (220 to 270 px: a search field on top, groups under small uppercase headings, items with icon and name, the active item with a background and a 3 px accent bar, counts as small badges) and a content area with the page title, optional tabs, the toolbar, the content and an optional status bar at the bottom.
- An app with one view (Viewer, Editor) has no navigation column and keeps the toolbar.
- In the compact size class the navigation becomes a list page the user drills into (with a back button), never a drawer over the content.

### Toolbar

- The primary action first as the one filled accent button of the view, then the other actions as buttons with icon and label, grouped by separators; view options and the details toggle at the end; search at the end or in the address bar.
- Buttons have labels. Icon-only buttons only where the width runs out, always with a tooltip and an accessible name.

### Lists and tables

- Items with more than two attributes are a table: a header row with column separators and sortable headings, rows of 28 to 30 px on fine pointers (44 px on coarse), rows with two lines about 52 px with room above and below (D406), secondary columns muted, numbers right-aligned with tabular figures, states as badges.
- The selected row uses the selection color with an accent outline; hover uses the hover surface.
- Details of the selected item go into a details pane on the right (250 to 300 px by default, its left edge drags it wider, `DetailsPane`, D406), switched from the toolbar, not into a dialog.
- Groups inside a list (by day, by type) get a small bold muted heading. An empty list says in one sentence what is missing and offers the primary action.

### Forms and settings

- A settings page is made of sections: a bordered card with a title row, the label on the left (about 200 px, with a hint line below), the control on the right (at most 360 px); in a section wider than 900 px the control sits at the right edge and the label takes the room between (D406); one column in the compact size class.
- A page whose changes need applying has a fixed action bar at the bottom (**Discard**, **Apply**, one line about what applying does); settings that apply at once (toggles, appearance) have none. Never both kinds on one page.
- Radio buttons for two to four choices, a select beyond that; a toggle only for on and off that applies at once.

### Cards, state and attention

- Objects with a state (pools, interfaces, apps) are cards: icon, name, a state badge, one line of facts, a meter where a quantity fits, and a chevron that expands the details and the object's actions.
- State badges name the state in words, never by color alone: success green, warning orange, error red, neutral grey.
- An overview page starts with at most four summary tiles, then **Needs attention** (one row per problem with the one action that leads to the fix), then the rest. Problems go there instead of scattered banners.

### Rights

- The UI follows the user's rights. What a user can never do is not shown (the System group, the box status, power without the right). What the current place forbids is shown disabled, and the reason is said once for the place (an info bar), not per button.
- A read-only place says so once: the info bar, a lock in the navigation, **Read only** in the status bar.
- Never let a user find out about a missing right through an error after a click when the UI could know it before.

### Look

- Tokens only: no raw colors, sizes, radii or shadows in components. Radii 4 to 6 px for controls, cards and windows; menus attached to an edge have square corners on that edge.
- Calm surfaces: neutral backgrounds; the accent only for the primary action, selection, the active navigation item and focus; amber only for the brand and for attention. App icons are tinted tiles (one hue per app: light tile with dark glyph, dark tile with light glyph in the dark theme).
- No translucent or blurred surfaces except over the wallpaper; content stays opaque.
- Every screen works and is checked in light and dark.
- The direction borrows the structure of a classic desktop, never its look: no copied icons, glyphs, colored ad tiles or sounds, and no product of another company is named (root `CLAUDE.md`).
