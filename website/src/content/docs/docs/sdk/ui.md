---
title: '@pithyx/ui'
description: "The desktop's React components and tokens, so third-party apps look native."
status: draft
audience: developer
sidebar:
  order: 3
---

`@pithyx/ui` is the React component library of the Pithyx desktop, built on React Aria Components (keyboard, touch and screen reader support), together with the design tokens in `tokens.css`. The tokens and the class names for plain HTML elements work without React. Third-party apps can use it to look like the desktop; the SDK does not require it.

## Installing

```sh
npm install @pithyx/ui@next react react-dom
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

`@pithyx/ui/tokens.css` is the only token file of Pithyx and needs no JavaScript. Colors follow the system's light or dark setting (`light-dark()`); force one with `<html data-theme="light">` or `"dark"`. Choose an accent with `<html data-accent="...">`: `petrol` (the default), `ocean`, `indigo`, `plum`, `moss` or `graphite`. Every preset is contrast-checked in both themes. The token names are on [Theming and design tokens](/docs/developers/desktop/theming/).

The components read the `--color-*`, `--space-*`, `--radius-*` and `--control-height` tokens, not the SDK's `--pithyx-*` properties. An app that uses the components therefore imports `tokens.css`.

`@pithyx/ui/native` exports class names for native elements that look like the components, without React Aria: `buttonStyles`, `controlStyles` and `textFieldStyles`, plus `cx` and `uiTexts`. It is meant for code that has to stay small.

## Components

- Buttons: `Button` (with `ButtonVariant`) and `IconButton`.
- Fields: `TextField`, `PasswordField`, `SearchField` and `OtpField`.
- Controls: `Checkbox`, `Switch`, `ProgressBar` and `Badge`.
- Overlays: `Dialog`, `Menu`, `MenuItem`, `MenuTrigger`, `MenuPopover`, `MenuSeparator`, `ContextMenuPopover`, `Tooltip` and `TooltipTrigger`.
- Helpers: `UiProvider`, `useDeliberatePress`, `cx` and `uiTexts`.

The [API reference](/docs/reference/sdk/) has a page per component, generated from its TSDoc (for example [Button](/docs/reference/sdk/ui/button/) and [Dialog](/docs/reference/sdk/ui/dialog/)), and every token with its value on [tokens](/docs/reference/sdk/ui/tokens/).
