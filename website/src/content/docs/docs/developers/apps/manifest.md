---
title: 'The manifest'
description: 'Every field of pithyx.json: identity, windows, permissions, backend and lifecycle.'
status: ready
audience: developer
sidebar:
  order: 3
---

`pithyx.json` at the package root describes an app: who it is, which window it opens, which permissions it asks for, which folders it wants and, if it has one, how its backend runs. The JSON Schema is `sdk/schema/manifest.v2.schema.json` (published as `@pithyx/sdk/schema/manifest.v2.schema.json`); unknown fields are errors. The schema is a stable contract: a new optional field is a minor change, anything else a major one. Manifest version 2 (Pithyx 1.2, D388) replaced version 1: a box refuses a version 1 manifest with [`app.manifest.version_unsupported`](/docs/reference/errors/#app.manifest.version_unsupported), so rebuild an older app with `@pithyx/cli` 2. Apps already installed keep working; the box converted their stored manifests.

Required fields: `manifestVersion`, `id`, `version`, `name`, `author`, `entry`, `sdk`, `permissions`, `arch`, `source` and `window`. Optional: `$schema`, `description`, `license`, `homepage`, `pithyx`, `dataVersion`, `storage`, `fileHandlers`, `fileActions`, `backend`, [`notifications`](#notification-categories) and the [Store information](#store-information) (`privacy`, `security`, `maturity`, `categories`, `keywords`, `locales`, `screenshots`, `releaseNotes`, `requirements`, `audience`). See also the [schema reference](/docs/reference/manifest-schema/) with every field and the [Compose rules](/docs/reference/compose-rules/) for the backend.

```json
{
  "$schema": "./node_modules/@pithyx/sdk/schema/manifest.v2.schema.json",
  "manifestVersion": 2,
  "id": "com.example.notes",
  "version": "1.0.0",
  "name": { "en": "Notes", "de": "Notizen" },
  "author": { "name": "Example Author" },
  "entry": "frontend/index.html",
  "sdk": "^1.0.0",
  "arch": ["amd64", "arm64"],
  "source": "https://github.com/example/notes",
  "permissions": {
    "notifications": {
      "reason": {
        "en": "Reminds you of notes with a due date.",
        "de": "Erinnert dich an fällige Notizen."
      }
    }
  },
  "window": {
    "instances": "single",
    "defaultSize": { "width": 800, "height": 600 },
    "minSize": { "width": 400, "height": 300 }
  }
}
```

## Identity

- `manifestVersion`: the number `2`.
- `id`: see [App ids](/docs/developers/apps/overview/#app-ids).
- `version`: SemVer 2.0 without build metadata, at most 64 characters (`1.2.0`, `2.0.0-beta.1`).
- `name`: an object keyed by language tag (`en`, `de`, `pt-BR`). `en` is required; each text is 1 to 60 characters on one line. The desktop uses the user's language, then its base language, then `en`.
- `description`: optional, same keys, 1 to 500 characters, line breaks allowed.
- `author`: `name` (1 to 100 characters) and an optional `url` (http or https).
- `license`: optional SPDX expression. Catalogs require it.
- `homepage`: optional http or https URL.
- `source`: required. The URL of the app's source repository, or `private` when the source is not public. The Store shows "Open source" with the `license` for a public source and "Source not public" otherwise.
- `arch`: required list of the processor architectures the app runs on, `amd64` and `arm64`. Every image of the backend must exist for each of them; an app without a backend usually names both. A box of another architecture refuses the install with [`app.arch.unsupported`](/docs/reference/errors/#app.arch.unsupported), because the app cannot run there. That is the only kind of refusal: what an app merely recommends is a hint.
- `entry`: the HTML page, `frontend/<path>.html`, at most 200 characters, no segment starting with a dot.
- `sdk`: required npm-style version range for the SDK, for example `^1.0.0`. The box checks its syntax ([`app.manifest.sdk_invalid`](/docs/reference/errors/#app.manifest.sdk_invalid)); it does not compare it with the desktop's protocol today. An app built during the release candidates may keep `^1.0.0-rc.8`: that range also covers 1.0.0 and every later 1.x.
- `pithyx`: optional range of box versions the app works with. The box installs the app only if its own version is inside. Catalogs require it. Prerelease versions match by plain SemVer ordering, so `>=0.1.0` includes `0.2.0-beta.1`, but `^0.1.0` does not.
- `dataVersion`: optional integer from 1 to 2147483647. A catalog downgrade is allowed only between versions with the same `dataVersion`. A rollback to the previous version after an update does not depend on it.

## Store information

Optional fields (D389) that the Store shows to administrators before and after the install. None of them changes what the app may do, and none stops an install: what an app recommends is a hint.

- `privacy`: what the app sends out of the box, `{ "sends": ..., "note": {...} }`. `sends` is `none` (nothing), `optional` (only what a user switches on), `telemetry` (usage or crash data to you) or `vendorAccount` (it needs an account at a service outside the box); `note` says more, localized like `description`. The Store shows it in plain words; without it the Store says the author does not tell. Catalogs require it.
- `security`: where to report a vulnerability, an `https` URL or a `mailto:` address. `pithyx validate` warns without it when the app asks for a right of high risk or root access (`cli.manifest.security_missing`).
- `maturity`: `stable` (also without the field), `beta` or `experimental`. The Store shows the last two as a badge.
- `categories`: one to three of `productivity`, `office`, `communication`, `files`, `backup`, `photos`, `media`, `home-automation`, `network`, `security`, `monitoring`, `development`, `education`, `finance`, `games` and `utilities`. The catalog tabs filter by them.
- `keywords`: up to 10 words in any language (1 to 30 characters) that the Store's search finds the app by, next to its id, name, description and author.
- `locales`: the languages of the app's own interface as BCP 47 tags (`en`, `pt-BR`). The Store says "Available in German" when one matches the user's language or its base.
- `screenshots`: up to 8 `{ "file": "frontend/<path>.png", "caption": {...} }`. The files are part of the frontend (PNG, JPEG or WebP, at most 1 MiB each; the box and `pithyx validate` check both with [`app.package.layout_invalid`](/docs/reference/errors/#app.package.layout_invalid)), so an installed app serves them itself and its page in the Store shows them. A catalog keeps its own `screenshots/` folder for apps that are not installed yet.
- `releaseNotes`: what is new in this version, localized like `description`. The update dialog and the **Updates** tab show it, which matters most for apps that never update by themselves.
- `requirements`: `memory` and `disk` in MB, about what the app uses. The install dialog warns when the box has less memory or the app pool less free space, and installs anyway.
- `audience`: `everyone` or `admins`, the default of "Who may use it" at install. An app that may see every user, runs as root or asks for a special right is suggested for administrators either way; the administrator can change it.

```json
"privacy": { "sends": "none" },
"security": "mailto:security@example.com",
"maturity": "beta",
"categories": ["productivity"],
"keywords": ["todo", "Aufgaben"],
"locales": ["en", "de"],
"screenshots": [{ "file": "frontend/screenshots/main.png", "caption": { "en": "Your notes" } }],
"releaseNotes": { "en": "Notes can have a due date." },
"requirements": { "memory": 128, "disk": 50 },
"audience": "everyone"
```

## Notification categories

`notifications.categories` (optional, D390) names up to 16 kinds of notifications the app sends, each `{ "id", "name", "description"? }` with an `id` of lowercase letters, digits and dashes (at most 32 characters, unique in the app) and a localized `name`. It needs the permission `notifications` ([`app.manifest.permission_missing`](/docs/reference/errors/#app.manifest.permission_missing)). `app.notify({ ..., category: "mentions" })` names the kind; a category the manifest does not declare is refused with [`notifications.message.invalid`](/docs/reference/errors/#notifications.message.invalid). Each user can switch a kind off under **Notification channels**: its notifications then arrive as read, without a toast, email or webhook. A notification without a category is never muted.

```json
"notifications": {
  "categories": [
    { "id": "messages", "name": { "en": "New messages", "de": "Neue Nachrichten" } },
    { "id": "mentions", "name": { "en": "Mentions", "de": "Erwähnungen" } }
  ]
}
```

## Windows

`window` is required:

- `instances`: `single` focuses the user's existing window on this device and passes launch arguments; `multi` opens a new window each time.
- `defaultSize` and `minSize`: `width` and `height` as integers from 100 to 8192 CSS pixels. `minSize` may not be larger than `defaultSize` in either direction. The default size is used when the user has no remembered size; the minimum applies on screens that can fit it.
- `loading` (optional): `text`, the line a new window shows while the backend starts, localized like `name` (up to 60 characters; without it the desktop says "Starting <name> ..."), and `untilReady`, whether the placeholder waits for `app.window.ready()` after the app connected. See [Events and context](/docs/sdk/guides/events-and-context/#loading).
- `suggestAtSignIn` (optional, default false): the app suggests opening after sign-in, for an app people keep open all day such as a chat. The desktop marks it as suggested where users choose the apps that open after signing in (account menu, **Open at sign-in**); it never opens the app on its own.

`fileHandlers` (optional, up to 32) makes the app appear under "Open with" for file types. Each handler has `extensions` (1 to 32 lowercase extensions without a dot, such as `png` or `tar.gz`), and optionally `mimeTypes` (1 to 32) and `isDefault`. With `isDefault`, a double click in Files opens the type in your app, as long as no other installed app sets `isDefault` for it and the user has not chosen another way with "Always open .x files this way". An extension can belong to one handler only. The file reaches the app through the `launch` or `openFile` event.

`fileActions` (optional, up to 16, D375) adds entries to the context menu of files and folders in Files, for example "Extract here" on archives or "Compress to ZIP" on a selection. Each action has an `id` (unique in the app), a `label` like `name`, and says what it applies to: `extensions` (files with these extensions), `folders: true`, or both. `multiple: true` offers it for several selected items at once (otherwise only for exactly one), and `writes: true` only where the user may change files. The desktop shows an action only when every selected item matches. Choosing it opens your app with `launch.action`: the action's `id`, the share as `location`, the `folder` and the `names` of the selected items. Your app reads and writes them with the files API in that share, with the user's rights, so `fileActions` needs the permission `files.shares` (the box refuses the manifest without it with [`app.manifest.permission_missing`](/docs/reference/errors/#app.manifest.permission_missing)).

## Permissions

`permissions` is a required map (it can be empty, `{}`) from the name of a permission or a special right to what the app says about it. Every entry needs a `reason`: why the app needs it, in your words, localized like `name` (`en` required, one line, at most 200 characters). The install dialog and the Store page show it next to the right; `pithyx validate` and the box refuse a manifest without it.

```json
"permissions": {
  "network.internet": {
    "reason": { "en": "Fetches the weather for your city.", "de": "Lädt das Wetter für deine Stadt." },
    "hosts": ["api.open-meteo.com"]
  },
  "device:/dev/dri": {
    "reason": { "en": "Converts videos faster with the graphics card." },
    "optional": true
  }
}
```

`hosts` is allowed only on `network.internet`: the domains the app talks to (`api.example.org`, or `*.example.org` for its subdomains), shown in the Store; the box does not enforce them yet. `optional` is allowed only on a device right (see [special rights](#special-rights)). The permissions are:

| Permission               | Lets the app                                                                                                                                         |
| ------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------- |
| `files.read`             | read files the user opens with it or picks in a desktop file dialog                                                                                  |
| `files.write`            | write those files and save new files through the desktop save dialog                                                                                 |
| `files.shares`           | browse every shared folder the user may access, with the user's own rights                                                                           |
| `notifications`          | show notifications                                                                                                                                   |
| `notifications.external` | lets them also reach the users' email and webhook channels, together with `notifications`                                                            |
| `directory.read`         | list all users and groups                                                                                                                            |
| `network.internet`       | make outgoing connections to the internet from its window (`connect-src https:`) and its backend                                                     |
| `network.lan`            | make outgoing connections from its backend to other devices in the local network (private, shared and link-local addresses); never to the box itself |

An administrator confirms the list at install and can revoke each permission later; the app then receives a `permissionsChange` event. A permission that is new in an update waits for the administrator, and a revoked permission stays revoked across updates. Calling something without the permission fails with [`app.permission.denied`](/docs/reference/errors/#app.permission.denied).

An app that asks for `directory.read`, runs a container as root (`backend.root`) or asks for a special right is suggested for administrators only: the install dialog preselects **Administrators only** and says why. So does `"audience": "admins"` (see [Store information](#store-information)). The administrator can still let everybody use it.

### Special rights

The same map holds the rights beyond the sandbox, for the backend only (D311, D387): `host.network`, `host.pid`, `device:/dev/<path>`, `capability:<NAME>`, `docker.socket`, `docker.socket.raw` and `host.full`. Each needs a `backend` ([`app.elevated.backend_missing`](/docs/reference/errors/#app.elevated.backend_missing)). The box sorts each into a risk tier (D388, `elevated.tiers` in the [Compose rules](/docs/reference/compose-rules/#elevated-rights)), and the tier decides what an administrator does to grant it:

| Tier          | Rights                                                                                  | Granting it                                                                                       |
| ------------- | --------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------- |
| Device access | `device:/dev/net/tun`, `device:/dev/dri`, `device:/dev/serial` and the nodes below them | a hint and a checkbox                                                                             |
| High risk     | `host.network`, `host.pid`, `capability:<NAME>`, `docker.socket`, every other device    | a risk text, a checkbox and the password (and TOTP)                                               |
| Root access   | `docker.socket.raw`, later `host.full`                                                  | a red step of its own with the consequences, the app's name typed in, and the password (and TOTP) |

An app with a root access right never updates by itself, whatever the box's update policy says, and every other administrator gets a notification when one is granted. `docker.socket.raw` is allowed from a file and from every catalog; if your app only manages containers it starts itself, ask for `docker.socket` instead (`pithyx validate` warns, `cli.rights.raw_socket`). `host.full` and blocked devices and capabilities are refused with [`app.elevated.unsupported`](/docs/reference/errors/#app.elevated.unsupported). With `root: true` an app cannot ask for `host.network`, `host.pid` or a device other than `/dev/net/tun` ([`app.elevated.root_conflict`](/docs/reference/errors/#app.elevated.root_conflict)), and a capability needs `root: true` ([`app.elevated.root_required`](/docs/reference/errors/#app.elevated.root_required)). The Compose file must use exactly the rights the map declares; details are under [Elevated rights](/docs/reference/compose-rules/#elevated-rights).

A device right with `optional: true` is one the backend runs without (for example `/dev/dri` for hardware acceleration). Every other device is required: without it the app cannot be installed ([`app.device.missing`](/docs/reference/errors/#app.device.missing)) and its backend does not start until the device is back.

### Folders

`storage` (optional, up to 16) asks for folders. Each request has an `id`, a `name`, an optional `description`, `access` (`read` or `readWrite`), `required` and, for backends, `services` (the services that get the folder). An administrator maps each request to a folder in a shared folder. The window addresses it as `{ storage: id }` in the SDK file API; a backend service sees a Compose volume with that name. An app cannot start until its required requests are mapped.

## Backend

`backend` is optional. Required inside it: `compose`, `endpoints` and `lifecycle`.

- `compose`: path of the Compose file in the package, usually `backend/compose.yaml`.
- `endpoints`: an object from endpoint name to `{ "service": "...", "port": 8080 }`, up to 16. The endpoint `api` is required and is the default. Names match `^[a-z][a-z0-9-]{0,31}$`, so at most 32 characters; that is the limit the box enforces. The SDK accepts longer names, but the box would refuse them. `port` (1 to 65535) is the container's port inside the app network and is never published.
- `lifecycle`: `always` or `onDemand`.
- `idleTimeout`: seconds from 60 to 86400, default 600; ignored for `always`.
- `root`: `true` runs the services as root inside the container (unprivileged on the host); default `false`. The install dialog warns. Services without root lose every Linux capability.
- `network`: `{ "services": ["fetcher"] }` names the services that get the network out of the box which `network.internet` and `network.lan` open; the others stay on the app's internal network. Without it every service gets it. Each name must be a service of the Compose file ([`app.compose.reference_unknown`](/docs/reference/errors/#app.compose.reference_unknown)).
- `health`: `{ "endpoint": "api", "path": "/health" }`. The daemon calls `GET` on it; a 2xx answer means the backend is running.
- `schedules`: up to 16 entries of `{ id, cron, endpoint, path }`. The daemon starts the backend and calls `POST path` with `X-Pithyx-Schedule` (see [Who is calling](/docs/developers/apps/backend/#who-is-calling)). The cron expression has five fields (minute 0 to 59, hour 0 to 23, day of month 1 to 31, month 1 to 12, weekday 0 to 7) in the box's time zone, with `*`, numbers, ranges, lists and steps; names are not allowed ([`app.manifest.cron_invalid`](/docs/reference/errors/#app.manifest.cron_invalid)).

The special rights the backend uses are declared in `permissions`, see [Special rights](#special-rights).

Beyond the schema the box checks that service and endpoint names exist ([`app.manifest.reference_unknown`](/docs/reference/errors/#app.manifest.reference_unknown)) and that the storage requests match the Compose mounts.

## Lifecycle

`lifecycle` decides when the backend runs. `always` starts it whenever it can run (the app is enabled, the pool is available, required folders are mapped). `onDemand` runs it while a window is open or a request or schedule holds it, and stops it `idleTimeout` seconds after the last one. The details are on [Backends](/docs/developers/apps/backend/#lifecycle).

## Versions

The box compares the installed and the uploaded version and shows install, update, reinstall or downgrade. Reinstalling the same version replaces its files; installing an older one warns that its data may not fit. The box keeps the previous version and a snapshot of the app's data until the next update so that an administrator can roll back. In a catalog, a published version is never changed or removed.
