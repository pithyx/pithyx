# @pithyx/sdk

[![npm](https://img.shields.io/npm/v/@pithyx/sdk/next?label=npm%40next)](https://www.npmjs.com/package/@pithyx/sdk)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](./LICENSE)

> **Work in progress.** This is a release candidate of the first SDK. The API can still change before `1.0.0`, and the documentation, guides and examples come with the first Pithyx release. Until then this page only says what the package is.

The SDK for apps on [Pithyx](https://github.com/pithyx/pithyx), the free, self-hosted NAS and server OS with a web desktop. Every Pithyx app uses it, the system apps as well as third-party apps: it is the only way an app talks to the desktop and to the box.

## What it covers

- The app's window, its title, size and lifecycle events
- Notifications, badges and context menus drawn by the desktop
- Files the user shares with the app, and per-user storage kept by the box
- The signed-in user's context: name, groups, language, time zone and theme
- Logs that reach the box's Log Center
- Requests to the app's own backend through the box's proxy

Every call can fail with a stable error code, and the box checks the app's permissions on every call.

## Install

```sh
npm install @pithyx/sdk@next
```

## Related packages

| Package                                                    | What it is                                              |
| ---------------------------------------------------------- | ------------------------------------------------------- |
| [`@pithyx/ui`](https://www.npmjs.com/package/@pithyx/ui)   | The React components and design tokens of the desktop   |
| [`@pithyx/cli`](https://www.npmjs.com/package/@pithyx/cli) | The `pithyx` command: validate, pack and a mock desktop |

## Versions

`@pithyx/sdk`, `@pithyx/ui` and `@pithyx/cli` share one version line, separate from the Pithyx product version. Release candidates are published under the dist-tag `next`.

## License

MIT, see [LICENSE](./LICENSE). Pithyx itself is AGPL-3.0.
