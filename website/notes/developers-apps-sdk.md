# Notes: developers/apps (5 pages) + sdk (4 pages)

Evidence paths relative to repo root. "S" = sdk/schema/manifest.v1.schema.json, "R" = sdk/schema/compose.v1.rules.json, "CLI" = packages/cli/src/cli.ts, "D" = daemon/src/Pithyx.Daemon/Apps.
Versions in package.json today: @pithyx/sdk, @pithyx/ui, @pithyx/cli all `1.0.0-rc.5` (sdk/package.json:3, packages/ui/package.json:3, packages/cli/package.json:3). Nothing about what is on npm is verifiable from code.

======================================================================

## developers/apps/overview.md

### Frontend and backend

- App = static frontend served by daemon + optional backend of 0..n containers (CLAUDE.md "Apps"; D300 TODO.md:1433).
- Frontend URL: `/apps/<id>/<path>`; served from installed version on the app pool; GET/HEAD only (405 `Allow: GET, HEAD` otherwise) (D/AppFrontendHosting.cs:56-62).
- Entry = HTML page inside `frontend/`, manifest field `entry` (S:57-62). Desktop loads it in the sandboxed iframe.
- Reserved frontend paths: first segment `api` or `api-*` belongs to the backend, 404 for files (D/AppFrontendHosting.cs:100; packages/cli/src/layout.ts:26-30; package check code `app.package.layout_invalid` reason `reserved`).
- Disabled/unavailable app, folder, symlink => 404 (AppFrontendHosting.cs:6-17, 107-108).
- Backend = Compose subset, reached ONLY through the daemon proxy (YARP) at `/apps/<id>/api/` (endpoint `api`) and `/apps/<id>/api-<name>/` (D/Proxy/AppProxy.cs:13-21, 160-166). No host ports.
- One backend instance per app, shared by all users; users separated by `X-Pithyx-User` header (CLAUDE.md "Apps"; D/Proxy/AppProxy.cs:232-235).
- App without backend: manifest has no `backend` key (S:104-106, optional). Install dialog: "Runs in the browser only, without a background service." / "Brings its own background service in containers." (i18n/en/store.json install.backend.\*).
- Icon: always `icon.svg` at package root, no manifest field; served at `/api/apps/{id}/icon` with `default-src 'none'; sandbox` (CLAUDE.md; D/AppEndpoints.cs:129). Icon rules: max 64 KB (packages/cli/src/icon.ts:4), valid SVG, square viewBox, no DOCTYPE/script/foreignObject/image/iframe etc. (icon.ts:9,40); errors `app.icon.invalid` reasons too_large|malformed|not_svg.
- Not in code (plan only): `kind: "web"` apps with own port and SSO "after SDK 1.0" (D300, TODO.md:1433; manifest.ts:12 says "Not in manifest version 1: apps that talk to other apps, app code without a window (backends excepted), web apps with their own port, desktop widgets"). Template repos `app-blank/app-chat/app-showcase` (D326) are private; nothing in the monorepo.

### The sandbox

- Iframe attribute: `sandbox="allow-scripts allow-forms"`, no `allow-same-origin` (desktop/src/apps/sandbox/SandboxApp.tsx:123). Same host as the desktop.
- Consequences: opaque origin, no cookies sent, no localStorage/IndexedDB use expected ("They store data through the SDK", CLAUDE.md). Right-click browser menu suppressed after `connect()` (sdk/src/index.ts:96-97).
- Headers on EVERY answer under `/apps/` (404 included, also proxied backend answers) (D/AppFrontendHosting.cs:28-40, 67):
  - `Content-Security-Policy: sandbox allow-scripts allow-forms; default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; media-src 'self' data: blob:; worker-src 'self' blob:; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'self'; connect-src 'self'` (AppFrontendHosting.cs:22-25)
  - with permission `network.internet`: ` https:` is appended after `connect-src 'self'` (=> `connect-src 'self' https:`) (AppFrontendHosting.cs:29)
  - `X-Content-Type-Options: nosniff`, `Access-Control-Allow-Origin: *`, `Referrer-Policy: no-referrer`, `Cache-Control: no-cache` (AppFrontendHosting.cs:38-41, 67)
  - So: no inline scripts, no CDN scripts, no remote images/fonts without... (img-src has no https: even with internet; only connect-src gets https:).
- Content types: from .NET FileExtensionContentTypeProvider; html/css/js get `; charset=utf-8`; unknown => application/octet-stream (AppFrontendHosting.cs:77-79).
- `pithyx dev` serves the identical policy (packages/cli/src/dev/frontend.ts:9-30; Vite mode adds a script nonce).
- Frontend files are public like the desktop's own; access is decided at launch/token (AppFrontendHosting.cs:13-15).
- Permissions are checked by daemon on every call (S:194 Permission description).

### App ids

- Rule: author's domain reversed + app name; 3 to 5 parts; each lowercase letters/digits/dashes; max 100 chars (S:117; D/Manifest/AppId.cs:6-9).
- Regex: `^[a-z][a-z0-9-]*(\.[a-z0-9][a-z0-9-]*){2,4}$` (S:120; AppId.cs:30). Note: first part must start with a letter; later parts may start with a digit; no underscore.
- Examples in schema: `com.mueller.chat`, `io.github.jan.chat` (S:117).
- `org.pithyx.*` reserved: refused for sideload, allowed only for system apps and the official catalog (AppId.cs:14-17; error `app.manifest.id_reserved`; CLI refuses too unless `allowReservedId`, packages/cli/src/manifest.ts:10,123).
- Unique per box. No `local.*` ids (CLAUDE.md).
- Error codes: `app.manifest.id_invalid` (message "...for example com.example.chat"), `app.manifest.id_reserved` (i18n/en/errors.json).
- Compose project name on the box: `pithyx-<id with dots as underscores>` (D/Compose/ComposeRewriter.cs: ProjectName). `pithyx dev` uses `pithyx-dev-<id_>` (packages/cli/src/dev/compose.ts:11).

### The package

- File name `<id>-<version>.pithyxapp` (packages/cli/src/pack.ts:14).
- Format: gzip-compressed ustar tar (pack.ts:35; tar.ts:75,108). Deterministic: manifest first, icon second, every other path sorted by code unit; fixed mode 0644, owner 0, fixed times, no gzip timestamp, gzip level best (tar.ts:44,75,117; project.ts:~104 packageOrder). "Same files give same bytes" (CLI HELP).
- Contents: `pithyx.json`, `icon.svg`, `frontend/**`, optional `backend/compose.yaml` + `backend/images.lock.json`, README/README.md/LICENSE/LICENSE.md/LICENSE.txt if they exist at project root, and `CHECKSUMS` (project.ts:17, 248: `<sha256>  <path>` lines in `sha256sum -c` format, sorted by path).
- Offline images (opt-in `pithyx pack --images`): `backend/images/<arch>/<config digest hex>.tar` (docker.ts:71,112).
- Path rules (packages/cli/src/layout.ts:6-24; same in D/Manifest/PackageLayout.cs): relative, `/` separator, ASCII letters/digits and `._~@+-`, no segment empty or starting with a dot, max 512 chars path, 255 per segment, 32 segments. Case-insensitive duplicates refused; file-vs-folder clash refused. `backend/` only allowed with a manifest backend; anything not `frontend/`/`backend/`/root files => reason `unexpected`. Required: `pithyx.json`, `icon.svg`, entry file, compose file.
- Hidden files (leading dot) in the frontend build are left out with warning `cli.frontend.skipped` (project.ts:66-71; findings.ts:30). Symlinks/special files in frontend => error `app.package.invalid` reason entry_type.
- Box limits on upload (D/Packages/PackageReader.cs:19): file max 256 MB, unpacked max 512 MB, max 10,000 entries, `pithyx.json` max 64 KB. Errors `app.package.too_large` (reasons size|unpacked|files|manifest), `app.package.invalid` (gzip, format, ...).
- Upload API: `PUT /api/apps/packages` raw body, admin only; kept 15 minutes for install by same admin (D/AppEndpoints.cs:69-73). Install `POST /api/apps/packages/{id}/install` -> 202 job; first install needs `pool`; update takes dataset snapshot and returns to it on failure (AppEndpoints.cs:86-89).
- Package origin stored: sideload vs catalog (CLAUDE.md). Sideload shown as "Installed from a file, not verified" (store.json installed.\*.sideload).

### Questions developers will ask

- Can my app use localStorage? Sandbox without allow-same-origin => use `app.storage` (SDK). Evidence SandboxApp.tsx:123; sdk/src/app.ts storage doc.
- Why is my CDN script blocked? CSP `script-src 'self'`; AppFrontendHosting.cs:22.
- Can the frontend call external APIs? Only with permission `network.internet` (connect-src https:). AppFrontendHosting.cs:29.
- Can I use inline `<script>`? No (script-src 'self'); inline styles OK (`style-src 'unsafe-inline'`).
- What id do I pick? see regex above; `org.pithyx.*` not possible.
- Max package size? 256 MB file / 512 MB unpacked (PackageReader.cs:19).
- Does the box build my app? No, "the box never runs npm or a compiler" (D301, CLAUDE.md).

======================================================================

## developers/apps/first-app.md

### Start from a template

- CLI has NO scaffold/create/init command (CLI:34-90 lists validate, lock, build, pack, dev, catalog). Icon `template-icon.svg` ships in CLI package (packages/cli/package.json files; project.ts:83-87) and `pithyx validate` warns `cli.icon.template` while icon.svg is still it (findings.ts:29).
- README: "the documentation, guides and app templates come with the first Pithyx release" (packages/cli/README.md:7). Templates `pithyx/app-blank`, `app-chat`, `app-showcase` private since 2026-10-08, local clones only (CLAUDE.md "Current state"; D326 TODO.md:1461). Not readable here: say "unknown" for their layout.
- Vite plugin default: `import pithyx from '@pithyx/cli/vite'; defineConfig({ plugins: [pithyx()] })` (packages/cli/src/vite.ts:5-8). Peer dep `vite >=7`, optional (packages/cli/package.json:64-71). Node >=22.12.0 (package.json engines).
- Minimal manifest needs: manifestVersion 1, id, version, name{en}, author{name}, entry, sdk, permissions[], window{instances,defaultSize,minSize} (S:8-18). See manifest page.
- Project layout the CLI expects: `pithyx.json` + `icon.svg` at project root, built frontend in `dist/` (default `--frontend dist`, project.ts:20 DEFAULT_FRONTEND). `manifest.entry` must be `frontend/<file>.html` and the built `dist/` content becomes `frontend/` in the package (project.ts:~118-125).
- Editor support: `"$schema": "./node_modules/@pithyx/sdk/schema/manifest.v1.schema.json"` (sdk/src/manifest.ts:4-6; S:20-24).
- App code starts with `const app = await connect()` (sdk/src/index.ts:5-15).
- Vite build uses base `./` unless set (vite.ts:~64) so relative asset paths work under `/apps/<id>/`.

### pithyx dev

- Two ways: (a) `pithyx dev` (needs built frontend; "rebuild and reload to see changes", start.ts:86-88); (b) Vite plugin: run `vite`, base becomes `/apps/<id>/`, mock desktop joins dev server, HMR works (vite.ts:46-68).
- Defaults: port 7400, host 127.0.0.1 (CLI:80-81; start.ts:46). Prints `Pithyx mock desktop: http://127.0.0.1:7400/`. Stops on SIGINT/SIGTERM and runs `docker compose down` (keeps volumes) (start.ts:83-95; dev/compose.ts:52).
- Mock desktop: at `/`, own API under `/__pithyx/`; frontend at `/apps/<id>/`; proxy `/apps/<id>/api*` (dev/server.ts:15,86-90). Mock users alice (admin), bob (2 groups family/office), carol (no groups) (dev/users.ts:21-39). Panel: theme, accent, locale, tz, size class, online, debug log, backend status, permissions, storage, events (i18n/en/cli.json dev.panel.\*).
- Backend in dev: runs `docker compose up --wait --build` on `backend/compose.dev.yaml` if present, else the pinned `backend/compose.yaml`, plus override publishing each endpoint port on `127.0.0.1::<port>` (dev/compose.ts:12-60). Box publishes nothing.
- Flags: `--endpoint name=url` (repeatable) skips Docker for that endpoint; `--no-docker` leaves backend alone, endpoints without --endpoint answer 503; unknown endpoint name => error (start.ts:20-36; CLI:82-86).
- Differences to the box (verify in code before claiming more): dev proxy mirrors header/strip/token behavior, 5 min token, 30 s ticket (dev/tokens.ts:5-9); Vite mode adds CSP script nonce (frontend.ts:20-30); token issue only from same-origin POST with `X-Pithyx-Request: 1` (dev/server.ts:148).
- "There is no development mode on the box" (CLI HELP end; D307): to test on a box, upload the package.
- Docker note: the backend needs local `docker compose`. Not in code: how a developer without Docker tests (only --endpoint/--no-docker).

### Build the package

- Sequence (from CLI HELP + code): build frontend (vite build -> `dist/`), `pithyx validate`, (backend) `pithyx build` or `pithyx lock`, `pithyx pack`.
- `pithyx validate [folder] [--frontend dir]`: checks pithyx.json, icon, layout, entry exists in built frontend, compose, images.lock; exit 1 on error, prints `<level> <code>: <text>` (CLI:167-175). Success line `<id> <version> is valid.`
- `pithyx lock`: resolves compose images from registries (anonymous or `~/.docker/config.json`, no credential helpers) -> `backend/images.lock.json` (amd64 and arm64: manifest + config digest) (CLI:44-47,176-199). App without backend: message "The app has no backend; there is nothing to lock." exit 0.
- Compose images must be pinned by digest `@sha256:` (R patterns.image; limit 16 images in lock, images-lock.v1.schema.json maxProperties 16).
- `pithyx pack [folder] [--out file-or-folder] [--images]`: validate then write; prints `Packed <path> (<n> bytes).`; refuses on errors (exit 1); `--out` must end `.pithyxapp` or be a folder (exit 2) (CLI:207-250).
- `--images` needs Docker; saves images per lock architecture; package gets large (CLI:93-96).
- Validation uses the same validators as the box with the same codes (CLI HELP; fixtures sdk/schema/fixtures shared Vitest/xUnit, CLAUDE.md "Tests").
- Vite plugin option `validate` (default true) runs validate after `vite build` and fails on errors; `project` option; BackendOptions `endpoints`, `docker` (vite.ts:25-31,102-110).

### Install it on a box

- Admin only. Store > "Installed" tab > "Install from file" (i18n/en/store.json installed.installFromFile, installed.empty "Install one from its package file (.pithyxapp)").
- Dialog "Install an app": Package file / Choose file; shows version/update/reinstall/downgrade line; "by <author>"; file count and size; warning "This package comes from a file and is not verified."; permissions list ("Permissions it asks for"), new permissions marked "New"; folders it asks for ("Map them to folders after the install."); backend yes/no; root warning; elevated rights; "Who may use it" (default everybody; suggested admins only if directory.read, root, or elevated); "Pool for app data" (first install only, fixed afterwards, `app.pool.fixed`) (store.json install.\*).
- No pool => "There is no pool that can hold apps. Create one in Storage first." (store.json install.noPools).
- After install: admin maps storage requests to share folders (`PUT /api/apps/{id}/storage/{storageId}`, AppEndpoints.cs:100); grants permissions; users get access per user/group grant (`/api/apps/{id}/grants`).
- Update by uploading a newer package: update that declares a new permission waits for admin confirmation, old version keeps running (S:194). Update takes a snapshot of app dataset; rollback `POST /api/apps/{id}/rollback` goes back with data (AppEndpoints.cs / StoreEndpoints.cs:74-81).
- Uninstall dialog asks to keep or delete data (store.json uninstall.\*).
- `org.pithyx.*` ids refused on upload (AppEndpoints.cs:69-73).
- Backend apps need Docker on the box; Docker starts when a backend app is installed and the pool is mounted (daemon/CLAUDE.md:19). `app.backend.unsupported` text says "installs apps without a backend only" but code never raises it (ErrorCodes.cs:62 only) => stale, see open questions.

### Questions

- Can I install without Docker on the dev machine? Yes without backend; with backend use `--no-docker --endpoint`.
- Why does validate warn about the icon? still the template icon (findings.ts:29).
- Why "frontend build does not exist"? build first or `--frontend <folder>` (findings.ts:26).
- Is output reproducible? Yes for the package (D315); images not unless `build --oci-out` (CLI:51-53).

======================================================================

## developers/apps/manifest.md

File: `pithyx.json` at package root. Schema `sdk/schema/manifest.v1.schema.json`, `$id https://pithyx.org/schema/manifest.v1.schema.json` (S:3). Unknown fields are errors (`additionalProperties:false`, S:7). Stable contract: new optional field = minor, else major (S:5). Required top-level: manifestVersion, id, version, name, author, entry, sdk, permissions, window (S:8-18). Optional: $schema, description, license, homepage, pithyx, dataVersion, storage, fileHandlers, backend.

### Identity

- `$schema` string max 500, ignored by box (S:20-24).
- `manifestVersion` const `1` (S:25-28). Box error `app.manifest.version_unsupported`.
- `id` see overview (S:29-31,115-121).
- `version` SemVer 2.0 without build metadata, max 64 (S:32-37). Example `1.2.0`, `2.0.0-beta.1`.
- `name` LocalizedName: object keyed by BCP 47 tag (`^[a-z]{2,3}(-[A-Za-z0-9]{1,8})*$`), `en` required, each 1-60 chars, one line (S:122-150).
- `description` LocalizedText: same keys, `en` required, 1-500 chars, line breaks allowed (S:128-133,151-167). Optional.
- `author` object, required `name` (1-100 chars, no control chars), optional `url` (http/https, max 500) (S:173-191).
- `license` optional SPDX expression, max 100 (S:47-52). Required for catalogs (R manifestFields ["license","pithyx"]).
- `homepage` optional http(s) URL max 500 (S:53-56).
- `entry` pattern `^frontend/(seg/)*seg.html$`, max 200, segments never start with a dot (S:57-62).
- `sdk` required VersionRange (npm range syntax w/o loose forms: `^ ~ > >= < <= =`, x/\* wildcards, hyphen ranges with spaces, `||`), max 100 (S:63-66,109-114). Example `^1.0.0`. Invalid => `app.manifest.sdk_invalid`. No code found in daemon that compares this range with the host protocol (open question).
- `pithyx` optional VersionRange of box versions (S:67-70). Box installs only if own version in range; prerelease matched by plain SemVer precedence (`>=0.1.0` includes `0.2.0-beta.1`, `^0.1.0` does not). Catalogs require it. Errors `app.manifest.pithyx_invalid`, `app.package.box_too_old`.
- `dataVersion` optional integer 1..2147483647 (S:71-76). Downgrade from catalog only if both versions have the same dataVersion; error `app.package.downgrade_unsupported`; rollback via snapshot works without it.

### Windows

- `window` required: `instances` enum `single|multi`, `defaultSize` {width,height}, `minSize` {width,height} (S:232-250). Sizes integers 100..8192 CSS px (S:213-231). `minSize` must not exceed `defaultSize` in either dimension (error `app.manifest.window_size_invalid`).
- `single`: opening again focuses user's existing window on this device and passes launch args (`launch` event). `multi`: each launch opens a new window (S:240).
- `defaultSize` used when user has no remembered size; `minSize` applies on screens that can fit it (S:234).

### Permissions

- `permissions` required array, unique items (can be `[]`), enum (S:77-84,192-204):
  - `files.read`: read files the user opens with the app or picks in a desktop file dialog
  - `files.write`: write those files, save new files via desktop save dialog
  - `files.shares`: browse every shared folder the user may access, with user's own rights
  - `notifications`: show notifications
  - `directory.read`: list all users and groups
  - `network.internet`: outgoing connections from window (`connect-src https:`) and from the backend (second non-internal network)
- Install dialog lists all; admin confirms; each revocable; app gets `permissionsChange`; new permission in an update waits for admin (S:194). A revoked permission stays revoked across updates (AppEndpoints.cs:99). Unknown permission set via API => `app.permission.not_requested`; call without it => `app.permission.denied`.
- Elevated rights `backend.elevated` (S:205-212,457-465): items: `host.network`, `host.pid`, `device:/dev/<path>`, `capability:<NAME>` (no CAP\_ prefix), `docker.socket`, `host.full`; max 32 unique. Only admins install, after confirmation with password + TOTP (S:207). THIS version refuses `docker.socket`, `host.full` and capabilities ALL, SYS_ADMIN, SYS_PTRACE, SYS_MODULE, SYS_RAWIO, DAC_READ_SEARCH, BPF with `app.elevated.unsupported` (S:207; packages/cli/src/manifest.ts:33-41). Compose must use exactly what elevated allows (R rights; errors `app.compose.right_missing|right_unused`). Not in code (plan only): A16 elevated-rights UI/risk texts, D311 reserved (TODO.md:108,442). Open: whether host.network/host.pid/device/capability actually work end to end today.
- `storage` (optional, max 16) StorageRequest (S:262-297): `id` (Name `^[a-z][a-z0-9-]{0,31}$`), `name` LocalizedName, optional `description`, `access` enum `read|readWrite`, `required` boolean, optional `services` (unique ServiceName list, min 1; needs a backend). Admin maps each request to a folder in a shared folder; window uses `{ storage: id }` in SDK file API; backend services see a Compose volume named `id`. App cannot start until required storage mapped.
- `fileHandlers` (optional, max 32) (S:298-334): `extensions` (required, 1-32 unique, lowercase no dot, pattern `^[a-z0-9]+([.+_-][a-z0-9]+)*$`, max 32 chars, e.g. `png`, `tar.gz`), `mimeTypes` (1-32 unique, pattern type/subtype, max 127), `isDefault` boolean. An extension belongs to one handler only. Desktop offers "Open with" only for these; delivered via `launch` / `openFile` events.

### Backend

- `backend` optional object, required `compose`, `endpoints`, `lifecycle` (S:400-405). Fields:
  - `compose`: path in package, pattern `^backend/(seg/)*seg.ya?ml$`, max 200; usually `backend/compose.yaml` (S:407-412)
  - `endpoints`: object name -> {service, port}; `api` required (default endpoint of `app.backend`); max 16; names `^[a-z][a-z0-9-]{0,31}$` (S:413-429). `service` ServiceName `^[a-z0-9][a-z0-9_.-]{0,62}$`; `port` integer 1..65535 inside app network, never host-published (S:335-352). `api-api` is not a valid second name (AppProxy.cs:162).
  - `lifecycle`: `always|onDemand` (S:430-434)
  - `idleTimeout`: integer seconds 60..86400, default 600, ignored for always (S:435-441)
  - `root`: boolean default false (S:442-446)
  - `health`: {endpoint (Name, key of endpoints), path (starts `/`, max 200)}; daemon calls `GET path`, 2xx = running (S:353-374,447-449)
  - `schedules`: max 16 of {id (Name), cron, endpoint, path}; daemon starts backend and calls `POST path` (S:375-399,450-456). cron: 5 fields single-space separated, minute 0-59, hour 0-23, dom 1-31, month 1-12, dow 0-7 (0 and 7 Sunday), box's time zone, comma lists of `*`, numbers, ranges a-b, steps /n; no names. Max 100 chars. Error `app.manifest.cron_invalid`.
  - `elevated`: see Permissions.
- Cross-checks (beyond schema) error codes: `app.manifest.duplicate`, `app.manifest.reference_unknown` (service/endpoint names), `app.compose.storage_mismatch` (storage vs compose mounts).

### Lifecycle

- see backend page; manifest side: `lifecycle`, `idleTimeout`, `schedules`, `health`.

### Versions

- Version bumping: SemVer; box compares installed vs package: `install|update|reinstall|downgrade` states in dialog (store.json install.action.\*).
- Same version again => "Installing it again replaces its files". Older => "Its data may not fit."
- Catalog: a published version is never changed or removed (index.v1 description); `dataVersion` never lower than an older version's (`store.catalog.data_version_decreased`, D330).
- Box keeps previous version + snapshot until next update for rollback (D329 TODO.md:1465; `app.app.no_previous`).
- `pithyx` range filters catalog versions by box version.
- Manifest schema versioning: `manifestVersion` const 1 now.

### Questions

- What if I add a permission in an update? Waits for admin, old keeps running (S:194).
- Can an app have 2 windows? `instances: multi`.
- Is `sdk` range enforced? unknown (no daemon check found).
- Can the manifest name an icon? No, dropped (D300).

======================================================================

## developers/apps/backend.md

### Compose subset

- File `backend/compose.yaml`; validated by allowlist R (shared by CLI and daemon, R:2). Any key not listed => `app.compose.key_unsupported` (R:2). Box REWRITES the file (R:2; D/Compose/ComposeRewriter.cs).
- Limits (R:4-16): file 65,536 bytes; depth 10; entries 256; services 16; volumes 32; shm_size max 268,435,456; tmpfs size max 268,435,456; image ref max 512 chars; mem_limit 6,291,456 (6 MiB) to 274,877,906,944 (256 GiB); cpus max 256.
- YAML: no anchors/aliases/tags/directives, no merge keys, no duplicate keys, keys must be strings (D/Compose/ComposeYaml.cs:83-84,152,248); `x-*` refused.
- Top level allowed: `services` (required), `volumes` (R:39-42). Refused: name, version, networks, secrets, configs, include, x-\* (R:128-136).
- Service allowed keys (R:43-84): image (required, pinned `@sha256:` digest, pattern R:20), command, entrypoint, environment, working_dir, volumes, tmpfs, depends_on (condition service_started|service_healthy|service_completed_successfully; restart; required), healthcheck (test, interval/timeout/start_period/start_interval durations 1s..1h (start_period 0s..1h), retries 1..100, disable), expose, read_only, init, stop_signal, stop_grace_period (0s..5m), shm_size, mem_limit, cpus, labels (reserved prefixes `com.pithyx`, `com.docker`, R:126), and rights-bound: network_mode (only `host`, needs `host.network`), pid (only `host`, `host.pid`), devices (`device:`), cap_drop, cap_add (`capability:`), privileged (`host.full`).
- Mounts (R:85-101): `type` volume|tmpfs|bind (bind refused: "host paths are never mounted", R:207), `target` absolute container path, `read_only`, tmpfs size. Reserved targets /proc /sys /dev; docker sockets `/var/run/docker.sock`, `/run/docker.sock` need right `docker.socket` (refused today).
- Refused service keys with reasons (R:137-198), notable: ports ("backends are reached only through the proxy"), build (box never builds), networks, links, volumes*from, env_file, secrets, configs, extends, profiles, container_name, user ("box sets the app uid"), group_add, restart ("box runs the lifecycle"), logging, deploy ("use mem_limit and cpus"), security_opt, sysctls, ulimits, dns\*, extra_hosts, hostname, platform, pull_policy, tty/stdin_open, develop, pids_limit, oom*\*. Volumes: driver, driver_opts, external, name, labels refused (R:199-205).
- Source form `backend/compose.dev.yaml` (R:112-125): same rules plus `build` (string or {context, dockerfile, target, args}); each service exactly one of image/build; `pithyx build` writes `compose.yaml` + `images.lock.json`; the box never reads the dev file.
- `images.lock.json` (sdk/schema/images-lock.v1.schema.json): `lockVersion` 1; `images` map (1..16) reference -> {amd64|arm64: {manifest sha256, config sha256}}. Box checks every image it loads/pulls against config digest; mismatch => `app.image.mismatch`; missing/stale lock => `app.package.lock_invalid` (reasons missing|invalid|stale). Architectures: amd64 and arm64 only.
- Images come from package `backend/images/<arch>/<config hex>.tar` or are pulled by digest (D/Docker/AppBackendRuntime.cs:37-39); `pull_policy: never` set by the box (ComposeRewriter ~line 99). Failure => `app.image.unavailable`.
- Box rewrite adds (ComposeRewriter.cs): `user` = `<uid>:<uid>` or `0:0` with root; `group_add` share gids (30000-39999) for mapped storage; app network + optional internet network; `restart: "no"`; journald logging with labels; `pids_limit: 1024` (const PidsLimit); `oom_score_adj: 500`; `security_opt: no-new-privileges:true`; labels `com.pithyx.app`, `com.pithyx.service`, `com.pithyx.version`; named volumes become bind mounts of `<pool>/.pithyx/apps/<id>/volumes/<name>`; mapped storage becomes bind of the share folder (read_only if admin granted less); `bind.create_host_path:false`.
- Error codes: `app.compose.yaml_invalid`, `.key_unsupported`, `.value_invalid`, `.reference_unknown`, `.right_missing`, `.right_unused`, `.storage_mismatch` (errors.json).

### Endpoints and routes

- `/apps/<id>/api/...` = endpoint `api`; `/apps/<id>/api-<name>/...` = endpoint `<name>`; regex `^api(?:-(?<name>[a-z][a-z0-9-]{0,31}))?$` (AppProxy.cs:~190). Container ports never in URLs. Path after prefix forwarded (AppProxy.cs:160-166 Match).
- SDK: `app.backend.fetch(path, {endpoint})`, `.url()`, `.socket()`, `.token()` (sdk/src/app.ts). SDK `ENDPOINT_NAME` regex allows up to 63 chars (sdk/src/backend.ts:17) vs manifest/proxy 32: mismatch (open question).
- Auth at proxy: HTTP: `Authorization: Bearer <token>` or `?pithyx-token=`; WebSocket: only `?pithyx-ticket=` for that endpoint; cookie never counts (AppProxy.cs:13-18,38-39). Token must belong to path's app; bad => 401 `app.token.invalid` before anything is said (D323 TODO.md:1456).
- Preflight: `OPTIONS` from `Origin: null` => 204 with ACAO `*`, methods `GET, HEAD, POST, PUT, PATCH, DELETE`, requested headers, max-age 600; other origin => 403 (AppProxy.cs:196-210).
- Uploads: request body size limit removed ("backend decides") (AppProxy.cs:~118). Connect timeout 5 s; HTTP/1.1 to backends; no redirects/decompression/cookies (AppProxy.cs:44-52). Idle WebSocket kept up to 24 h activity timeout (AppProxy.cs:62).
- Not reachable: 503 `app.backend.unavailable` when backend not running/updating/unavailable (AppProxy.cs:~124). Stopped/starting backend: request wakes it, waits up to 30 s (supervisor WakeTimeout, AppBackendSupervisor.cs:36).
- Answers lose origin-wide headers: Set-Cookie, Set-Cookie2, Clear-Site-Data, HSTS, Alt-Svc, Refresh, Report-To, Reporting-Endpoints, NEL, Public-Key-Pins(-Report-Only), Expect-CT, CSP-Report-Only, Origin-Agent-Cluster, Service-Worker-Allowed, ACA-Credentials/-Methods/-Headers/-Max-Age (AppProxy.cs:258-264). Sandbox CSP etc. added to every answer; `Access-Control-Expose-Headers: *` (AppProxy.cs:248-250).

### Who is calling

- Headers stripped from the incoming request: Cookie, Authorization, Proxy-Authorization, Referer(?) (comment "A referrer may carry ?pithyx-token="), Forwarded, traceparent, anything `X-Pithyx-*` or `X-Forwarded-*`; query params `pithyx-token`, `pithyx-ticket` (AppProxy.cs:13-21, 169-180, 277-287).
- Headers set: `X-Pithyx-User` (user name), `X-Pithyx-User-Id`, `X-Pithyx-Groups` (comma list of group names), `X-Pithyx-Admin` (`true`/`false`), `traceparent`, `X-Forwarded-Prefix` (`/apps/<id>/api` or `/apps/<id>/api-<name>`), `X-Forwarded-For`, `X-Forwarded-Host`, `X-Forwarded-Proto` (AppProxy.cs:231-240).
- Backend must trust these headers only because proxy strips them (CLAUDE.md "Apps").
- Tokens: created by desktop `POST /api/apps/{appId}/tokens` (cookie + CSRF header), kinds `host` (for `/api/app/**`, stays in desktop) and `backend`; lifetime 5 minutes; 256 random bits URL-safe (32 bytes); bound to user, session, app, window instance; stored hashed (D/AppTokens.cs:32,45-46,132; AppApiEndpoints.cs:31-37). SDK keeps token until 30 s before expiry (sdk/src/app.ts TOKEN_MARGIN), refetches after `permissionsChange`.
- WebSocket ticket: `POST /api/app/tickets` (host token), one-time, 30 s, bound to endpoint (AppTokens.cs:46,81; AppApiEndpoints.cs:116-120). Unknown endpoint => `app.backend.endpoint_unknown`. App without backend => `app.backend.missing`.
- Every use re-checks session, user, app grant (AppTokenCheck); revoked permissions/sign-out close proxied sockets (CLAUDE.md; AppProxySockets.cs).
- Tokens/tickets masked in logs (CLAUDE.md).
- Daemon API for apps `/api/app/**` (host token): logs, notifications, storage (GET list, GET/PUT/DELETE value), directory users/groups, files shares/list/entry/content/folders, tickets (AppApiEndpoints.cs:44-120).

### Storage

- App data on the app pool the admin picks at first install (`app.pool.required`); dataset per app: `/<pool>/.pithyx/apps/<id>/` with `versions/<version>`, `current`, `volumes/`, `run/compose.json` (D/AppStorage.cs:12-27; AppBackendRuntime.cs:129). Docker data root also on the pool (`<pool>/pithyx/docker`, D324). Nothing on the system disk.
- Named volumes: declare top-level `volumes:` and mount by name; become folders under `volumes/<name>` owned by the app uid (AppBackendRuntime.cs:408-426). No bind mounts, no drivers.
- Manifest `storage` requests: Compose volume named `id`; mapped to share folder; read-only if admin granted `read`; an unmapped optional one becomes a folder of the app dataset (ComposeRewriter ~line 245). Required+unmapped => no start, `app.backend.storage_unavailable`.
- Pool locked or missing => backend does not start, UI says why; pool `app.pool.unavailable` reasons missing|offline|read_only|...
- Update takes a snapshot of the dataset; rollback restores data (AppEndpoints.cs:89, 81).
- Uninstall: keep or delete data (store.json uninstall.keep/keepHint).
- Per-user key/value via SDK (`app.storage`): 128-char keys, 64 KB values, 1 MB per user (D/AppValueStore.cs:16-18).

### Network

- Own internal Docker bridge network per app: `internal: true`, driver bridge, bridge name `px-<uid-40000>` (AppBackendRuntime ~line 70; ComposeRewriter Network()). No internet by default.
- `network.internet` permission => second non-internal bridge `px-i<n>` that every service joins (ComposeRewriter.cs:20-22, 100-121).
- Services reach each other by service name on the app network (R:141 links reason).
- Host firewall: nftables chain `inet pithyx apps-input` drops new connections from `px-*` to the host (D324 TODO.md:1458).
- No host ports (`ports` refused; manifest port never published).
- UID: own uid 40000 to 49999 (also gid) per app, allocated at install (D/Compose/ComposeRewriter.cs:12; daemon/CLAUDE.md:20). `root: true` => `0:0` inside container, unprivileged on host through userns (pithyx-docker remap user); install dialog warning (store.json install.root).
- Hardening always: no-new-privileges, pids_limit 1024, oom_score_adj 500, journald logs into source `app/<id>`.
- Not allowed without right: docker.sock, privileged, host network (refused today).

### Lifecycle

- `always`: starts whenever it can run (enabled, pool available, required folders mapped). `onDemand`: runs while a user holds a window lease, a request or schedule holds it, until `idleTimeout` after the last (D/Docker/BackendLifecycle.cs:36-50).
- Window lease: desktop subscribes to topic `apps/<id>/context` once per app and tab; unsubscribe (last window closed) ends it at once; connection loss keeps it for 2 minutes grace (AppContexts.cs:139, class doc 122-130).
- idleTimeout default 600 s (BackendLifecycle.cs:49; S:435-441). Start request holds backend for WakeTimeout 30 s; schedule holds for ScheduleTimeout 10 min (AppBackendSupervisor.cs:36,39).
- Failed start retried after 1 minute (BackendLifecycle.cs:46); opening a window forgets the failure and retries at once.
- Start timeouts: Compose `up --wait` up to 10 min; manifest health check up to 60 s afterwards (AppBackendRuntime.cs:55,58).
- Stop: `docker compose down --timeout 30 --remove-orphans` (AppBackendRuntime.cs:68,231): containers get SIGTERM then kill after 30 s; volumes and images stay.
- Status exposed to the window: `stopped|starting|running|updating|unavailable` (sdk/src/protocol.ts BackendStatus), event `backendStatus`.
- Schedules: backend woken even with no window; call `POST path`; then idles normally.
- Docker starts only while a backend app is installed and pool mounted (daemon/CLAUDE.md:19).
- Not in code (plan only): "keep-alive lease" for background work mentioned in CLAUDE.md "Apps"; no code found (grep keepalive in sdk and Apps: none).

### Questions

- How does my backend know the user? `X-Pithyx-User*` headers (AppProxy.cs:232-235).
- Can my backend call the internet? Only with `network.internet`.
- Can I publish a port? No.
- How do I persist data? named volume or manifest `storage`.
- Why does my compose key fail? Allowlist R; check refused list reasons.
- Can I use `build:` in compose.yaml? No; use compose.dev.yaml + `pithyx build`.
- Why does the first request after idle take long? wake up to 30 s.

======================================================================

## developers/apps/publishing.md

### Catalogs

- Two project catalogs: `pithyx/apps-official` (only source of `org.pithyx.*` besides the image) and `pithyx/apps-community` (off by default), both from template `pithyx/catalog-template` (D328 TODO.md:1464; CLAUDE.md "Catalogs"). Built in as kinds Official/Community; admin-added = Custom (D/Catalog/StoreContracts.cs:26-31).
- Source in `apps/<id>/` on `master`; CI builds frontend + images and pushes to `ghcr.io/<owner>/apps/<id>/<service>:<version>`, never overwrites, writes CI-only branches `catalog-next` (channel beta) and `catalog` (channel stable) (D328, D333). `catalog` only after owner approval in environment `catalog-release`. Images linked by digest only.
- Repository files: `catalog.json` (sdk/schema/catalog.v1.schema.json: catalogVersion 1, id, name{en}, description, homepage https, images registry prefix like `ghcr.io/pithyx/apps`); on branches `index.json` (index.v1.schema.json) and `index.json.sig` (index-signature.v1.schema.json).
- Catalog id: like app id (3-5 parts); `org.pithyx.official`, `org.pithyx.community` reserved (catalog.v1.rules.json reservedIds: only `org.pithyx.official` may list `org.pithyx.*`).
- Index: indexVersion, catalog, channel (`stable`|`beta`), sequence (+1 per publish, box refuses lower `store.catalog.sequence_old`, equal accepted), publishedAt, apps -> versions with SHA-256+size of package, manifest, icon, screenshots; images, architectures, `pithyx` range (`*` if none), dataVersion, license, source (repo, commit, `apps/<id>`). Files at `apps/<id>/<version>/`: `<id>-<version>.pithyxapp` (no image tarballs), `pithyx.json`, `icon.svg`, `screenshots/<name>` (png/jpg/webp).
- Signature: Ed25519 pure over exact bytes of index.json; `index.json.sig` JSON `signatureVersion` 1, 1..4 signatures {keyId `^[a-z0-9][a-z0-9._-]{0,63}$`, base64 64-byte sig}. Box accepts if >=1 signature by a key trusted for the channel and every signature by trusted keys verifies; unknown key ids ignored (rotation). Errors `store.catalog.signature_invalid` reasons missing|malformed|untrusted|mismatch.
- Built-in keys (D/Catalog/StoreService.cs:30-34): `pithyx-catalog-1` trusted on stable+beta, `pithyx-next-1` beta only. Private halves exist only as environment secrets of the catalog repos (D331).
- Custom catalogs: unsigned, "unverified", installed apps from them show warning "From the catalog X, which is not verified" (store.json install.fromCustomCatalog); official: "checked against its signature".
- Box side: shallow copy in `/var/lib/pithyx/catalogs/<id>/`; fetch with hardened git (https/ssh only, 3 min timeout, depth 1, no hooks/submodules/redirects) (D/Catalog/CatalogGit.cs:74; D334). Verifies catalog.json, signature (project catalogs), index, sequence, every file hash; failed fetch keeps last verified copy.
- Store tabs: Installed, Updates, Official, Community, Own (store.json tabs.\*).
- Channels and updates settings (StoreService.cs:~70-75, StoreEndpoints.cs:19-22): policy `automatic|notify|none` (default notify), daily check time `HH:mm` default 03:00 plus per-box random delay up to 60 min, channel `stable|beta` (stable reads `catalog`, beta reads `catalog-next`), per-app override `PUT /api/apps/{id}/update-policy`, "Check now" `POST /api/store/check`.
- Update never installs automatically if the new version asks new permissions, root or elevated rights (D329, D334).
- Rollback: `POST /api/apps/{id}/rollback`; under automatic policy rollback sets app policy to notify (D336).

### The review

- Owner reviews every pull request to a catalog: ids by reversed-domain rule, no foreign brands, no `org.pithyx.*` in community, only apps worth having (D328). Machine rules catch the rest (below). Not in code: the human review criteria beyond D328 text; no CONTRIBUTING for catalogs readable here.
- `maintainers.json` per app (maintainers.v1.schema.json): `{ "maintainers": [GitHub logins] }` 1..20 unique; a PR changing an app must come from one of them as the file stood before the PR; new app's file must name the author.
- After merge: workflows `check.yml` (PRs, read only), `publish.yml` (push to master -> catalog-next, environment `catalog-next`), `promote.yml` (manual, environment `catalog-release`) (D333). Outside-contributor runs wait for approval.
- Promotion to stable needs owner approval in `catalog-release` (D328/D333).

### Rules (catalog.v1.rules.json)

- appsFolder `apps`; one app folder per PR (rule `one_app`, errors.json store.catalog.rule_violation).
- limits: appBytes 5,242,880 (5 MiB) per app folder; lineChars 1000; screenshots max 8, each max 1,048,576 bytes; screenshot names `^[a-z0-9][a-z0-9_-]{0,62}\.(png|jpg|webp)$` in `screenshots/`.
- requiredFiles: `pithyx.json`, `icon.svg`, `maintainers.json`, one of `LICENSE|LICENSE.md|LICENSE.txt`.
- manifestFields required: `license`, `pithyx`.
- Files must be text (UTF-8, no NUL, no line over 1000 chars except svg) or listed media with matching magic bytes (gif jpeg jpg png webp woff2); minified (`.min.js|mjs|cjs|css`) refused; every other file, folders and unlisted dotfiles refused. Text extensions list in rules file (c, cjs, css, html, js, json, md, ts, tsx, ... ).
- Dockerfile name pattern: `Dockerfile|Containerfile[.x]` or `name.Dockerfile`. Every FROM and COPY/ADD --from (not earlier stage) must be pinned `@sha256` or `scratch`. Compose `build` context rules in R:117-124, dockerfilePath pattern.
- Licenses: SPDX expression, every id and exception in the OSI list in the rules file (e.g. MIT, Apache-2.0, AGPL-3.0-only ...); `LicenseRef-` refused.
- Index rules: every image must have each listed architecture (`store.catalog.architecture_missing`); dataVersion never decreases.
- Reproducible builds where possible: BuildKit pinned v0.33.1 by digest in workflows (D332); `SOURCE_DATE_EPOCH` from env or last commit.

### An own catalog

- Make repo from `pithyx/catalog-template` (public template). Placeholder id refused by publish job (D333). CLI commands (see sdk/cli page) run in repo root.
- Add to a box: Store > Own > add by URL: `https://host/path` (public) fetched at once as a job; SSH `git@host:owner/repo.git` (private): box creates an SSH deploy key (ed25519 via ssh-keygen), shows public key to add to the repo (read-only), then "fetch" (StoreEndpoints.cs:30-33; CatalogGit.cs:15-47,168). Host key: GitHub's built-in keys or pinned at first connect. Optional registry read token `user:token` for private images (StoreEndpoints.cs:37).
- URL rules: https without credentials; ssh forms `user@host:path` or `ssh://user@host[:port]/path`; `file://` only in simulation. Error `store.catalog.url_invalid` reasons scheme|host|...
- Custom catalog may link images from any registry by digest, follows global update policy (D328).
- Unsigned: publish workflow signs only if env has `SIGNING_KEY` (+ `SIGNING_KEY_ID`) (D333); a custom catalog still must be valid per schema, sequence etc. Box verifies signatures only for project catalogs (StoreService doc).
- GHCR caveat: packages pushed by a workflow are private even from public repos; owner must make each package public in GitHub web UI (D331; CLAUDE.md pitfalls).
- Removing: custom catalog removable (`DELETE /api/store/catalogs/{key}`); installed apps stay without updates; project catalogs only switchable (`store.catalog.built_in`).
- Errors: `store.catalog.unreachable` (reasons timeout|auth|...), `.exists`, `.not_found`, `.busy`, `store.package.hash_mismatch`, `store.app.not_found`, `store.catalog.version_unsupported`, `.mismatch`, `.schema_violation`, `.invalid_json`, `.id_reserved`.

### Questions

- How do I get my app into the official/community catalog? PR to the repo; owner review; unclear exact process (no contributor docs in repo), see open questions.
- Why does a stable box show "Fetch failed" for apps-official? Not promoted yet (CLAUDE.md "Current state"; time-specific, do not document).
- Can I use my own registry? Custom catalogs: yes, by digest. Project catalogs: CI pushes to ghcr.io/<owner>/apps.
- Do users need an account? No; box only needs git access (https public / SSH deploy key).
- Can I downgrade? Same `dataVersion` or via rollback snapshot.

======================================================================

## sdk/overview.md

### Packages

- `@pithyx/sdk` (MIT): the API between an app and the desktop. Subpath exports: `.`, `./host` (desktop side, apps do not use it, host.ts:1-5), `./i18n` (createI18n), schema JSON files `./schema/manifest.v1.schema.json`, `compose.v1.rules.json`, `images-lock.v1.schema.json`, `catalog.v1.schema.json`, `catalog.v1.rules.json`, `index.v1.schema.json`, `index-signature.v1.schema.json`, `maintainers.v1.schema.json` (sdk/package.json exports). Dependency intl-messageformat (i18n helper).
- `@pithyx/ui` (MIT): React components + `tokens.css`; exports `.`, `./native`, `./tokens.css` (packages/ui/package.json). Peer deps react ^19.3.0, react-dom ^19.3.0; deps @tabler/icons-react, react-aria-components.
- `@pithyx/cli` (MIT): `pithyx` binary (`bin/pithyx.js`), exports `.` (validators API: validateManifest, validateCompose, pack, readProject, etc., packages/cli/src/index.ts) and `./vite` (plugin) and `./template-icon.svg`. Node >=22.12.0. deps: @pithyx/sdk, ajv, intl-messageformat, saxes, yaml. Optional peer vite >=7.
- Whole product AGPL-3.0; packages MIT (README of each; CLAUDE.md).
- READMEs say "Work in progress... release candidate. The API can still change before 1.0.0" (sdk/README.md:7).
- Docs / API reference generated by typedoc (sdk/typedoc.json, `pnpm docs`), not hand-written (CLAUDE.md "Repository").

### Versions and channels

- One shared SemVer line for sdk, ui, cli, separate from product version (D306; sdk/src/version.ts:3-9). Tag `sdk-vX.Y.Z`.
- package.json today: 1.0.0-rc.5 for all three.
- READMEs: "Release candidates are published under the dist-tag `next`"; badge shows npm `next` (sdk/README.md). Install line `npm install @pithyx/sdk@next`.
- Not verifiable from code: what is on npm now (TODO/CLAUDE says rc.2 `latest`+`next`, rc.3 `next`; repo is at rc.5, "not published yet"). `latest` only after owner's approval (D306 TODO.md:1439). Owner publishes with `tools/release/publish-sdk.sh`, npm 2FA; no token stored.
- SDK compatibility policy (sdk/src/version.ts:3-9): new method/event/optional field = minor; removed/changed = major. Deprecations last one major (deprecated in N, works through N+1, removed N+2 earliest). Desktop of major N+1 serves apps built against N (MIN_PROTOCOL_VERSION).
- Protocol: `PROTOCOL_VERSION = 1`, `MIN_PROTOCOL_VERSION = 1`; additions keep protocol version and are found via `context.capabilities` / `app.supports(method)` (version.ts:20-34).
- Manifest schema + compose rules + catalog formats are stable contracts: loosening = minor, else major.

### Installing

- `npm install @pithyx/sdk@next`; ui: `npm install @pithyx/ui@next react react-dom`; cli: `npm install --save-dev @pithyx/cli@next` (READMEs).
- TypeScript types shipped (`dist/index.d.ts`). Framework-neutral: "any framework, or plain HTML and CSS" (theme.ts:8).
- Not in code: framework starters; pnpm/yarn variants not mentioned.

### Questions

- Which version should I install? Today `@next` (README); `latest` unknown.
- Does the SDK need React? No. Only ui does.
- Are SDK version and Pithyx version the same? No.

======================================================================

## sdk/sdk.md (package `@pithyx/sdk`, sdk/src)

### Getting started

- `import { connect } from '@pithyx/sdk'; const app = await connect();` (index.ts:5-15,99). `connect(options?: CreateAppOptions & { connectTimeout?: number }): Promise<PithyxApp>`. Waits for exactly one `pithyx:connect` message with one MessagePort, only from `window.parent`; times out after 10,000 ms with `sdk.transport.closed` (index.ts:81-127), e.g. when opened outside the desktop.
- `CreateAppOptions`: `themeRoot?: HTMLElement | null` (default documentElement, null = skip), `timeout?: number` (call limit ms), `origin?: string` (box address for backend URLs, default from window URL) (app.ts ~410-418).
- Core apps: `createApp(endpoint, options)` with `inProcessPair()`, `portEndpoint(port)` (index.ts:17,78).
- Theme: SDK sets `--pithyx-*` CSS custom properties on root, `color-scheme`, `data-pithyx-theme`, `data-pithyx-density` (theme.ts:49-65). 20 public token names: `--pithyx-color-background|surface|surface-raised|hover|selected|text|text-muted|border|accent|accent-text|focus|success|warning|danger`, `--pithyx-font-ui|font-mono|font-size|radius|space|control-height` (theme.ts:17-37). `themeChange` event; `applyTheme(theme, root)`, `themeTokenNames`.
- Timeouts: REQUEST_TIMEOUT 15,000 ms (calls fail `sdk.request.timeout`), `files.upload` untimed (UNTIMED_METHODS), BEFORE_CLOSE_TIMEOUT 5000, BEFORE_UNLOAD_TIMEOUT 2000 (protocol.ts:551-561).
- Constants exported: SDK_VERSION, PROTOCOL_VERSION, MIN_PROTOCOL_VERSION.
- Other exports: `createFormatters`, `PithyxError`, `isProblem`, `sdkErrorCodes`, many types (index.ts:28-79).

### App and user

- `app.context: AppContext` (protocol.ts:115-167), kept current by events: `app {id,version}`, `instance`, `user {id,name,displayName,groups[{id,name}],isAdmin,twoFactor}`, `language`, `locale` (BCP47 like de-AT; also set as `lang` on root), `timeZone` (IANA), `theme`, `sizeClass` (`compact` <600px, `medium` <=1023, `expanded`), `launch {args,file?,notification?}`, `session`, `permissions: string[]`, `capabilities: string[]`, `box {name,version}`, `visible`, `online`, `logLevel` (`debug|info`), `backend: {status, endpoints[]} | null`, `storage: StorageInfo[]` (`{id,state ready|unmapped|locked|unavailable, access read|readWrite|null}`), `host {version,protocol}`.
- `app.window`: `setTitle(title)` (max 200 chars), `close()` (no beforeClose), `setSize({width,height})` resolves with granted size, `setDirty(bool)`, `requestAttention()`. Window methods act on caller's own window; none focuses/raises (protocol.ts header).
- `app.notify({title(1-200), body?(<=1000), level? info|success|warning|error, actions? (max 2: {id 1-64, label 1-40}), data? (<=4 KB JSON), tag? (1-64)}) => {id}`; needs `notifications`. History max 200 entries, 30 days (protocol.ts:187-205).
- `app.setBadge(count|null)`, `app.openUrl(url)` (https/http/mailto, new tab), `app.clipboard.writeText(text)`, `app.contextMenu.show(at, items)` => id|null, `app.ui.showError(error)` (desktop error dialog with translated text).
- `app.directory.users()/groups()` needs `directory.read`; `DirectoryUser {id,name,displayName,groups:number[]}`.
- `app.session.save(state)` (max 16 KB JSON) / `restore()`: survives reload, moves with desktop layout.
- `app.format`: `date, time, dateTime, relative, number, bytes (decimal units), list` following locale/timeZone (format.ts).
- `@pithyx/sdk/i18n`: `createI18n(app, { en, de, ... })` -> `{language, t(key, params)}`; ICU MessageFormat; picks exact locale, base language, then `en`; `pickLanguage(locale, available)`.
- `app.supports(method)`, `app.call(method, params)` low-level, `app.on(event, handler)` returns unsubscribe, `app.answer(query, handler)`.

### Files and storage

- Storage (per user, on the box): `app.storage.get<T>(key)`, `set(key, value)`, `remove(key)`, `keys()`; key <=128 chars, value <=64 KB JSON, <=1 MB per user per app (app.ts storage doc; D/AppValueStore.cs:16-18). Errors `app.storage.key_invalid`, `app.storage.too_large`. Sandboxed apps have no localStorage.
- Handed-over files (permissions files.read/files.write): `files.pick({multiple?, extensions?})`, `files.saveAs(suggestedName, blob)`, `files.read(ref)`, `files.write(ref, blob)`, `files.url(ref)` (valid 5 min), `FileRef {ref,name,type?,size,modified,writable}`. Must follow a user action for pick/saveAs.
- Named storage and shares (`FileLocation = {storage:id} | {share:id}`; shares need `files.shares`): `files.open(location, path)`, `shares()`, `list(location, path, {cursor,limit(default 100,max 1000),sort name|size|modified,order asc|desc})`, `watch/unwatch`, `upload(location, folder, name, blob, {overwrite, upload id})` (existing name => `fs.entry.exists`; `uploadProgress` events), `download`, `createFolder`, `rename`, `run(FileOperation)` (copy/move/delete as job; delete goes to recycle bin unless `permanent`), `versions(location,path)`, `restoreVersion(location,path,snapshot,{overwrite})`.
- `app.jobs.cancel(id)`. `app.trash.list/restore(onConflict fail|skip|overwrite|rename)/delete/empty`.
- Storage requests in manifest map to `{storage: id}`; state in `context.storage`.
- Open: exact errors for locked pool (app.storage.not_found / app.backend.storage_unavailable).

### Backend

- `app.backend.fetch(path, init & {endpoint?}) -> Response` (Bearer token, no cookies; resolves with any status; unreachable => `sdk.backend.unreachable`), `url(path, {endpoint?}) -> string` with `?pithyx-token=` (valid 30 s to 5 min), `socket(path, {endpoint?, protocols?}) -> WebSocket` (ticket `?pithyx-ticket=`, 30 s), `token() -> {token, expiresAt}`.
- Default endpoint `api`; other `endpoint: 'name'`; unknown => `sdk.backend.endpoint_unknown` (params.endpoint). Helpers in backend.ts: `endpointPath`, `backendUrl` (refuses `..` leaving endpoint, strips pithyx-\* params), `socketUrl` (wss on https, ws on http).
- `context.backend.status` + `backendStatus` event.

### Events (Events, protocol.ts:478-525)

- launch (LaunchArgs), focus, blur, minimize, restore, themeChange, localeChange {language,locale,timeZone}, sizeClassChange {sizeClass}, openFile {file}, appUpdated {version} (reload to use), visibilityChange {visible}, connectionChange {online}, backendStatus {status}, permissionsChange {permissions} (complete list), storageChange {storage}, notificationAction {notification,action,data}, logLevelChange {level}, sharesChanged (only with files.shares), folderChanged, uploadProgress, jobChanged.
- Queries (`app.answer`): `beforeClose` => `{allow:boolean}` (desktop waits 5 s, then offers close anyway; asked only while `setDirty(true)`); `beforeUnload` `{reason: logout|shutdown|restart|update}` cannot be refused, 2 s. One handler per query, last wins.
- SDK keeps `context` fields current for theme, locale, sizeClass, permissions, visible, online, logLevel, storage, backendStatus.

### Logs

- `app.log.debug|info|warn|error|log(message, data?)`; never throws; every entry also to browser console at its level; info/warn/error go to the daemon, debug only while admin switched debug on (`context.logLevel`); log shows in Log Center source `app/<id>` with user (app.ts log doc; D305).
- Limits: message cut at 4000 chars; data max 20 fields, key 100 chars, value 1000 chars (app.ts:46-52 logRecord); client batching 250 ms, 100 per batch, buffer 500; rate limit bursts of 200 then 20/s, extra dropped (doc); API accepts <=100 entries/request (AppApiEndpoints.cs:44-48).
- "Never log secrets".

### Errors

- All failed calls reject with `PithyxError {code, params, detail?, requestId?}` (errors.ts:45-62); `isProblem(value)`; daemon problem details pass through unchanged. UI never shows request id (CLAUDE.md) except `ui.showError` dialog "request id to copy".
- `sdkErrorCodes` (errors.ts:11-28): `sdk.method.unknown`, `sdk.request.invalid`, `sdk.request.timeout`, `sdk.transport.closed`, `sdk.protocol.unsupported`, `sdk.transfer.cancelled`, `sdk.backend.endpoint_unknown`, `sdk.backend.unreachable`. Texts under `errors.sdk.*` (i18n).
- Daemon-side codes apps meet: `app.permission.denied`, `app.permission.not_requested`, `app.storage.too_large`, `app.storage.key_invalid`, `app.token.invalid`, `app.backend.unavailable`, `app.backend.missing`, `app.backend.endpoint_unknown`, `fs.entry.exists`, plus fs.\* (errors.json).
- Unexpected handler throw => `system.internal.unexpected` (errors.ts:92-96).
- Apps translate codes themselves; `detail` is English fallback.

### Questions

- How do I persist settings? `app.storage`.
- How do I detect user language? `app.context.locale` + `localeChange`.
- How do I know what the desktop supports? `app.supports('files.list')`.
- Why do I get `app.permission.denied`? permission not declared/granted/revoked.
- Is there React hook support? Not in sdk/ (core desktop has hooks in desktop/src/apps/sdk, not published). Unknown.

======================================================================

## sdk/ui.md (packages/ui)

### Installing

- `npm install @pithyx/ui@next react react-dom`; React 19 peer (^19.3.0) (packages/ui/README.md; package.json peerDependencies).
- Import styles: `@pithyx/ui/tokens.css` (package.json exports, `sideEffects: ["*.css"]`). Components are CSS modules; dist built by vite. Need to confirm whether CSS is injected automatically or `tokens.css` must be imported (unknown: only tokens.css is documented as separate export).
- Wrap in `<UiProvider locale="de-AT">` to set React Aria locale and package texts (English and German; other languages => English) (UiProvider.tsx:9-19). Without it follows the browser.

### Tokens without React

- `tokens.css` is the only token file (TODO.md:345); works without React (packages/ui/README.md; tokens.css:1-5).
- Theme: follows system via `light-dark()`; override with `<html data-theme="light|dark">`; accent presets via `<html data-accent="...">`: `petrol` (default), `ocean`, `indigo`, `plum`, `moss`, `graphite` (tokens.css:221-261). No red preset. Contrast checked in both themes (contrast.test.ts).
- Variables in tokens.css use names like `--color-bg`, `--color-surface-1`, `--color-text`, `--color-accent`, `--color-danger` (tokens.css:68-100) and brand palette `--petrol-*`, `--amber-*`; NOT the SDK's public `--pithyx-*` names (grep `pithyx-` in tokens.css: 0). SDK sets `--pithyx-*` from the desktop. Relationship between the two for third-party apps = open question.
- `@pithyx/ui/native`: class names for native elements without React Aria: `buttonStyles`, `controlStyles`, `textFieldStyles`, plus `cx`, `uiTexts` (src/native.ts).
- Fonts: IBM Plex Sans (UI), JetBrains Mono per CLAUDE.md; whether tokens.css ships @font-face unknown.

### Components (src/index.ts exports)

- Button (variants via `ButtonVariant`), IconButton, ContextMenuPopover, Badge, Checkbox, ProgressBar, Switch, Dialog (`DialogProps`), Menu, MenuItem, MenuPopover, MenuSeparator, MenuTrigger, `useDeliberatePress`, TextField, PasswordField, SearchField, OtpField, Tooltip, TooltipTrigger, UiProvider, `cx`, `uiTexts`/`UiTexts`.
- Built on React Aria Components: keyboard, touch, screen reader (README). Texts English+German.
- README also names "code fields", "buttons, text, password, code and search fields" (code field not in index.ts exports: OtpField? unclear).
- No docs per component beyond TSDoc (typedoc.json); props types exported only for Button, IconButton, Dialog, Menu items, text fields, Tooltip, UiProvider.

### Questions

- Do I need the UI package? No; SDK works with plain HTML (theme.ts:8-9).
- Can I use it in Vue/Svelte? Components are React; tokens.css and `/native` classes are framework-free.
- Dark mode? Follows system / data-theme; SDK `themeChange` gives scheme.

======================================================================

## sdk/cli.md (packages/cli/src/cli.ts)

General: `pithyx <command> [project folder] [options]` (CLI:34-37 HELP); folder defaults `.`; `pithyx --help|-h`, `-v|--version`; no command => help, exit 2; help flag => exit 0; unknown command => exit 2 "Unknown command X. Run pithyx --help."; extra positionals => exit 2; uncaught error prints `pithyx: <message>` exit 1 (cli.ts:140-165,400-412). Exit codes: 0 ok, 1 findings/errors/failure, 2 usage. Commands: validate, lock, build, pack, dev, catalog (+ `help`). README table lists only validate, lock, pack, dev (README out of date for build and catalog).
Global option: `--frontend <folder>` built frontend (default `dist`) for validate/lock/pack/dev (CLI:75).

### pithyx dev

- `pithyx dev [folder] [--port 7400] [--host 127.0.0.1] [--endpoint name=url ...] [--no-docker] [--frontend dir]`. `--port` must be integer 1..65535 else "is not a port." exit 2 (CLI:252-256). `--endpoint` must be `name=url` (CLI:111-120). Runs until Ctrl+C; exit via process.exit(0).
- Needs valid `pithyx.json` ("pithyx.json is missing or not valid; run pithyx validate", start.ts:67).
- Details in first-app page. Vite alternative: `@pithyx/cli/vite` default export `pithyx(options?)` with options `project`, `validate`, `endpoints`, `docker` (vite.ts:25-31).

### pithyx build

- `pithyx build [folder] [--oci-out dir] [--pin prefix]` (CLI:48-53,201-206). Requires manifest backend and `backend/compose.dev.yaml` (error "the app has no backend to build" / "<path> is missing: pithyx build reads the backend's source form from it").
- Default: local `docker build` per service with `build`, tag `pithyx.local/<id>/<service>:<version>`, `docker save`, writes pinned `backend/compose.yaml` (first-line comment "Written by pithyx build from compose.dev.yaml") and `backend/images.lock.json`; host arch amd64 or arm64 only; services with `image` resolved from registry (build.ts:140-330). Output lines `Built <tag> for <arch>`.
- `--oci-out <dir>`: `docker buildx build --platform linux/amd64,linux/arm64 --provenance=false --sbom=false --output type=oci,dest=<dir>/<service>.tar,rewrite-timestamp=true`; needs `SOURCE_DATE_EPOCH` env or git checkout (else error). Pushes nothing.
- `--pin <prefix>` (with `--oci-out`): pins to `<prefix>/<id>/<service>:<version>@<digest>` from archives; without --oci-out error "--pin reads the archives of --oci-out: name that folder".
- Used by `pack --images` for offline.

### pithyx catalog

- Run in root of a catalog repository. Subcommand is first positional; branch folder second (CLI:289-300). Global: `--catalog <file>` default `catalog.json`; `--allow-reserved-id` (undocumented in HELP, used for project catalogs).
- `catalog check --base <commit> --author <login> [--index <file>]` pull request rules; missing --base/--author => exit 2; issues => exit 1; success "The pull request passes the catalog rules."
- `catalog pending [--index <file>]` prints JSON of versions in `apps/` not in catalog-next index.
- `catalog index <branch folder> --channel stable|beta (--add <package>... --source <checkout> --repository <url> --commit <sha> | --promote <folder>)`; writes index.json with next sequence; "Added a, b." or "Nothing new; the index has a new sequence."; channel other than stable|beta throws "--channel must be stable or beta".
- `catalog sign <branch folder> --key-id <id> [--append]`: Ed25519 PEM key from env `PITHYX_SIGNING_KEY` (never a flag); missing => exit 2; `--append` keeps other keys' signatures (rotation). Output "Signed <branch>/index.json with <id>."
- `catalog verify <branch folder> --channel stable|beta [--key <id>=<base64 pubkey> ...]`; with keys: "is valid and signed", without: "is valid (signature not checked)". `--key` must be `id=<base64>` else error.
- Exit codes: 0, 1 (issues, IssuesError), 2 (usage).

### validate / lock / pack (needed for first-app page; options exact)

- validate: `<id> <version> is valid.` / exit 1.
- lock: no extra flags. Registry auth: anonymous or `~/.docker/config.json` (no credential helpers).
- pack: `--out <file.pithyxapp|folder>`, `--images`.
- CLI JS API (index.ts): validateManifest, validateCompose, validateIcon, checkLayout, isSafePath, pack, packageName, readProject, RegistryClient, resolveLock, formatLock, isValidCron, describe, constants (MANIFEST_FILE, ICON_FILE, LOCK_FILE, CHECKSUMS_FILE, ARCHITECTURES, SUPPORTED_MANIFEST_VERSION, RESERVED_ID_PREFIX).

### Questions

- Why does `pack` refuse? validate errors; run `validate` (CLI:207-215).
- Where is the signing key? env `PITHYX_SIGNING_KEY` only.
- Default dev port? 7400.
- Does dev need Docker? Only for a backend without `--endpoint`/`--no-docker`.
- Is there a `pithyx create`? No.

======================================================================

## Not in code (plan only) summary

- Template scaffolding command / template repos content (D326): repos private, not readable.
- Keep-alive lease for background work (CLAUDE.md Apps), not found in sdk or daemon.
- Elevated rights UI + risk texts, docker.socket/host.full (A16, D311 reserved); schema accepts, box refuses.
- Web apps with own port `kind: "web"`, SSO; widgets; app-to-app (D300; manifest.ts:12).
- The special folder "Apps" (A15b, D314); email/push update notifications (D329 "later").
- Published-state of npm packages; website/typedoc API reference generation output.

## Open questions / contradictions

1. `app.backend.unsupported` error text says "installs apps without a backend only" but only defined in ErrorCodes.cs:62, never thrown (grep). Stale? Backends clearly run (Docker runtime).
2. Endpoint name length: manifest/proxy `[a-z][a-z0-9-]{0,31}` (S:260,425; AppProxy EndpointSegment) vs SDK `ENDPOINT_NAME` `{0,62}` (sdk/src/backend.ts:17).
3. `sdk` manifest range: validated syntactically only; no daemon check against host protocol found. Does the box refuse an app whose `sdk` range does not match? unknown.
4. README of @pithyx/cli lists 4 commands; code has `build` and `catalog` too (help text at CLI:34-90). README also says dist-tag `next` only; D306/CLAUDE.md say rc.2 was also `latest`.
5. CLAUDE.md Apps says "Background work needs a keep-alive lease": no code found.
6. tokens.css (`--color-*`) vs SDK `--pithyx-*`: ui components read only `--color-*`, `--space-*`, `--control-height`, `--radius-*` (grep of packages/ui/src/_.css, zero `--pithyx-` uses). So a sandboxed app using @pithyx/ui must import tokens.css; it is unknown whether the SDK's `--pithyx-_` and the ui tokens are kept in sync.
7. Elevated rights: schema allows host.network, host.pid, device, capability (non-blocked); not verified they are implemented end to end in Docker runtime today (ComposeRewriter passes cap_add etc. under rights; install dialog text exists).
8. Image loading: whether pulls by digest need internet at install from sideloaded packages without `backend/images/`; where/when pulled (runtime start vs install).
9. `app.manifest.version_unsupported` message wording uses "manifest version"; fine.
10. Sideload of catalog-less apps: can the community catalog apps be updated from a sideloaded app? (D334 says sideload with an id in an enabled catalog gets updates from it): maybe worth a FAQ, behavior in StoreService line ~1042.
11. How to contribute to project catalogs (process, who reviews, SLAs): only D328 text; no contributor doc found.
12. Exact install-dialog behavior for `uid`/`gid` display etc. not checked.
13. PackageReader: gzip bomb and "padding" rules (MaxPaddingBytes 64 KB) exist; error reasons list in errors.json app.package.invalid truncated in my read.
14. Upload limits of the proxy: unlimited body; WebSocket activity timeout 24 h (AppProxy.cs:62).
