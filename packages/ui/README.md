# @pithyx/ui

[![npm](https://img.shields.io/npm/v/@pithyx/ui/next?label=npm%40next)](https://www.npmjs.com/package/@pithyx/ui)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](./LICENSE)

> **Release candidate.** The API can still change before `1.0.0`.

The React components and design tokens of the [Pithyx](https://github.com/pithyx/pithyx) desktop, for Pithyx apps. The system apps use them, and third-party apps can too, so every app looks and behaves like part of the desktop.

## What it covers

- Buttons, text, password, code and search fields, checkboxes, switches, progress bars, badges, dialogs, menus and tooltips
- Keyboard, touch and screen reader support, built on React Aria Components
- The light and dark themes and the accent colors of the desktop
- `tokens.css`, the design tokens, usable without React
- Texts in English and German

## Install

```sh
npm install @pithyx/ui react react-dom
```

`react` and `react-dom` 19 are peer dependencies.

## Related packages

| Package                                                    | What it is                                              |
| ---------------------------------------------------------- | ------------------------------------------------------- |
| [`@pithyx/sdk`](https://www.npmjs.com/package/@pithyx/sdk) | The API between an app and the Pithyx desktop           |
| [`@pithyx/cli`](https://www.npmjs.com/package/@pithyx/cli) | The `pithyx` command: validate, pack and a mock desktop |

## Entry points

| Import                  | What it is                                                    |
| ----------------------- | ------------------------------------------------------------- |
| `@pithyx/ui`            | The React components                                          |
| `@pithyx/ui/native`     | Class names and texts for native elements, without React Aria |
| `@pithyx/ui/tokens.css` | The design tokens as CSS, usable without React                |

## Documentation

The full documentation is on the docs site at [pithyx.org](https://pithyx.org):

- [The @pithyx/ui reference](https://pithyx.org/docs/sdk/ui/)
- [SDK overview](https://pithyx.org/docs/sdk/overview/)
- [Your first app](https://pithyx.org/docs/developers/apps/first-app/)

Every page is also available as Markdown (add `.md` to its path), and [pithyx.org/llms.txt](https://pithyx.org/llms.txt) indexes them all.

## Versions

`@pithyx/sdk`, `@pithyx/ui` and `@pithyx/cli` share one version line, separate from the Pithyx product version. Release candidates are published under the dist-tag `next`.

## License

MIT, see [LICENSE](./LICENSE). Pithyx itself is AGPL-3.0.
