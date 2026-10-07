# @pithyx/cli

The `pithyx` command for building apps for [Pithyx](https://github.com/pithyx/pithyx), the free, self-hosted NAS and server OS with a web desktop: it checks an app the way the box checks it, packs it into the file the box installs, and runs it on your machine in a mock of the desktop. A Vite plugin wires the same mock into `vite`.

> **Release candidate.** `1.0.0-rc.*` versions are published under the dist-tag `next`. The commands may still change before `1.0.0`.

## Install

```sh
npm install --save-dev @pithyx/cli@next
```

## An app project

```
pithyx.json        the manifest (schema: @pithyx/sdk/schema/manifest.v1.schema.json)
icon.svg           the app's icon (square viewBox, at most 64 KB, nothing that runs)
dist/              the built frontend; the manifest's entry frontend/index.html is dist/index.html
backend/compose.yaml        optional: the backend, a subset of Compose with images pinned by digest
backend/images.lock.json    written by pithyx lock
backend/compose.dev.yaml    optional: only for pithyx dev, for example with build:
README.md, LICENSE optional, packed as they are
```

## Commands

| Command                 | What it does                                                                                                                                                                                                 |
| ----------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `pithyx validate [dir]` | Checks the manifest, the icon, the entry in the build, the package layout, the Compose file and the images lock with the same rules and error codes as the box. Warns while `icon.svg` is the template icon. |
| `pithyx lock [dir]`     | Resolves the images of `backend/compose.yaml` from their registries into `backend/images.lock.json`: the platform manifest and image id for `amd64` and `arm64`.                                             |
| `pithyx pack [dir]`     | Validates, then writes `<id>-<version>.pithyxapp`. The same files always give the same bytes. `--images` adds the images for offline installs, saved with the local Docker.                                  |
| `pithyx dev [dir]`      | Serves the built app in the mock desktop on `http://127.0.0.1:7400/`, with a proxy to the backend started by `docker compose`.                                                                               |

`pithyx --help` lists every option.

## The mock desktop

The app runs in a window with the box's sandbox (`sandbox="allow-scripts allow-forms"`, no `allow-same-origin`) and the box's `Content-Security-Policy`, and talks to the desktop over the real protocol of `@pithyx/sdk`. Switches change the user (an administrator and two users with groups), the theme and accent color, the language, the time zone, the size class, the connection, debug logging, the backend status, permissions and storage; a button sends each event and each question with data you can edit. Notifications, badges, attention, context menus, the clipboard, per-user storage and picked files work as on the box.

Requests to `/apps/<id>/api/` and `/apps/<id>/api-<name>/` go through a proxy that behaves like the box's: it needs a backend token (`Authorization: Bearer` or `?pithyx-token=`) or, for a WebSocket, a one-time ticket (`?pithyx-ticket=`); a cookie never counts. It removes `Cookie`, `Authorization`, the `pithyx-*` parameters and every incoming `X-Pithyx-*` header, and sets `X-Pithyx-User`, `X-Pithyx-User-Id`, `X-Pithyx-Groups` (group names, separated by commas), `X-Pithyx-Admin` (`true` or `false`), `traceparent` and `X-Forwarded-Prefix`. Your backend can trust these headers on the box as well.

There is no development mode on the box: to try an app there, upload its package in the Store.

## Vite

```ts
// vite.config.ts
import { defineConfig } from 'vite';
import pithyx from '@pithyx/cli/vite';

export default defineConfig({ plugins: [pithyx()] });
```

`vite` then serves the app with hot reload under `/apps/<id>/` and the mock desktop at `/`; `vite build` writes relative asset paths and fails when the build would not pass `pithyx validate`. Options: `endpoints` (reach a backend endpoint at a URL instead of through Docker), `docker: false`, `validate: false`.

## Versions

`@pithyx/sdk`, `@pithyx/ui` and `@pithyx/cli` share one SemVer line, separate from the Pithyx product version.

## License

MIT, see [LICENSE](./LICENSE). Pithyx itself is AGPL-3.0.
