# Developer docs: desktop (research notes)

Paths relative to repo root /home/user/pithyx. Facts only from code unless under "Not in code".

## 1. architecture.md

### Overview

- Stack: React 19, Vite 8, TypeScript, zustand 5, TanStack Query 5, @tanstack/react-virtual, react-aria-components, i18next 26 + react-i18next 17 + intl-messageformat. desktop/package.json:14-42
- React Compiler enabled via babel preset (no hand memo): desktop/vite.config.ts:218 (`babel({ presets: [reactCompilerPreset()] })`)
- Workspace packages (@pithyx/sdk, @pithyx/ui) compiled from source via resolve condition `pithyx-source`: desktop/vite.config.ts:226; packages/ui/package.json:37
- Entry: desktop/src/main.tsx. Order: applyStoredTheme(), applyStoredAppearance(), QueryClient, `await initI18n()`, render <App/>. main.tsx:12-44
- QueryClient: retry up to 3 failures unless session expired; any query error "auth.session.expired" invalidates queryKeys.session (back to login). main.tsx:20-34
- Source folders desktop/src: shell, wm, desktop, items, apps, realtime, api, platform, theme, design, errors, auth, installer, network, ui, plus i18n.ts, i18nNamespaces.ts, icuFormat.ts.
- Two entry HTMLs: desktop/index.html (shipped) and desktop/design.html (design gallery, dev only, `pnpm design` = vite on port 5174 --strictPort). desktop/package.json:7; vite.config.ts:231 comment
- Loading path: sign-in chunk first; Desktop chunk (`src/desktop/Desktop.tsx`) loaded by `loadDesktop()` = dynamic import + `loadDesktopTranslations()`; sign-in screen prefetches; if a session hint exists (auth/sessionHint.ts) it loads right away. desktop/src/App.tsx:18-45
- Not lazy()/Suspense for Desktop on purpose (React holds back content up to 300 ms). App.tsx:18-21
- Installer UI only lazy-loaded on the live ISO. App.tsx:59-60
- Dev server proxies /api and /ws to daemon, rewrites Origin for WS (daemon accepts own origin only). vite.config.ts:240-257
- Build writes precompressed .br/.gz for js,css,html,svg,json,txt,map (>=1 KB). vite.config.ts:123-150
- Docs rule: visible desktop change must update website docs en+de. desktop/CLAUDE.md:5

### Shell and window manager

- Size classes (viewport width): compact <600 px, medium 600-1023, expanded >=1024. `useSizeClass()` via matchMedia, no polling. desktop/src/wm/sizeClass.ts:5-27; types.ts:31
- Ultrawide workspace >= 2400 px offers thirds/two-thirds snap (`ULTRAWIDE_WIDTH`). desktop/src/wm/geometry.ts:22
- Snap zones: left,right,left-third,center-third,right-third,left-two-thirds,right-two-thirds. wm/types.ts:22-29
- Window placement kinds: free | maximized | snapped. wm/types.ts:34
- MAX_WINDOWS = 50 (daemon enforces same: LayoutRules.MaxWindows). wm/state.ts:6; daemon/src/Pithyx.Daemon/Layouts/LayoutRules.cs:13
- WM state is a zustand store `useDesktop`; every change via `dispatch(action)` -> `reduce()`. wm/store.ts:12-17. Actions: open, focus, close, closeAll, minimize, maximize, restore, toggleMaximize, setArgs ... wm/types.ts:72-80
- `openApp(id, launch, {session})`: `single` apps focus the user's existing window on this device and emit `launch`; `multi` opens new; false if unknown/not allowed/full. wm/store.ts:19-60
- WindowOptions per app: `instances: 'single'|'multi'`, defaultSize, minSize. wm/types.ts:38
- Keyboard shortcuts: all Alt+Shift: Space=window menu, Left/Right=snap, Up=maximize, Down=restore/minimize ("down"), PageDown/PageUp=cycle windows. Reason: browsers/OS do not claim Alt+Shift. wm/keys.ts:18-45. UI labels in i18n/en/desktop.json:109-111 ("Alt+Shift+←" etc.)
- Last free window size per app remembered; daemon-stored, shared across devices: `PUT /api/desktop/preferences/{appId}`; only when viewport >= 1024 px. wm/persistence.ts:179-191; wm/preferences.ts:3
- Shell components (desktop/src/shell): DesktopShell, Taskbar, StartMenu, AppSwitcher, Window, Wallpaper, Notifications/NotificationCenter/NotificationPanel, TransfersButton, JobList, StickyNote, DesktopIcon, AppIcon, toasts.ts, storageAlerts.ts.
- Taskbar position: bottom|top|left|right, saved in layout. wm/layout.ts:78
- Desktop items (icons, notes, files) in desktop/src/items; per user, shared across devices (daemon `/api/desktop/items`, PATCH uses If-Match). daemon/src/Pithyx.Daemon/Layouts/LayoutEndpoints.cs:46-58
- Account menu items: language (menu.language), appearance (menu.theme), accent (menu.accent), wallpaper, taskbar position, "Get desktop from" (menu.getDesktop). i18n/en/desktop.json:9-45
- Third-party windows: `<iframe sandbox="allow-scripts allow-forms">` (no allow-same-origin). desktop/src/apps/sandbox/SandboxApp.tsx:123
- Host layer `apps/host/AppHostLayer.tsx` keeps app connections; `apps/host/connections.ts` (`emitTo(windowId, event, data)`, `setReady`).

### Core apps

- Registry: `coreApps` array, `desktop/src/apps/registry.tsx:45-176`. Ids in `desktop/src/apps/ids.ts` (`coreAppIds`), mirrored by daemon `Apps/CoreApps.cs` (ids.ts:1-4).
- Table (id | name key `apps.<id>` in desktop ns | label en | flags | instances | default size | extra translation namespaces loaded):
  - org.pithyx.files | "Files" | granted | multi | 920x580 (min 320x280) | files | registry.tsx:46-63
  - org.pithyx.storage | "Storage" | adminOnly | single | 1000x660 | storage | :64-79
  - org.pithyx.settings | "Settings" | adminOnly | single | 960x640 | settings, storage, users, network | :80-97
  - org.pithyx.monitor | "Resource Monitor" | adminOnly | single | 980x680 | monitor | :98-113
  - org.pithyx.logs | "Log Center" | adminOnly | single | 1040x640 | logs | :114-129
  - org.pithyx.viewer | "Viewer" | granted, launchOnly (opened with a file, not in menu) | multi | 900x640 | viewer | :130-146
  - org.pithyx.about | "About Pithyx" | none | single | 560x620 | none | :147-159
  - org.pithyx.store | "Store" | adminOnly | single | 960x660 | store | :160-175
  - Labels: i18n/en/desktop.json:177-189
- Flags: `adminOnly` listed only for admins; `granted` given to users by a grant (daemon `CoreApps.Granted`); `launchOnly`; `thirdParty`. registry.tsx:24-39
- Legacy short ids (files, viewer ...) mapped by `currentAppId()` for pre-migration-0022 log entries. apps/ids.ts:15-40
- Every app component loaded with `lazy(() => import(...))` when first window opens, never at login; namespaces loaded alongside via `loadTranslations(...)`. registry.tsx:56-62
- Third-party apps: `thirdPartyApp(app)` -> one shared `ThirdPartyWindow` component; window options from manifest. registry.tsx:179-204. List from topic `apps` (`useInstalledApps`, apps/installed.ts:1-20).
- `appsFor(allowed)` = start menu list (excludes launchOnly). registry.tsx:214
- Manifest text lookup: exact language, base language, then `en`. apps/installed.ts:39
- SDK in-process transport: `CoreSdkApp` wrapper: `inProcessPair()` -> `serveApp(hostEnd, {context, methods, extensions})` on host side; `createApp(appEnd, {themeRoot})` yields the same `PithyxApp` a sandboxed app gets from `connect()`. desktop/src/apps/sdk/CoreSdkApp.tsx:1-117 (imports `@pithyx/sdk`, `@pithyx/sdk/host`)
- One connection per window; `useEffect(() => open(), [windowId])` justified in comment (sync with SDK). CoreSdkApp.tsx:108-110
- Core app permissions passed per window: files `['files.shares','notifications']`, viewer same, monitor/settings/storage `['notifications']`, logs/store `[]`. apps/*/ *Window.tsx (e.g. apps/files/FilesWindow.tsx:12-14)
- Core-only extras: `internal` option (apps/sdk/internal.ts, filesInternal.ts, monitor.ts, network.ts, store.ts, users.ts, backup.ts, logs.ts, system.ts, viewerInternal.ts): internal host methods/events not in the public SDK.
- About app does NOT use CoreSdkApp (imported directly: registry.tsx:156-158; no CoreSdkApp in apps/about).
- `appContext()` builds the init context shared by core and sandboxed apps: version (`PITHYX_VERSION` = **PITHYX_VERSION** define), locale (`formatLocale`), size class, theme (`currentTheme()`), permissions. Core apps have no backend/storage. apps/sdk/context.ts:1-40
- Brokers: `brokerFor` (apps/sandbox/broker.ts) shared dialogs/clipboard/etc.; `fileBrokerFor` (apps/sdk/fileBroker.ts).
- Third-party isolation: identity from MessageChannel port (CLAUDE.md "Apps"); code: apps/sandbox/SandboxApp.tsx, appBroker.ts, appApi.ts, appLogs.ts.
- Window lease: topic `apps/+/context` reducer; apps/useAppLifecycle.ts for core.

### Data and realtime

- REST: one client `api = createApiClient()` from generated `@pithyx/api-client`. desktop/src/api/client.ts:1-3. Use `unwrap(await api.GET(...))` (throws PithyxApiError with code, params, requestId, status). api/queries.ts:11-18; errors/describe.ts:12-23
- Query keys: `queryKeys = {setup, session, health}`. api/queries.ts:5-9
- `/api/setup` also carries system language/region, applied by `applySystemRegion`. api/queries.ts:12-16
- WebSocket: single `RealtimeConnection` per tab at `ws(s)://<host>/ws`. realtime/connection.ts:94-97
- Protocol client->server: `{type:'sub'|'unsub', id:'c<n>', topic}`; server->client types: `snap`, `event`, `error`, `ack`. connection.ts:135-181
- Subscriptions reference-counted; re-sent after every open. connection.ts:53-63,100-105
- Reconnect: `reconnectDelay(attempt)` = min(30000, 500*2^attempt) * (0.5 + random/2) (exponential backoff with jitter, 0.5 s .. 30 s, randomized to 50-100%). realtime/reducers.ts:125-128
- Close codes (packages/api-client/src/realtime.ts:13-21): NormalClosure=none, DaemonRestart=reconnect, ClientTooSlow=reconnect, MessageTooBig=reconnect, SessionEnded=login (show login, no reconnect), Forbidden=stop. Unknown code (e.g. 1006) = reconnect. realtime.ts:28-50
- Status values: idle|connecting|open|reconnecting|stopped (zustand `useRealtime`). connection.ts:12-22
- `useTopic(topic)` hook = subscribe + narrow selector; undefined until first snapshot. realtime/useTopic.ts
- `realtime.listen(topic, cb)` for concrete members of a topic family (folder, job) with own state. connection.ts:65-81
- Reducers: `reducers: {[K in TopicName]: Reducer<K>}` - new daemon topic fails build until handled. reducers.ts:9-12
- Topics in reducers.ts: storage/disks (upserts/removals delta), files/+/+, jobs/+, jobs, notifications (created|read|deleted), session/permissions, apps/admin, store/admin, apps/+/context, storage/pools, storage/alerts, system/info, system/network, shares/list, thumbnails/+/+, system/resources, system/processes. reducers.ts:10-96 (+ others after line 96? check file)
- Error messages over WS go to `useErrorStore.current` (ErrorDialog). connection.ts:161-165
- Layout memory (per user AND device):
  - Device id from cookie (`DeviceCookie.Read`), daemon table `desktop_layouts` unique (user_id, device_id). daemon/src/Pithyx.Daemon/Layouts/LayoutService.cs:32-34; LayoutEndpoints.cs:99
  - Layout JSON: version=1 (`LAYOUT_VERSION`), viewport, taskbar, windows[{id,app,screen,title,rect,placement,minimized,args}], order, focused. desktop/src/wm/layout.ts:13-37
  - Never contains permissions; on restore, windows of unknown/not-allowed apps are skipped (`skipped` count -> toast `desktop.restoreSkipped`). wm/persistence.ts:34-37,157-162,243-247
  - Save: debounced `SAVE_DELAY = 1000` ms; `PUT /api/desktop/layout` with `If-Match: "<version>"`; on `desktop.layout.conflict` retries once with the server version (this tab wins); version from `ETag`. persistence.ts:19,67-96
  - Only visible + focused tab saves (`isActiveTab`). persistence.ts:57-60
  - Flush with `keepalive` on `pagehide` / visibility hidden. persistence.ts:104-120
  - Sign-out waits max 1000 ms for last save (`SIGN_OUT_SAVE_WAIT`). persistence.ts:122-135
  - First layout fetch (`GET /api/desktop`) retried up to 12 times (`RESTORE_RETRIES`, ~5 min); until fetched nothing is saved. persistence.ts:137-138,205-212
  - Limits (client + daemon): app data (sum of window args, UTF-8 bytes) 16 KB (`APP_DATA_LIMIT`, `MaxAppDataBytes`); whole layout 48 KB (`MaxLayoutBytes`, daemon only); 50 windows. Client drops args of lower windows first when over budget. wm/layout.ts:14-57; LayoutRules.cs:13-17,50-58. Error codes desktop.layout.invalid / desktop.layout.too_large (params maxWindows, maxAppDataKb).
  - Expiry: layout expires 90 days after save (LayoutService.cs:11 comment); SQLite, no Redis.
  - "Get desktop from ...": `GET /api/desktop/devices`, `POST /api/desktop/layout/copy`; applied via `applySavedLayout`. LayoutEndpoints.cs:39-40; persistence.ts:164-168
  - Appearance (accent, wallpaper) per user across devices: `PUT /api/desktop/appearance`. LayoutEndpoints.cs:42-44
- Server state vs React: persistence uses useQuery with `staleTime: Infinity, gcTime: 0` for ['desktop'].

### platform/browser.ts (secure-context fallbacks)

- File: desktop/src/platform/browser.ts. Exports: `isSecureContext()`, `randomId()`, `copyText(text, fallbackEl?)`, `selectContents(el)`, `openInNewTab(url)`.
- `randomId()`: RFC 4122 v4 from `crypto.getRandomValues` (works in insecure contexts; `randomUUID` does not). browser.ts:13-19
- `copyText`: Clipboard API only if secure + 'clipboard' in navigator; else hidden textarea + `execCommand('copy')`; else selects `fallback` element and returns false (caller shows hint). browser.ts:26-65
- `openInNewTab`: `window.open(url,'_blank','noopener,noreferrer')`. browser.ts:83-85
- ESLint rule (eslint.config.js:112-175, block starts "Pithyx works without a secure context"): applies to desktop/src, sdk/src, packages/ui/src, e2e/apps/_/src; ignores browser.ts and _.test.ts(x).
  - `no-restricted-properties`: crypto.randomUUID, crypto.subtle, navigator.{clipboard,serviceWorker,credentials,storage,locks,share,mediaDevices}, document.execCommand, window.{isSecureContext,crypto,navigator}, globalThis.{crypto,navigator}, window.open. Message: "Needs a secure context; use desktop/src/platform/browser.ts."
  - `no-restricted-globals`: Notification, PublicKeyCredential, isSecureContext, showSaveFilePicker, showOpenFilePicker, open.
  - window.open / open message: "A new tab must not reach the desktop; use openInNewTab in desktop/src/platform/browser.ts."
- Test: desktop/src/platform/browser.test.ts (randomId).
- Other lint rules in same config: icons only from `ui/icons.ts` (no direct @tabler/icons-react, eslint.config.js:~85-110); `i18next/no-literal-string` mode `jsx-text-only` on desktop/src and packages/ui/src .tsx (eslint.config.js:~176-181); react-hooks recommended-latest.
- No ESLint rule found banning `useEffect` for data (desktop/CLAUDE.md states the rule; only reactHooks recommended is enforced).
- Language/theme/accent/layout fall back safely when localStorage throws (try/catch): i18n.ts:189-196, theme/preference.ts:9-16.

### Not in code (plan only) / mismatches

- "Directory listings stream in chunks over the WebSocket": code uses cursor pages over REST (TODO.md:960 "pages instead of a WebSocket stream"). Topic `files/+/+` only reports watching/deltas.
- Web Worker for heavy sort/filter: no `new Worker` in desktop/src; TODO.md:979 open item.
- "Cached data first, then reconcile", virtualization: virtualization exists (FolderView.tsx, LogsApp.tsx via useVirtualizer); optimistic actions in FilesApp, SharesPage, EditShareDialog.
- Prefetch on hover, thumbnails, tus uploads: TODO.md:960 says open.
- Live sync of window positions: none by design.
- Per-device layout via "Get desktop from": implemented.

### Questions developers will ask (architecture)

- How do I add a core app? Add id to apps/ids.ts (+ daemon Apps/CoreApps.cs), entry in coreApps registry.tsx, `apps.org.pithyx.<name>` key in i18n/\*/desktop.json (test i18n.test.ts:"name every core app" enforces), Window component wrapping CoreSdkApp with permissions. Evidence above. Daemon grant side: unknown beyond CoreApps.Granted.
- Why can't I call navigator.clipboard / crypto.randomUUID? ESLint rule above; use platform/browser.ts.
- Do core apps run in iframes? No; desktop context with in-process SDK transport (CoreSdkApp.tsx:23-26). Only third-party iframes.
- How does reconnect work? See reconnectDelay and closeAction.
- Where do I get live data? `useTopic()` / `realtime.listen`; REST via `api` + TanStack Query.
- Where is layout stored? SQLite `desktop_layouts`, per user+device, 90 days.
- How many windows? 50.
- How do I run the desktop alone? `pnpm --filter @pithyx/desktop dev` (vite, proxy to daemonUrl; see vite.config.ts top for daemonUrl env name: unknown, not read).

## 2. i18n.md

### Keys

- Rule (CLAUDE.md "Errors and i18n"): no user-visible literals; keys `area.screen.element`; errors under `errors.<code>`; English reference, German required; ICU plurals and params; German informal "du"; never name companies/products.
- Enforcement of "no literals": ESLint `i18next/no-literal-string`, mode `jsx-text-only` (only JSX text nodes, not attributes), desktop/src/**/\*.tsx and packages/ui/src/**/\*.tsx, tests excluded. eslint.config.js:~176-181
- Files: i18n/<locale>/<namespace>.json, locales `en`, `de`. Namespaces (key counts approx, grep): auth, cli, desktop, errors, files, installer, jobs, logs, monitor, network, notifications, settings, storage, store, ui, users, viewer (17 files). ls i18n/en
- Nested JSON; keys are dotted paths in i18next (e.g. `menu.themeChoice.system`). Example: i18n/en/desktop.json:9-45
- Typed keys: `Resources` interface imports en JSON types; `CustomTypeOptions { defaultNS: 'desktop', resources }`. desktop/src/i18n.ts:22-40,108-113. (types list excludes ui and cli: those are handled separately)
- `ui.json`: texts of @pithyx/ui components (password show/hide, search clear), bundled with the package, no i18n lib needed. packages/ui/src/texts.ts:1-12; excluded from desktop glob i18n.ts:49-55
- `cli.json`: texts of mock desktop of `pithyx dev` (@pithyx/cli); excluded from desktop glob. i18n.ts:47-51
- Namespace chunking (vite.config.ts:196-211, i18nNamespaces.ts):
  - signInNamespaces = ['desktop','auth','errors'] -> chunk `i18n-<lang>` loaded before first render
  - desktopNamespaces = ['notifications','jobs'] -> chunk `i18n-<lang>-desktop` loaded with the Desktop chunk
  - every other namespace -> `i18n-<lang>-<ns>`, loaded by the app that needs it via `loadTranslations('ns',...)` (registry.tsx)
  - only active language is ever loaded; switching language reloads the same set (`requested`). i18n.ts:56,79-83,198-212
- Loading API: `initI18n(language?)`, `loadTranslations(...ns)`, `loadDesktopTranslations()`, `loadAllTranslations()` (design gallery). i18n.ts:86-103,215-233
- `fallbackLng: false` - every language must have every key, no fallback. i18n.ts:226-227

### ICU messages

- Syntax: ICU MessageFormat (plural, select, params). Examples: `"{count, plural, one {# window} other {# windows}}"` i18n/en/desktop.json deviceDetail; `{reason, select, ...}` in errors.json (app.compose.\*).
- Compiled at BUILD time: vite plugin `pithyx-compiled-translations` handles `?compiled` imports; `compileMessages()` in desktop/scripts/compile-messages.ts parses with @formatjs/icu-messageformat-parser (`ignoreTag: true`) and stores a JSON AST prefixed by `COMPILED = '\u0001'`; plain-text messages stay strings. Production build swaps the parser for its `no-parser.js` entry (about 8 KB gzip saved on the sign-in path). vite.config.ts:89-120; scripts/compile-messages.ts:1-30; icuFormat.ts:1-6
- Runtime: `icuFormat` i18next format module, `IntlMessageFormat` over the AST, cached by `lng + message`; on missing/wrong param returns the KEY. icuFormat.ts:9-41
- Tags stay text (react-i18next `<Trans>` replaces). compile-messages.ts:14
- `interpolation.escapeValue: false` (React escapes). i18n.ts:230
- Daemon renders same messages for out-of-band channels with `IcuMessageFormatter` (daemon/src/Pithyx.Daemon/Localization/IcuMessageFormatter.cs); golden file tools/i18n/icu-golden.json must match in both: desktop/src/icu-golden.test.ts (vitest) and daemon IcuGoldenTests.cs. icu-golden.test.ts:1-5; daemon/tests/.../Localization/IcuGoldenTests.cs:11-26
- Format locale (dates, numbers) is language + system region e.g. `de-AT`, `en-DE`; plural rules/texts stay with language only. `formatLocale()`, `useFormatLocale()`. i18n.ts:147-160
- Dates: store UTC ISO 8601, format with `Intl` per user tz/locale (CLAUDE.md "Misc"); desktop/src/desktop/format.ts (+ format.test.ts) exists.

### Locale selection

- Languages supported: `languages = ['en','de']`. i18n.ts:42
- Priority at startup: stored choice (localStorage `pithyx.language`, only 'en'|'de') > browser `navigator.languages` (first whose base subtag is en/de) > system language (from `GET /api/setup`, `applySystemRegion`) > 'en'. i18n.ts:106,125-136,189-196,215-217
- Menu: account menu, Language submenu: "Automatic" (`menu.languageAuto`) plus each language shown in its own name (`Intl.DisplayNames`). desktop/src/desktop/LanguageOptions.tsx; i18n/en/desktop.json "menu.language": "Language"
- "Automatic" = remove storage key and use detectLanguage(browser). i18n.ts:198-212
- `<html lang>` set on init and change. i18n.ts:210,217
- Stored per device (browser), comment says "moves to the user profile once profiles exist". i18n.ts:105. (Not per user on the daemon today.)
- System language & region: Settings > Regional settings, label `settings.regional.title` "Regional settings" (i18n/en/settings.json:150-153); field "System language" (`regional.language`), hint: "The sign-in page uses it, and so does everyone whose browser asks for a language Pithyx does not have." (settings.json:367-376). Daemon `SystemRegion.Languages = ["en","de"]`, default en. daemon/.../SystemSettings/SystemContracts.cs:64-68
- Automatic users switch when system language changes (if browser language unsupported). i18n.ts:162-186
- Manifest texts (third-party apps): exact lang, base lang, then en. apps/installed.ts:39
- Apps get locale in init context (`formatLocale`). apps/sdk/context.ts:11

### Adding a language (what the code requires today; no single doc)

- Create i18n/<code>/ with every file that i18n/en has (parity test: "have the same files in every language"). desktop/src/i18n.test.ts:37-43
- Add code to `languages` (i18n.ts:42); `detectLanguage` and `storedLanguage` hardcode `'en'`/`'de'` (i18n.ts:131,191) - must be edited too.
- Add to `ariaLocales` in desktop/vite.config.ts:121 (React Aria strings; currently ['en-US','de-DE']).
- Add to daemon `SystemRegion.Languages` (SystemContracts.cs:64) and daemon test enumerates i18n/ dirs (ErrorCodesTests/ SystemContracts test: daemon/tests/.../Errors/ErrorCodesTests.cs:11 region).
- Other hardcoded lists: tools/design-shots/matrix.ts languages ['en','de']; sdk/src/i18n.test.ts; daemon StoreService.cs (grep hit, purpose unknown); website separate (website/src/content/i18n/{en,de}.json, content/docs/de/).
- No script or doc for this: "unknown whether an `add-language` tool is planned".

### Checks that enforce parity

- desktop/src/i18n.test.ts (vitest, runs in `pnpm test` in CI `frontend` job):
  - same flattened keys in en and de (:31-35)
  - same files in every language, includes signIn+desktop namespaces (:37-43)
  - every SDK transport code (`sdkErrorCodes`) has `errors.<code>` in en+de (:45-50)
  - every core app has `desktop.apps.<id>` (:52-56)
  - every API `errorCodes` has `errors.<code>` in en+de (:58-63)
  - all texts valid ICU for IntlMessageFormat (:65-77)
  - detectLanguage / formatLocale unit tests (:80-109)
- daemon xUnit `ErrorCodesTests`: code matches `^[a-z0-9_]+\.[a-z0-9_]+\.[a-z0-9_]+$`; has English text in embedded i18n/en/errors.json; has German text in i18n/de/errors.json. daemon/tests/Pithyx.Daemon.Tests/Errors/ErrorCodesTests.cs
- `ICU golden` test above.
- tools/checks/repo-check.ts: no i18n-specific key check found (only skips i18n/ for the non-English comment check, :171). Em dash check applies (:161).
- Not enforced in code found: German "du" (informal) - no check; "never name companies/products" - no check found.
- Website: error reference page generated from i18n/<locale>/errors.json (website/CLAUDE.md:"Generated pages"); website job runs on changes under i18n/ (website/CLAUDE.md:10).

### Error texts

- Codes: `area.object.reason` lowercase, dots, underscores in words; defined once as C# constants (daemon/src/Pithyx.Daemon/Errors/ErrorCodes.cs), TS union `errorCodes`/`ErrorCode` generated into @pithyx/api-client (don't edit generated).
- JSON path: `errors.app.app.busy` for code `app.app.busy` (nested objects by segment; errors.json top level = area). i18n/en/errors.json:1-8; daemon test splits code on '.'.
- API: RFC 9457 problem details with `code`, `params`, `detail` (English fallback), `requestId`. API never returns translated text. (CLAUDE.md; PithyxApiError class in api-client.)
- Desktop lookup: `translateError(t, err)` = `t(code, {ns:'errors', ...params})`; code `'network'` (fetch TypeError) uses `error.network` in desktop ns. desktop/src/errors/ErrorNotice.tsx:10-17; describe.ts:12-23
- `ErrorNotice` shows message in `role="alert"`; request id NEVER shown, only `console.error('Pithyx error <code>, request id <id>')`. ErrorNotice.tsx:29-50. Layout save failure logs request id likewise. persistence.ts:92
- WS `error` messages feed `useErrorStore` -> ErrorDialog. connection.ts:161-165
- SDK transport codes (`sdkErrorCodes` in sdk/src/errors.ts) texts live under `errors.sdk.*` (e.g. errors.sdk.backend.unreachable "The app could not reach its backend ({endpoint}). Check the connection to the box."). i18n/en/errors.json:367-375; i18n.test.ts:45-50
- Params used in ICU (`{id}`, select on `{reason}`) come from problem `params`.
- Daemon English texts: `ErrorMessages.English` from embedded i18n/en/errors.json for `detail`. daemon/.../Errors/ErrorMessages.cs:8-21
- Out-of-band channels (email, push, SSH MOTD) rendered server-side in recipient's language: rule in CLAUDE.md; code is IcuMessageFormatter; which channels exist today: unknown.

### Questions developers will ask (i18n)

- Why does my text show the key? icuFormat returns the key when a param is missing or wrong (icuFormat.ts:271-275); also namespace not loaded (call loadTranslations).
- Do I need to add de too? Yes, test fails otherwise (i18n.test.ts:31-35).
- Can I use `<b>` tags in messages? Tags are text for the compiler; use `<Trans>`. (compile-messages.ts:14)
- Why is my namespace not available in my app? Must call `loadTranslations('ns')` in registry.tsx lazy import.
- Where is the language saved? localStorage `pithyx.language`, per browser.
- Which formats do I use for dates? `Intl` with `formatLocale()`.

## 3. performance.md

### Budgets

- Bundle (gzip, `pnpm check:bundle` = `node tools/checks/bundle-budget.ts`, after `pnpm build`; runs in CI `frontend` job on every frontend change). tools/checks/bundle-budget.ts:1-12; package.json:25; ci.yml frontend job (last step)
  - Sign-in bundle = everything index.html loads before first render (script type=module + modulepreload + stylesheet) + largest `i18n-<lang>` chunk. Hard max 200 KB (`SIGN_IN_LIMIT = 200*1024`) AND at most baseline + 5 % (`TOLERANCE = 0.05`). bundle-budget.ts:25-28,100-116
  - Baseline in tools/checks/bundle-budget.json: `signInGzipBytes: 147995` (144.5 KB). Allowed = min(204800, round(147995\*1.05)=155395 B = 151.8 KB). Update only with `--update` and "owner's consent" (bundle-budget.ts:4-5,8,95-100).
  - Desktop bundle (what signing in adds: Desktop.tsx chunk + static imports + CSS, minus sign-in files, + largest `i18n-<lang>-desktop`): fixed limit 150 KB (`DESKTOP_LIMIT = 150*1024`), owner decision 2026-09-29. bundle-budget.ts:5-6,26,117
  - Data source: `desktop/node_modules/.cache/bundle-graph.json` written by vite plugin `pithyx-bundle-graph`. vite.config.ts:157-190
  - Chunking: sign-in `index` chunk (tags $initial), shared chunk (code shared by >= 2 apps, minShareCount 2), translations per language/namespace. vite.config.ts:233-255
  - Desktop/CLAUDE.md says "within 5 percent of recorded baseline" - code applies it to sign-in only; desktop is a fixed limit.
- Timing budgets, `e2e/tests/budgets.spec.ts` (Playwright, production build served by daemon, Chrome "Fast 4G" + 4x CPU slowdown, 1440x900, locale en-US, fresh context per round => cold cache):
  - `BUDGETS` (ms, medians): coldToLogin 1800; clickToFeedback 100; loginToDesktopWithoutArgon2 1000; reload 650. budgets.spec.ts:15-20
  - Measure = median of ROUNDS=5 sign-ins after 1 unmeasured warm-up round; `MIN_RUNS = 3` valid values else NaN (fails). budgets.spec.ts:12,34-49; e2e/tests/performance.ts:163-169
  - Reported, not budgeted: firstReload (V8 code cache write after update), argon2. budgets.spec.ts:59-64
  - Fast 4G profile: 9 Mbit/s down x0.9, 1.5 Mbit/s up x0.9, latency 60\*2.75 = 165 ms; CPU slowdown 4. performance.ts:9-15
  - Evidence for current numbers: runner spread for one commit (#98): reload 451-517 ms, cold start 1411-1575 ms; widened in D325 (2026-10-08, owner) from 1500->1800 (cold) and 500->650 (reload); click feedback 100 and login->desktop 1000 unchanged. budgets.spec.ts:13-14; TODO.md D325 (line ~1460); TODO.md:931 summary.
  - Files app budgets (same spec): `FILES_BUDGETS = {visitedFolder: 16, firstPage: 100}` ms, medians of 5, 1000 files per folder, measured by app's `performance.measure('pithyx:files:shown')` (apps/files/FilesApp.tsx:821). budgets.spec.ts:84-141
  - Daemon timing budget (Category=TimingBudget): first page of folder with 100,000 entries < 200 ms (`_firstPageBudget`); runs in the E2E job in Release. daemon/tests/Pithyx.Daemon.Tests/Files/ListingBudgetTests.cs:9-17,83; ci.yml e2e step "dotnet test ... TimingBudget"
  - Definitions of marks: sign-in usable = `input[autocomplete="username"]` present; desktop usable = `nav[aria-label="Taskbar"]` present and no unlabeled `<main>` covering (leaving cards with data-leaving ok); feedback = submit button gets `aria-busy="true"` after click. performance.ts:19-42
  - Motion: test uses reducedMotion 'no-preference' (real welcome card), trace off. budgets.spec.ts:23
- CI "5 percent regression on any tracked timing fails the build" (desktop/CLAUDE.md): NOT in code; budgets are absolute numbers.
- Daemon/box budgets from CLAUDE.md: RSS < 200 MB, CPU < 1 % idle (CI budgets): test for it not examined here (unknown).
- Rules of thumb in desktop/CLAUDE.md (text only): no spinner before 300 ms (App.tsx:190 comment; React Suspense 300 ms note); apps via dynamic import() when opened; high-frequency state via zustand narrow selectors; React Compiler instead of useMemo/useCallback; no useEffect for derived state/fetching (not lint-enforced); optimistic UI; virtualization.
- Offline-ish: nothing polls when an event source exists (CLAUDE.md); realtime store used.

### The perf gate

- Job `e2e` in .github/workflows/ci.yml:146-193. Runs: build daemon (`dotnet build daemon/src/Pithyx.Daemon`), `pnpm --filter "@pithyx/desktop..." build`, playwright chromium install, `pnpm e2e` (includes budgets.spec.ts), `pnpm design:test`, then daemon `TimingBudget` tests (Release).
- Trigger (D278): only when asked: label `e2e` on a PR (`gh pr edit <n> --add-label e2e`), `workflow_dispatch`, any PR to master or push to master. ci.yml:146-155. Not on push to dev.
- Env `PITHYX_SKIP_BUDGETS` = '1' on push to dev (E2E suite runs but budgets spec skips: "Performance budgets gate pull requests only"). ci.yml:159; budgets.spec.ts:26
- `daemon` job also sets PITHYX_SKIP_BUDGETS on dev pushes and filters out TimingBudget, StorageIntegration, DockerIntegration, NetworkIntegration. ci.yml:55-58,73-78
- E2E waits for `daemon` job (`needs: [changes, daemon]`, `always() && !cancelled()`) so tests do not skew timings (D280). ci.yml:149-153
- Runner: `${{ vars.CI_RUNNER_E2E || vars.CI_RUNNER || 'ubuntu-latest' }}` (label pithyx-e2e, pinned cores, D280). ci.yml:158. Playwright config: workers 1, retries 0. e2e/playwright.config.ts:24-26
- Test timeout of budgets test 240 s; each `expect.soft` so all four report.
- Bundle check is NOT label-gated: runs in `frontend` job whenever frontend files changed. ci.yml frontend job; CLAUDE.md "CI"
- When to add the label (CLAUDE.md "CI", D278): user flows, desktop shell, window manager, sign-in, setup, SDK transport, API contract, loading path or bundle, several PRs merged in a row; run on dev before owner verifies on 7484.
- Known flakiness (CLAUDE.md pitfalls): E2E reload varies ~450-520 ms on runner; load spoils timings: check `uptime`; daemon tests skew E2E budgets even on pinned cores (D280, 2026-10-06 saw 529 ms reload, 1506-1514 ms cold).
- Design/layout checks: `pnpm design:test` = playwright -c tools/design-shots/playwright.config.ts; starts the Vite design server (`design.html`), no daemon; test "no screen overflows or covers a title bar on <viewport> in <lang>" for phone 390x844 / tablet 820x1180 / laptop 1440x900 x en/de, per screen in the gallery; timeout 360 s. tools/design-shots/layout.spec.ts:16-30; matrix.ts:6-12; playwright.config.ts:3,23
- Ultrawide 3440x1440 covered only in e2e/tests/devices.spec.ts:118-119 (not in design-shots matrix; desktop/CLAUDE.md lists 4 viewports for "key flows").
- A11y: e2e/tests/a11y.spec.ts uses @axe-core/playwright; token contrast: packages/ui/src/contrast.test.ts (vitest).

### Measuring

- Manual tool: `node tools/perf/measure.ts --url http://<box>:7484 [--user admin --password '...'] [--runs 5]` (defaults: url http://127.0.0.1:7484, user admin, password 'correct horse battery' (test default), runs 5, min 3). tools/perf/measure.ts:1-23
- Runs both profiles `unthrottled` and `fast4g-cpu4x`, cold context per run, median of runs; prints: cold start to sign-in usable (+KB transferred), click to visible feedback, sign-in to desktop usable (+argon2), first reload (code cache cold), reload warm, hard reload (only measured with `extended: true`). measure.ts:28-63
- Needs a user that exists and Chromium via @playwright/test (E2E deps).
- Bundle: `pnpm build` (desktop) then `pnpm check:bundle`; prints per-file gzip sizes and totals; `--update` records baseline. bundle-budget.ts:80-92
- Files app in-page marks: `performance.getEntriesByName('pithyx:files:shown','measure')`. budgets.spec.ts:88-93
- Daemon Server-Timing header supplies argon2 time. performance.ts:70-72
- PR practice: "performance table in a pull request" (measure.ts:1-3); format of table: unknown beyond column list above.

### Questions developers will ask (performance)

- Why did my PR fail check:bundle? Sign-in > 151.8 KB (baseline +5 %) or > 200 KB, or desktop > 150 KB. Run `pnpm build && pnpm check:bundle`.
- How do I raise the baseline? `node tools/checks/bundle-budget.ts --update`, only with owner consent.
- Do I get perf gate without the label? No; label `e2e` or workflow_dispatch.
- Why skipped on dev? PITHYX_SKIP_BUDGETS.
- Budget failed but nothing slow in my diff? Check runner load/uptime, rerun once (CLAUDE.md pitfalls; D280/D325).
- Where do translations count in the bundle? Largest language's sign-in chunk and desktop chunk are included.

## 4. theming.md

### Tokens

- Single token file: packages/ui/tokens.css (267 lines; `@pithyx/ui/tokens.css`, exported in packages/ui/package.json:46), usable without React; imported by desktop/src/theme/index.css:2 (`fonts.css`, tokens, `base.css`).
- Naming (all CSS custom properties on `:root`):
  - raw palette: `--petrol-50..950`, `--amber-100..900`, `--neutral-0..1000`, `--green-light/dark`, `--orange-light/dark`, `--red-light/dark` (tokens.css:19-66)
  - semantic color: `--color-bg`, `--color-surface-1/2/3`, `--color-surface-hover/pressed`, `--color-text(-muted/-disabled)`, `--color-border(-strong)`, `--color-overlay`, `--color-focus`, `--color-selection`, `--color-success/warning/danger` (+ `-surface`, danger `-contrast`), `--color-highlight(-contrast)` (tokens.css:68-95)
  - shell: `--color-desktop`, `--color-taskbar(-solid/-border)`, `--color-menu(-solid)`, `--color-window-border(-focused)`, `--color-titlebar(-unfocused)`, `--color-desktop-label(-bg)`, `--color-note(-text/-bar)` (:97-112)
  - accent: `--color-accent`, `-hover`, `-pressed`, `-contrast`, `-subtle` (set per preset)
  - type: `--font-sans`, `--font-display`, `--font-mono`; sizes `--font-size-xs|sm|md|lg|xl|2xl|display-1|display-2` (0.75, 0.8125, 0.875, 1, 1.25, 1.5, 2, 2.5 rem); weights regular 400/medium 500/semibold 600/bold 700; line heights tight 1.2/normal 1.45/relaxed 1.6; `--letter-spacing-display: -0.02em` (:114-133)
  - spacing 4 px grid: `--space-0` 2px, `-1` 4, `-2` 8, `-3` 12, `-4` 16, `-5` 24, `-6` 32, `-7` 48 (:135-143)
  - radii: `--radius-xs` 4, `-sm` 6, `-md` 10, `-lg` 14, `-full` 999px (:145-150)
  - shadows: `--shadow-1..4` built from `--shadow-color`, `--shadow-color-strong` (:152-158)
  - motion: `--duration-instant` 100ms, `-fast` 160, `-normal` 240, `-slow` 400, `-fade` 160; `--ease-standard` cubic-bezier(0.2,0,0,1), `--ease-enter`, `--ease-exit` (:160-168)
  - z layers: desktop 0, windows 100, taskbar 5000, menu 6000, toast 7000, modal 8000, tooltip 9000 (`--z-*`) (:170-177)
  - sizes: `--touch-target` 44px, `--control-height` 36px, `--taskbar-size` 52px, `--titlebar-height` 40px, `--desktop-icon-width` 88px, `--desktop-cell-width` 96px, `--desktop-cell-height` 112px, `--desktop-grid-padding` 12px (:179-188). Cell/padding must stay in step with desktop/src/items/grid.ts:4.
- Media adaptations: `(pointer: coarse)` -> control-height & titlebar = 44px, taskbar 60px; `prefers-reduced-motion: reduce` -> duration-instant/fast/normal/slow = 1ms (fades `--duration-fade` stay 160ms); `prefers-reduced-transparency: reduce` or `forced-colors: active` -> solid taskbar/menu. tokens.css:191-213
- Public tokens for third-party apps (stable names, mapped from internal): `--pithyx-color-background|surface|surface-raised|hover|selected|text|text-muted|border|accent|accent-text|focus|success|warning|danger`, `--pithyx-font-ui|mono`, `--pithyx-font-size`, `--pithyx-radius`, `--pithyx-space`, `--pithyx-control-height`. Map in desktop/src/apps/sandbox/theme.ts:6-27; names exported as `themeTokenNames` from sdk/src/theme.ts:15; `applyTheme` in SDK. Density currently always 'comfortable' (theme.ts:48-49). Colors keep light-dark() form; app resolves with the color scheme the SDK sets.
- Theme object delivered to apps: `{scheme:'light'|'dark', density, tokens}`; change events on system scheme change, user theme choice, accent change. theme.ts:43-62
- Base styles: desktop/src/theme/base.css (box-sizing, body uses --color-bg/--color-text/--font-sans/--font-size-md/--line-height-normal; headings `hyphens:auto`, `overflow-wrap:anywhere` for German compounds).
- Contrast test: packages/ui/src/contrast.test.ts parses tokens.css, resolves light-dark() per theme and per accent; text pairs min 4.5 (`TEXT`), graphics min 3 (`GRAPHIC`); WCAG 2.2 AA target (desktop/CLAUDE.md). Runs in `pnpm test`.
- Rule: tokens.css is the only token file (desktop/CLAUDE.md "Shared UI").
- CSS: component styles are CSS modules (`*.module.css`), types generated by `pnpm --filter @pithyx/desktop css-types` (part of `pnpm lint`). package.json:15; desktop/package.json:10

### Light and dark

- Mechanism: `color-scheme: light dark` on :root and `light-dark(a, b)` for every semantic color; `:root[data-theme='light'|'dark']` forces `color-scheme`. tokens.css:7-17
- Choices `ThemeChoice = 'system'|'light'|'dark'` (`themeChoices`). theme/preference.ts:1-3
- Stored per device in localStorage `pithyx.theme`; 'system' = key removed and `data-theme` attribute removed (follows OS/browser). preference.ts:5-24,31-43
- Applied before first render (`applyStoredTheme()` in main.tsx:12) so no flash. A `pithyx:themechange` window event notifies listeners (`onThemeChoice`). preference.ts:6,45-51
- UI: account menu > Appearance (`menu.theme` "Appearance"), choices `menu.themeChoice.system` "Like the system", `.light` "Light", `.dark` "Dark". i18n/en/desktop.json:12-17; desktop/src/desktop/ThemeOptions.tsx
- Theme is a per-device choice; accent and wallpaper are per user (D281). theme/appearance.ts:6-9
- Apps' scheme: `scheme()` = forced data-theme else matchMedia('(prefers-color-scheme: dark)'). sandbox/theme.ts:29-37
- Design screenshots render light and dark via Playwright `colorScheme`. tools/design-shots/matrix.ts:9,16-26

### Accents

- Presets (6), in `ACCENTS` order and in tokens.css: petrol (default), ocean, indigo, plum, moss, graphite. theme/appearance.ts:3; tokens.css:220-266. Labels `menu.accents.*`: "Petrol", "Ocean", "Indigo", "Plum", "Moss", "Graphite" (i18n/en/desktop.json:25-32). Menu title `menu.accent` "Accent color".
- Light/dark values of `--color-accent` per preset: petrol #1a6e82/#5fb3c6; ocean #1f5fae/#7ab2f0; indigo #4a4fb8/#a3a7f5; plum #7d3b8c/#d59ae3; moss #4f6b1f/#aed17a; graphite #3f4a55/#c3ccd4. (hover, pressed, contrast, subtle also defined per preset)
- Applied by `<html data-accent="...">`; petrol is also the `:root` default. appearance.ts:48-50; tokens.css:220-221
- Curated list on purpose: no free color picker (AA contrast checked per preset in both themes); no red preset so accent never reads as danger. tokens.css:215-219
- Stored: daemon, per user, same on every device (`PUT /api/desktop/appearance`, returned by `GET /api/desktop`), mirrored to localStorage `pithyx.appearance` so first paint matches. Invalid values fall back to default per field (`checkedAppearance`). appearance.ts:15-73; LayoutEndpoints.cs:42-44
- Wallpapers: `WALLPAPERS = ['boxes','plain']`, labels `menu.wallpapers.boxes` "Pithyx boxes", `.plain` "Plain" (menu.wallpaper "Wallpaper"). appearance.ts:4; desktop.json:33-37. UI: desktop/src/desktop/AppearanceOptions.tsx (AccentOptions, WallpaperOptions); shell/Wallpaper.tsx.
- Default appearance `{accent:'petrol', wallpaper:'boxes'}`. appearance.ts:15
- Adding an accent: add to `ACCENTS`, add `[data-accent='x']` block with the 5 accent tokens in tokens.css, add `menu.accents.x` en+de (typed Resources), daemon validation of appearance value: unknown (not read).

### Fonts

- Three families, all OFL 1.1 (IBM Plex Sans under OFL via its LICENSE.txt), shipped unmodified, self-hosted woff2 (no CDN): desktop/public/fonts/{ibm-plex-sans,sora,jetbrains-mono}, `@font-face` with `font-display: swap` in desktop/src/theme/fonts.css.
  - IBM Plex Sans: Regular 400, Italic 400, Medium 500, SemiBold 600 (UI text). fonts.css:2-32
  - Sora: SemiBold 600, Bold 700 (word mark, large headings): used via `--font-display` in ui/Logo.module.css, auth/Auth.module.css, design screens. fonts.css:34-48
  - JetBrains Mono: Regular 400, SemiBold 600 (terminal, logs, paths) via `--font-mono`. fonts.css:50-63
- Stacks: `--font-sans: 'IBM Plex Sans', system-ui, sans-serif`; `--font-display: 'Sora', 'IBM Plex Sans', system-ui, sans-serif`; `--font-mono: 'JetBrains Mono', ui-monospace, monospace`. tokens.css:115-117
- tools/fonts/fetch-fonts.sh: downloads pinned versions with SHA-256 verification (Plex Sans 1.1.0 from IBM's npm package `@ibm/plex-sans`; Sora from sora-xor/sora-font commit 7f9a9c5d...; JetBrains Mono 2.304 release zip) and copies files plus licenses into desktop/public/fonts/. Copied files are committed; run script only to update or check provenance. Reserved Font Name "Plex": never subset, convert or rename. fetch-fonts.sh:1-22. Needs network, curl, python3.
- License notices: desktop/public/third-party-notices.json lists IBM Plex Sans, Sora, JetBrains Mono, Tabler Icons, React Aria Components, PDF.js, SkiaSharp, SecLists; shown in About (CLAUDE.md "Misc": every bundled asset in About notices). desktop/public/licenses/ holds others (pdfjs-dist, react-aria-components, seclists, skiasharp, tabler-icons-react).
- Fonts are not part of the gzip JS/CSS bundle budget (woff2 files served separately; precompress skips woff2: COMPRESSIBLE regex vite.config.ts:124).
- Website: no external fonts (website/CLAUDE.md:31) - separate from desktop.

### Not in code (plan only)

- Density setting ("a density setting changes the spacing tokens"): only comment at sandbox/theme.ts:48.
- User-chosen light/dark stored in profile: today localStorage only (per device).
- Passkeys/service worker after trust: out of scope here.

### Questions developers will ask (theming)

- How do I get the colors in my app (third-party)? Use `--pithyx-*` tokens via SDK `applyTheme`/Theme object; internal `--color-*` are not stable (sandbox/theme.ts:5).
- Can I use my own accent color? No, six presets (tokens.css:215-219).
- How do I force dark for testing? `document.documentElement.dataset.theme = 'dark'` or account menu; `localStorage pithyx.theme`.
- Why do I get light-dark() in token values? by design.
- Do fonts load from Google? No, self-hosted, unmodified.
- How do I check contrast? `pnpm test` (contrast.test.ts) / `pnpm --filter @pithyx/ui test` (assumed script name, not verified).
- Where is the token reference page? `pnpm design` gallery has `TokensScreen` (desktop/src/design/TokensScreen.tsx) - gallery is dev only.

## Open questions

- desktop/CLAUDE.md says "regression of more than 5 percent on any tracked timing fails the build" (also "recorded baseline" for timing); code uses absolute budgets only (budgets.spec.ts:15-20); only the sign-in bundle has a 5 % tolerance. Which text is authoritative?
- desktop/CLAUDE.md lists Playwright key-flow viewports incl. 3440x1440; design-shots matrix has only phone/tablet/laptop; ultrawide only in e2e/tests/devices.spec.ts:119. Which tests run all four?
- CLAUDE.md "directory listings stream over the WebSocket", "heavy sorting in a Web Worker": not in code (REST cursor pages; no Worker). Docs must say REST pages.
- Layout "per device": device id comes from a cookie (`DeviceCookie`); cookie name/lifetime not read. Needed for FAQ "why is my layout different on another browser".
- Layout expiry 90 days: from comment in LayoutService.cs:11 only; actual value in code not verified.
- Adding a language touches hardcoded 'en'/'de' in i18n.ts (131, 191), vite ariaLocales, daemon SystemRegion.Languages, design-shots matrix, sdk i18n test: is a one-place list planned? Also the exact list of daemon places (StoreService.cs hit unexplained).
- German "du" and "no company names" rules: no automated check found; confirm.
- `useEffect` rule from desktop/CLAUDE.md not lint-enforced; CoreSdkApp itself uses useEffect with justification.
- Vite daemon URL env var for `pnpm --filter @pithyx/desktop dev` (daemonUrl in vite.config.ts top) not read; needed for a "run the desktop" section.
- Admin-only app visibility enforced by daemon too ("the daemon refuses everyone else"): exact grant model of `granted` apps (CoreApps.Granted) not read.
- Does the daemon validate `accent`/`wallpaper` values for `PUT /api/desktop/appearance`? Not read.
- Pnpm script to run only ui contrast test, and full list of root scripts for developers: root package.json scripts partially read (lines 12-28).
- reducers.ts lines 96+ (topics after system/processes) not fully listed; topic list may be incomplete. Generated topic catalog is in @pithyx/api-client (TopicSnapshots).
- Where website docs should link: the API/WS protocol reference (generated?) not located.
- bundle-budget baseline current vs actual build size: not measured (no builds allowed).
