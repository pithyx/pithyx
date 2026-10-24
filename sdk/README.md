# @pithyx/sdk

[![npm](https://img.shields.io/npm/v/@pithyx/sdk)](https://www.npmjs.com/package/@pithyx/sdk)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](./LICENSE)

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
npm install @pithyx/sdk
```

## Related packages

| Package                                                    | What it is                                              |
| ---------------------------------------------------------- | ------------------------------------------------------- |
| [`@pithyx/ui`](https://www.npmjs.com/package/@pithyx/ui)   | The React components and design tokens of the desktop   |
| [`@pithyx/cli`](https://www.npmjs.com/package/@pithyx/cli) | The `pithyx` command: validate, pack and a mock desktop |

## Entry points

| Import                      | What it is                                                   |
| --------------------------- | ------------------------------------------------------------ |
| `@pithyx/sdk`               | The app API                                                  |
| `@pithyx/sdk/host`          | The desktop's side of the protocol; apps do not use it       |
| `@pithyx/sdk/i18n`          | An optional helper for ICU message texts                     |
| `@pithyx/sdk/schema/*.json` | The JSON schemas of the manifest, Compose rules and catalogs |

## Documentation

The full documentation is on the docs site at [pithyx.org](https://pithyx.org):

- [The @pithyx/sdk reference](https://pithyx.org/docs/sdk/sdk/)
- [SDK overview](https://pithyx.org/docs/sdk/overview/)
- [Apps overview](https://pithyx.org/docs/developers/apps/overview/)
- [Your first app](https://pithyx.org/docs/developers/apps/first-app/)

Every page is also available as Markdown (add `.md` to its path), and [pithyx.org/llms.txt](https://pithyx.org/llms.txt) indexes them all.

## Versions

`@pithyx/sdk`, `@pithyx/ui` and `@pithyx/cli` share one version line, separate from the Pithyx product version, and follow SemVer since `1.0.0`: a breaking change needs a major version. Releases are published under the dist-tag `latest`; a release candidate of a later version goes to `next` first.

## License

MIT, see [LICENSE](./LICENSE). Pithyx itself is AGPL-3.0.
