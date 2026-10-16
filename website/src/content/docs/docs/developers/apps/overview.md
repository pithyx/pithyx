---
title: 'Apps overview'
description: 'What a Pithyx app is: a static frontend, an optional backend, a manifest and a package.'
status: ready
audience: developer
sidebar:
  order: 1
---

A Pithyx app is a static frontend that the daemon serves, plus an optional backend of zero or more containers, described by a manifest (`pithyx.json`) and shipped as one package file. The frontend runs in a sandboxed iframe on the desktop and talks to the box only through `@pithyx/sdk`. The box never builds anything: it installs the finished package.

## Frontend and backend

- **Frontend.** Static files under `/apps/<id>/...`, served only with `GET` and `HEAD`. The manifest field `entry` names the HTML page inside `frontend/`. The desktop opens it in a window.
- **Backend.** Optional. A subset of Docker Compose, started by the daemon and reachable only through the daemon's proxy at `/apps/<id>/api/` (and `/apps/<id>/api-<name>/` for further endpoints). No host ports are ever published. One backend instance serves all users; it learns who is calling from headers set by the proxy. See [Backends](/docs/developers/apps/backend/).
- **No backend.** An app without a `backend` key runs in the browser only. The install dialog says so.
- **Reserved paths.** A frontend path whose first segment is `api` or starts with `api-` belongs to the backend and returns 404 for files.
- **Icon.** Always `icon.svg` at the package root (no manifest field), served at `/api/apps/{id}/icon`. At most 64 KB, a valid SVG with a square viewBox and no scripts, `foreignObject`, images or iframes.

Pithyx does not support web apps with their own port, apps that talk to other apps, desktop widgets or app code without a window. A backend is the only code that runs without a window.

## The sandbox

A third-party app runs in `<iframe sandbox="allow-scripts allow-forms">` on the same host as the desktop, without `allow-same-origin`. The app has an opaque origin, receives no cookies, and cannot use `localStorage` or IndexedDB meaningfully. It stores data through the SDK (`app.storage`) and files through `app.files`.

The daemon adds these headers to every answer under `/apps/<id>/`, including 404 and proxied backend answers (frontend files also get `Cache-Control`, see below):

```
Content-Security-Policy: sandbox allow-scripts allow-forms; default-src 'self'; script-src 'self';
  style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:;
  media-src 'self' data: blob:; worker-src 'self' blob:; object-src 'none'; base-uri 'none';
  form-action 'self'; frame-ancestors 'self'; connect-src 'self'
X-Content-Type-Options: nosniff
Access-Control-Allow-Origin: *
Referrer-Policy: no-referrer
```

With the permission `network.internet` the policy ends in `connect-src 'self' https:`. Consequences:

- No inline scripts and no scripts from a CDN. Inline styles are allowed.
- Images, fonts and media come from the app itself, or from `data:` and `blob:` URLs, even with the internet permission.
- Calls to other servers (`fetch`, WebSocket) only with `network.internet`.

`pithyx dev` serves the same policy, so a blocked request shows up during development.

Frontend files are as public as the desktop's own files; access is decided when the window gets its tokens. Permissions are checked by the daemon on every call.

### Compression and caching

At install the box writes a Brotli and a gzip copy of every text file of the frontend from 1 KB on (JavaScript, CSS, HTML, JSON, source maps, SVG, text, XML, WebAssembly) and serves the copy the browser accepts, with `Content-Encoding` and `Vary: Accept-Encoding`. It compresses only what the package holds; it never builds.

A file under `assets/` whose name ends in a build hash as Vite writes it (`assets/index-B4kz_x9Q.js`: a hyphen, eight characters of letters, digits, `_` or `-`, at least one of them not a lowercase letter) gets `Cache-Control: public, max-age=31536000, immutable`; every other file keeps `no-cache`. The browser keeps hashed files across windows and reloads, but it does not revalidate `no-cache` files of a sandboxed window, so those load again for every window. So:

- Keep Vite's default file names under `assets/`; everything the entry page loads should be hashed.
- Never put a file under `assets/` whose name looks like a hash but whose content changes between versions: users would keep the old file for a year.
- Keep the entry page small; it loads with every window.

## App ids

An id is the author's domain reversed plus the app name, three to five parts, for example `com.mueller.chat` or `io.github.jan.chat`.

- Each part uses lowercase letters, digits and hyphens; the first part starts with a letter. The whole id is at most 100 characters. Pattern: `^[a-z][a-z0-9-]*(\.[a-z0-9][a-z0-9-]*){2,4}$`.
- `org.pithyx.*` is reserved for system apps and the official catalog. The box refuses it for a sideloaded package ([`app.manifest.id_reserved`](/docs/reference/errors/#app.manifest.id_reserved)).
- An invalid id gives [`app.manifest.id_invalid`](/docs/reference/errors/#app.manifest.id_invalid).
- Ids are unique on a box.

## The package

A package is named `<id>-<version>.pithyxapp`. It is a gzip-compressed tar archive, and it is deterministic: the same files give the same bytes (manifest first, icon second, the rest sorted, fixed mode, owner and times). It contains:

- `pithyx.json` and `icon.svg`,
- `frontend/` with the built frontend,
- optionally `backend/compose.yaml` and `backend/images.lock.json`, and `backend/images/<arch>/<config digest>.tar` for offline installs (named after the image config's digest, which is the image id in Docker's classic image store, not after the manifest digest that `compose.yaml` pins),
- the README and LICENSE files if they exist,
- `CHECKSUMS` in `sha256sum -c` format.

Path rules: relative paths with `/`, ASCII letters, digits and `._~@+-`, no empty segment and no segment starting with a dot, at most 512 characters per path, 255 per segment and 32 segments. Names that differ only in case are refused.

Limits when a package is uploaded to a box: 256 MB for the file, 512 MB unpacked, 10,000 entries and 64 KB for `pithyx.json`. Only an administrator can upload (`PUT /api/apps/packages`, kept for 15 minutes), then install. A package installed from a file is marked as a sideload and shown as not verified.

Build packages with the command line tool: see [@pithyx/cli](/docs/sdk/cli/) and [Your first app](/docs/developers/apps/first-app/).

## SDK guides

How a frontend works with the box, task by task:

- [Files and app storage](/docs/sdk/guides/files-and-storage/)
- [Calling the backend](/docs/sdk/guides/backend/)
- [Events, window context and launch arguments](/docs/sdk/guides/events-and-context/)
- [Permissions and permissionsChange](/docs/sdk/guides/permissions/)
- [Languages and theming in an app](/docs/sdk/guides/i18n-and-theming/)
- [Logging and errors](/docs/sdk/guides/logging-and-errors/)
- [Testing with pithyx dev and the mock desktop](/docs/sdk/guides/testing/)
