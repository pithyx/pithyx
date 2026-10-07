# @pithyx/ui

The React components and design tokens of the [Pithyx](https://github.com/pithyx/pithyx) desktop, for Pithyx apps. The system apps use them, and third-party apps can too, so every app looks and behaves like part of the desktop: keyboard, touch, screen readers and the light and dark themes included.

> **Release candidate.** `1.0.0-rc.*` versions are published under the dist-tag `next`. The API may still change before `1.0.0`.

## Install

```sh
npm install @pithyx/ui@next react react-dom
```

## Use

```tsx
import '@pithyx/ui/tokens.css';
import type { PithyxApp } from '@pithyx/sdk';
import { Button, TextField, UiProvider } from '@pithyx/ui';

export function Rename({ app }: { app: PithyxApp }) {
  return (
    <UiProvider locale={app.context.locale}>
      <TextField label="Name" />
      <Button variant="primary">Save</Button>
    </UiProvider>
  );
}
```

`UiProvider` sets the language of the few texts the components show themselves (English and German, English for any other language) and the locale for dates and numbers. The components take their colors, fonts and sizes from `tokens.css`, which follows the system's light or dark theme unless `<html data-theme="light|dark">` overrides it; `<html data-accent="...">` picks one of the accent presets. In a Pithyx app, `@pithyx/sdk` sets the desktop's own values as `--pithyx-*` properties as well.

## Entry points

| Import                  | What it is                                                                                                                                                         |
| ----------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `@pithyx/ui`            | Button, IconButton, TextField, PasswordField, OtpField, SearchField, Checkbox, Switch, ProgressBar, Badge, Dialog, Menu, ContextMenuPopover, Tooltip, `UiProvider` |
| `@pithyx/ui/native`     | The class names and texts for native elements without React Aria, for code that has to stay small                                                                  |
| `@pithyx/ui/tokens.css` | The design tokens, usable without React                                                                                                                            |

The components are built on [React Aria Components](https://react-spectrum.adobe.com/react-aria/); `react` and `react-dom` 19 are peer dependencies.

## Versions

`@pithyx/sdk`, `@pithyx/ui` and `@pithyx/cli` share one SemVer line, separate from the Pithyx product version.

## License

MIT, see [LICENSE](./LICENSE). Pithyx itself is AGPL-3.0.
