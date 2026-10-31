---
title: '@pithyx/ui'
description: "The desktop's React components and tokens, so third-party apps look native."
status: ready
audience: developer
sidebar:
  order: 3
---

`@pithyx/ui` is the React component library of the Pithyx desktop, built on React Aria Components (keyboard, touch and screen reader support), together with the design tokens in `tokens.css`. The tokens and the class names for plain HTML elements work without React. Third-party apps can use it to look like the desktop; the SDK does not require it.

## Installing

```sh
npm install @pithyx/ui react react-dom
```

React and React DOM 19 are peer dependencies. Import the token file once and wrap your app in the provider:

```tsx
import '@pithyx/ui/tokens.css';
import { UiProvider, Button } from '@pithyx/ui';

export function App() {
  return (
    <UiProvider locale="de-AT">
      <Button>Save</Button>
    </UiProvider>
  );
}
```

`UiProvider` sets the locale for React Aria and for the package's own texts (English and German; other languages show English). Without it the components follow the browser.

## Tokens without React

`@pithyx/ui/tokens.css` is the only token file of Pithyx and needs no JavaScript. Colors follow the system's light or dark setting (`light-dark()`); force one with `<html data-theme="light">` or `"dark"`. In an app the SDK sets `data-theme` to the desktop's scheme on connect and on every theme change, so the components follow the desktop, not the browser. Choose an accent with `<html data-accent="...">`: `petrol` (the default), `ocean`, `indigo`, `plum`, `moss` or `graphite`. Every preset is contrast-checked in both themes. The token names are on [Theming and design tokens](/docs/developers/desktop/theming/).

The components read the `--color-*`, `--space-*`, `--radius-*` and `--control-height` tokens, not the SDK's `--pithyx-*` properties. An app that uses the components therefore imports `tokens.css`. Once the SDK connected, the components follow the desktop without code: the SDK sets the scheme and `data-pithyx-theme` on the root, and `tokens.css` then takes the desktop's colors and fonts from the `--pithyx-*` properties, including the user's accent; hover and pressed shades of the accent are derived from it.

For your own CSS next to the components, use the same tokens of `tokens.css` (`--color-*`, `--space-*`, `--radius-*`, `--font-*`, `--control-height`); their names belong to the API of `@pithyx/ui` and follow its SemVer, so a rename is a major version. An app without `@pithyx/ui` uses the SDK's `--pithyx-*` properties instead, a stable contract of the SDK.

`@pithyx/ui/native` exports class names for native elements that look like the components, without React Aria: `buttonStyles`, `controlStyles` and `textFieldStyles`, plus `cx` and `uiTexts`. It is meant for code that has to stay small.

## Components

- Buttons: `Button` (with `ButtonVariant`) and `IconButton`.
- Fields: `TextField`, `PasswordField`, `SearchField` and `OtpField`.
- Controls: `Checkbox`, `Switch`, `ProgressBar`, `Badge`, `RadioGroup` with `Radio`, and `Select`.
- Overlays: `Dialog`, `Menu`, `MenuItem`, `MenuTrigger`, `MenuPopover`, `MenuSeparator`, `ContextMenuPopover`, `Tooltip` and `TooltipTrigger`.
- Helpers: `UiProvider`, `useDeliberatePress`, `cx` and `uiTexts`.

Use `RadioGroup` for two to four choices and `Select` (with `options` of `id` and `label`) beyond that.

`Checkbox` and `Switch` take their label as children (`<Checkbox isSelected={done} onChange={setDone}>Done</Checkbox>`), `ProgressBar` as `label`.

The [API reference](/docs/reference/sdk/) has a page per component, generated from its TSDoc (for example [Button](/docs/reference/sdk/ui/button/) and [Dialog](/docs/reference/sdk/ui/dialog/)), and every token with its value on [tokens](/docs/reference/sdk/ui/tokens/).

## Patterns

The patterns build whole views after the [design guidelines](/docs/developers/apps/design/). They adapt to the width of their container, not the screen, so they also work in a narrow app window.

- `AppFrame`: the frame of an app with more than one view. A `navigation` column on the left, then the page `title`, an optional `toolbar`, the content, an optional `footer` (fixed below the scrolling content) and `statusBar`. Below 600 px the navigation becomes a list page: choosing an item opens the page, and a **Back** button leads back. `scroll="content"` keeps the title and toolbar fixed for a table that scrolls on its own.
- `NavList`: the navigation column. `groups` of items with an `icon`, a `label` and an optional `count`, a search field that filters them (`searchable`), the current item from `selectedId` with an accent bar, announced as the current page.
- `Toolbar`, `ToolbarButton` and `ToolbarSeparator`: the primary action first, the other actions grouped, view options and search in `end`. The arrow keys move between the controls. `variant="bar"` draws a full-width strip. A `ToolbarButton` has an `icon` and a `label`; when the app's size container is narrower than 760 px it shows only the icon and the label as a tooltip, so a toolbar with many actions stays on one line.
- `Tabs`, `TabList`, `Tab` (with an optional `count`) and `TabPanel`: tabs below the page title, each panel with its own toolbar.
- `DataTable`: `columns` with a `cell` renderer, a `sortValue` that makes the column sortable, `align="end"` for numbers, `muted` for secondary columns and `hideWhenNarrow` for a column the details pane repeats, left out below 600 px. Single or multiple selection (a click selects, Ctrl or Shift extend), `onRowAction` for a double click or Enter, an `emptyState`, `density="comfortable"` for two-line rows (52 px), a `details` pane on the right that the user can drag wider and `virtualized` for long lists in a container with a bounded height.
- `DetailsPane`: the same pane for a list that is no `DataTable`, with the handle on its left edge (drag, the arrow keys, Home and End; a double click goes back to the default width). Its width is `var(--details-pane-width, <default>)` in your class; a layout that moves it below the list sets `--details-handle: none`.
- `FormSection` and `FormRow`: a settings section with a title row and rows of a label (with an optional hint) and a control. A `TextField`, `Select` or `RadioGroup` inside a `FormRow` takes the row's label as its name, so leave out its own `label`.
- `ActionBar`: **Discard** and **Apply** with one line about what applying does, for the `footer` of an `AppFrame`. Only for pages whose changes need applying.
- `Card` and `Meter`: an object with a state: `icon`, `title`, a `badge`, a line of `facts`, an optional `meter`, and the details (children) and `actions` behind a chevron. `Meter` shows a quantity with texts above both ends; give it a `valueLabel` for screen readers.
- `StatusBadge`: a state in words with the tone `success`, `warning`, `danger`, `info` or `neutral`.
- `AttentionList`: **Needs attention** on an overview, one row per problem with the one action that fixes it. It renders nothing while there is no problem.
- `SummaryTiles` and `SummaryTile`: at most four figures at the top of an overview.

```tsx
<AppFrame
  navigation={
    <NavList aria-label="Settings" groups={groups} selectedId={page} onSelect={setPage} />
  }
  title="Network"
  footer={<ActionBar message="Applies to every device." onApply={apply} onDiscard={reset} />}
>
  <FormSection title="IPv4">
    <FormRow label="IP address">
      <TextField value={address} onChange={setAddress} />
    </FormRow>
  </FormSection>
</AppFrame>
```

## Icons

`@pithyx/ui` exports no icons. The desktop uses Tabler Icons (MIT); to look the same, add `@tabler/icons-react` to your app with the range `@pithyx/ui` itself depends on (`^3.48.0`, so your bundle keeps one copy) and import each icon you use, for example `import { IconTrash } from '@tabler/icons-react';`. Give an `IconButton` its `label`, which is also its tooltip and accessible name.
