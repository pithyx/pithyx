---
title: 'Design guidelines'
description: 'How an app looks and behaves like the rest of Pithyx: the app frame, toolbars, tables, forms, state and rights.'
status: ready
audience: developer
sidebar:
  order: 6
---

Pithyx's own apps follow one set of design rules (D394): structured and calm, with a navigation column, a toolbar, tables and settings pages that look the same in every app. A third-party app does not have to follow them, but people find their way around an app that does at once. This page gives the rules that apply inside an app window; the desktop draws everything around it (title bar, caption buttons, taskbar button, context menus).

## The app frame

- An app with more than one view uses a navigation column on the left (220 to 270 px): a search field on top if there is something to search, groups under small uppercase headings, items with an icon and a name. The active item has a background and a 3 px bar in the accent color on its left; counts are small badges at the end of the item.
- The content area starts with the page title, then optional tabs, then the toolbar, then the content; a status bar at the bottom is optional (item count, selection, free space).
- An app with a single view (a viewer, an editor) has no navigation column and keeps the toolbar.
- In the compact size class (`app.context.sizeClass` is `compact`, below 600 px) the navigation becomes a list page the user opens items from, with a back button, rather than a drawer over the content. Listen to `sizeClassChange`; lay out with container queries, since the window, not the device, decides.
- [`@pithyx/ui`](/docs/sdk/ui/#patterns) builds this frame: `AppFrame` with a `NavList`. It switches to the list page by the width of its container, so it works in a narrow window on a wide screen.

## Toolbars

- The primary action comes first as the only filled button of the view (`Button` with the primary variant), then the other actions as buttons with an icon and a label, grouped by separators. View options and a switch for the details pane go at the end; search goes at the end or into the address bar.
- Buttons have labels. Show only the icon where the width runs out, with a tooltip and an accessible name: `ToolbarButton` does this by itself in a narrow window, `IconButton` for a button that never has room for a label.
- `Toolbar` with `ToolbarButton`, `ToolbarSeparator` and its `end` section; `Tabs` with a `count` per tab.

## Lists and tables

- Items with more than two attributes are shown as a table: a header row with sortable headings, rows of 28 to 30 px with a fine pointer and at least 44 px on touch (`density="comfortable"` for rows with two lines, 52 px), secondary columns in the muted text color, numbers right-aligned with tabular figures, states as `StatusBadge`.
- Show the details of the selected item in a pane on the right (250 to 300 px) that the toolbar switches on and off, rather than in a dialog. Users can drag its left edge to make it wider; `DetailsPane` does that outside a `DataTable`.
- An empty list says in one sentence what is missing and offers the primary action.
- `DataTable` does all of this, including the selection, the sort, the `emptyState` and the `details` pane; `virtualized` for long lists.

## Forms and settings

- Build a settings page from sections: a bordered card with a title row, the label on the left (about 200 px, with a hint line below), the control on the right (at most 360 px). In a section wider than 900 px, `FormRow` puts the control at the right edge and gives the label the room between. Stack them in the compact size class.
- If changes take effect only when applied, give the page a fixed bar at the bottom with **Discard** and **Apply** and one line about what applying does. Settings that take effect at once (switches, appearance) need no bar. Do not mix both kinds on one page.
- Use radio buttons for two to four choices, a select beyond that, and a `Switch` only for on and off that applies at once.
- `FormSection`, `FormRow`, `ActionBar`, `RadioGroup` and `Select` build such a page.

## State and attention

- Things with a state (an account, a device, a job) are cards: icon, name, a state badge, one line of facts, a meter where a quantity fits, and the details and actions when expanded.
- Name a state in words, never by color alone: success green, warning orange, error red, neutral grey.
- An overview starts with a few summary figures, then a list of what needs attention, each row with the one action that fixes it.
- `Card` with `Meter`, `StatusBadge`, `AttentionList` and `SummaryTiles` build these.

## Rights

The app knows the user (`app.context.user`, including `isAdmin` and `groups`) and its own permissions (`app.context.permissions`, updated by `permissionsChange`).

- Do not show what the user can never do.
- Where the current place forbids something (a folder the user may only read), show the actions disabled and say why once for the place, in an info bar, not on every button.
- Do not let the user find out about a missing right through an error after a click when the app can know it before.

## Look

- Use the components of [`@pithyx/ui`](/docs/sdk/ui/) and the tokens of `tokens.css` for everything else: no raw colors, sizes, radii or shadows. Radii stay small: `--radius-control` (4 px) for controls, `--radius-card` (5 px) for cards and sections, `--radius-tag` (3 px) for tags inside rows.
- Keep surfaces calm: neutral backgrounds; the accent only for the primary action, the selection, the active navigation item and focus.
- The SDK sets the desktop's light or dark scheme and the user's accent on the app's root; check every screen in both schemes.
- Ship fonts and icons inside the app package. The desktop's fonts are IBM Plex Sans for text and JetBrains Mono for paths, logs and code; `tokens.css` names them in `--font-sans` and `--font-mono`.
