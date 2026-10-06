# @pithyx/sdk

The SDK for apps on [Pithyx](https://github.com/pithyx/pithyx), the free, self-hosted NAS and server OS with a web desktop. Every Pithyx app uses it, the system apps as well as third-party apps: it is the only way an app talks to the desktop and to the box.

An app is a static frontend that runs in a sandboxed window of the Pithyx desktop, plus an optional backend of containers. This package covers the frontend side: the window, notifications, files, per-user storage, logs, the signed-in user's context, and requests to the app's own backend through the box's proxy.

> **Release candidate.** `1.0.0-rc.*` versions are published under the dist-tag `next`. The API may still change before `1.0.0`.

## Install

```sh
npm install @pithyx/sdk@next
```

## Use

```ts
import { connect } from '@pithyx/sdk';

const app = await connect();

await app.window.setTitle(`Hello, ${app.context.user.displayName ?? app.context.user.name}`);
app.on('themeChange', () => render());
app.answer('beforeClose', () => ({ allow: !hasUnsavedChanges() }));

// The app's own backend, with a token for the signed-in user.
const messages = await (await app.backend.fetch('/messages')).json();
const live = await app.backend.socket('/live');

app.log.info('Started', { messages: messages.length });
```

`connect()` waits for the desktop that framed the app. Outside the desktop it rejects with `sdk.transport.closed`.

Every call returns a promise that rejects with a `PithyxError` carrying a stable `code` (for example `app.permission.denied`), `params` for the translated message, and an English `detail`.

## Entry points

| Import             | What it is                                                                      |
| ------------------ | ------------------------------------------------------------------------------- |
| `@pithyx/sdk`      | `connect()`, the app API, the protocol and manifest types, theme and formatters |
| `@pithyx/sdk/i18n` | An optional helper for ICU MessageFormat texts (`createI18n`)                   |
| `@pithyx/sdk/host` | The desktop's side of the protocol; apps do not use it                          |

## Permissions

An app declares its permissions in its manifest (`pithyx.json`); an administrator confirms them at install and can revoke them later (`permissionsChange`). The box checks them on every call. The user's own context (name, groups, language, time zone, theme) needs no permission; listing all users and groups needs `directory.read`.

## Versions

`@pithyx/sdk`, `@pithyx/ui` and `@pithyx/cli` share one SemVer line, separate from the Pithyx product version. A new method, event or optional field is a minor release; a removed or changed one is a major release. `app.supports(method)` tells whether the desktop an app runs on knows a method.

## License

MIT, see [LICENSE](./LICENSE). Pithyx itself is AGPL-3.0.
