---
title: 'SDK overview'
description: 'The three npm packages, their versions and how they fit together.'
status: ready
audience: developer
sidebar:
  order: 1
---

Three npm packages share one version line and one MIT license: `@pithyx/sdk` is the API between an app and the desktop, `@pithyx/ui` is the desktop's React components and design tokens, and `@pithyx/cli` is the `pithyx` command that checks, develops and packages apps. The system itself is AGPL-3.0.

## Packages

| Package       | What it is                                                                                     | Needs React |
| ------------- | ---------------------------------------------------------------------------------------------- | ----------- |
| `@pithyx/sdk` | `connect()` and the `PithyxApp` API; JSON schemas for manifests, Compose rules and catalogs    | no          |
| `@pithyx/ui`  | Buttons, fields, menus, dialogs and `tokens.css`, built on React Aria Components               | yes (19)    |
| `@pithyx/cli` | The `pithyx` command (`validate`, `lock`, `build`, `pack`, `dev`, `catalog`) and a Vite plugin | no          |

- **`@pithyx/sdk`** has the entry point `.`, the subpath `./i18n` (a small message helper for apps) and `./host` (the desktop's side, not for apps). The schemas are under `@pithyx/sdk/schema/`. It works with any framework or plain HTML. See [@pithyx/sdk](/docs/sdk/sdk/).
- **`@pithyx/ui`** exports the components, `@pithyx/ui/native` (class names for plain HTML elements) and `@pithyx/ui/tokens.css`. See [@pithyx/ui](/docs/sdk/ui/).
- **`@pithyx/cli`** exports the validators as a JavaScript API, `@pithyx/cli/vite` and a template icon. It needs Node.js 22.12 or newer. See [@pithyx/cli](/docs/sdk/cli/).

Core apps and third-party apps use the same SDK API. Core apps connect in-process, third-party apps through a `MessageChannel` from their sandboxed iframe.

## Versions and channels

All three packages carry the same version, independent of the Pithyx version. They are release candidates of 1.0.0 (`1.0.0-rc.<n>`) whose API can still change before 1.0.0. Release candidates are published under the npm dist-tag `next`.

Compatibility rules (from `sdk/src/version.ts`):

- A new method, event or optional field is a minor change; removing or changing one is a major change.
- A deprecated feature keeps working through the next major version.
- A desktop of major version N+1 still serves apps built for N.
- The wire protocol has its own version (`PROTOCOL_VERSION`, currently 1). Additions inside a version are found through `app.supports(method)` and `app.context.capabilities`.
- The manifest schema, the Compose rules and the catalog formats are stable contracts: loosening is a minor change, everything else a major one.

## Installing

```sh
npm install @pithyx/sdk@next
npm install @pithyx/ui@next react react-dom
npm install --save-dev @pithyx/cli@next
```

The packages are release candidates of 1.0 under the npm dist-tag `next`, which these commands name; `latest` may still point to an older candidate until 1.0.0 is out. The packages ship TypeScript types with TSDoc comments; in the repository, `pnpm docs:api` builds an API reference of `@pithyx/sdk` and `@pithyx/ui` with TypeDoc. Start with [Your first app](/docs/developers/apps/first-app/).
