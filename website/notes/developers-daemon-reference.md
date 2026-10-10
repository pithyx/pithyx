# Notes: developers/daemon, reference, project pages

Paths relative to repo root /home/user/pithyx. D = TODO.md decision. DP = daemon/src/Pithyx.Daemon.

## developers/daemon/architecture.md

### Overview

- Modular monolith; modules are folders in DP (Apps, Auth, Backup, Commands, Database, Errors, FileServices, Files, Health, Hosting, Installer, Jobs, Layouts, Localization, Logs, Monitoring, Network, Notifications, Permissions, Realtime, Shares, Storage, SystemSettings, Users). daemon/CLAUDE.md:7; DP/ listing.
- Solution: daemon/Pithyx.slnx; src DP/Pithyx.Daemon.csproj; tests daemon/tests/Pithyx.Daemon.Tests (xUnit, Microsoft.Testing.Platform per global.json); fixtures daemon/tests/fixtures.
- .NET SDK 10.0.401, rollForward latestFeature (global.json). Node 24 (.nvmrc), pnpm via corepack (package.json packageManager pnpm@12.6.0).
- One binary (`pithyxd`, image/rootfs/usr/lib/systemd/system/pithyx.service ExecStart=/usr/lib/pithyx/daemon/pithyxd) + one SQLite DB (WAL). daemon/CLAUDE.md:15.
- Startup: ASP.NET Core `WebApplication.CreateSlimBuilder`; Program.cs:34. Kestrel listens on 7484 (Program.cs:30,61); real mode + production build also on port 80 (Program.cs:56-58) where PlainHttpRedirect sends everything to 7484 except `/api/install-id` (DP/Hosting/PlainHttpRedirect.cs).
- Plain HTTP only today: Program.cs:202 "HTTPS on 443 arrives with the TLS work in M5"; README.md Development banner says the same. No UseHttps/443 in code (grep). CLAUDE.md "Network" (HTTPS 443, self-signed, trust wizard) = plan only.
- Middleware order (Program.cs:204-224): RequestIdMiddleware, PlainHttpRedirect, Serilog request logging, exception handler, status-code pages (API only), AppProxy + AppFrontendHosting (not in installer mode), CsrfMiddleware, WebSockets (KeepAlive 30 s, timeout 30 s), authentication, authorization.
- Every `/api` endpoint requires a session unless `AllowAnonymous` (Program.cs:233 `MapGroup("/api").RequireAuthorization()`). Policies: `admin`, `power`, `appToken` (DP/Auth/Policies.cs).
- Desktop static files served by daemon from `PITHYX_DESKTOP_DIR` or /usr/lib/pithyx/desktop (DP/Hosting/DaemonPaths.cs); SPA fallback.
- Installer mode: env `PITHYX_INSTALLER=1` or kernel cmdline pithyx.installer (DP/Installer/SystemInstaller.cs:32); then only installer + setup endpoints are mapped (Program.cs:238-243).
- Modes: `DaemonMode.Real` / `Simulated` (DP/DaemonMode.cs); simulated iff `PITHYX_SIMULATE=1` (Program.cs:37).
- Exit codes (DP/DaemonExitCode.cs): 1 DatabaseFailed; 65 DatabaseNewerThanDaemon; 75 RestartRequested (after config restore, systemd restarts); 78 SecondInstallation. Unit: `RestartPreventExitStatus=65 78`, `Restart=on-failure`, `RestartSec=2`, StartLimit 5 in 120 s, `OOMScoreAdjust=-900` (pithyx.service).
- Apps module layout: daemon/CLAUDE.md:22 (Manifest, Compose, Proxy, Docker, Catalog, AppTokens, AppFrontendHosting, CoreApps). Per-app uid 40000-49999 (DP/Users/UidAllocator.cs:28 MinAppUid); users 20000-29999, groups 30000-39999 (migration 0013 comment, D103).
- Docker via Engine API on unix socket, `docker compose up --wait` as job via ICommandRunner (daemon/CLAUDE.md:21). Not verified in detail here.
- In-process: SystemStateService (DP/Storage/SystemStateService.cs), Channels, BackgroundService. `HybridCache`: named in daemon/CLAUDE.md:34 but NOT used anywhere in DP (grep empty) -> do not document.
- Realtime: raw WebSocket, no SignalR/MQTT (see api page).
- Hosted services only when not installer: JournalReader, ThumbnailCacheProvisioner, LinuxAccountSync, FileServiceSync (real mode), FtpServer (Program.cs:131-152).
- Not in code (plan only): `Restart` watchdog + `sd_notify` heartbeats (daemon/CLAUDE.md "Operations") -> no sd_notify/UseSystemd/WatchdogSec in DP or in the unit (grep). VMs (QEMU, "Virtual machines" section) -> no VM module in DP. Power profiles, spindown: not checked.

### Command runner

- Interface `ICommandRunner`: `RunAsync(CommandRequest, ct) -> CommandResult`, `StreamLinesAsync(request, ct) -> IAsyncEnumerable<string>` (long-lived, request timeout does not apply, cancel kills). DP/Commands/ICommandRunner.cs.
- `CommandRequest(FileName, Arguments, Timeout?, SensitiveArgumentIndexes?, StandardInput?)`; no shell, args via ArgumentList; stdin written then closed, never logged; sensitive arg indexes masked as `***` in logs (CommandRequest.cs).
- `ProcessCommandRunner`: default timeout 1 minute (`DefaultTimeout`, ProcessCommandRunner.cs:9); timeout kills whole process tree and throws TimeoutException; logs "Command {Command} {Arguments} exited with {ExitCode} after {DurationMs} ms. stdout/stderr" at Information; output truncated to 4096 chars + "..." (MaxLoggedOutputLength, :7).
- Always sets `LC_ALL=C.UTF-8` (ProcessCommandRunner.cs:111) and `TRACEPARENT` env var from the current Activity (:112-115) -> external command correlates to request id.
- Simulation: `FixtureCommandRunner.Load(<fixtures>/commands/<scenario>)`; scenario = folder with `<id>.json`, `<id>.stdout`, optional `<id>.stderr`, `<id>.stream` for long-lived (FixtureCommandRunner.cs:6-11). Error text on missing fixture: "No simulation fixture for: ..." (README).
- Record fixtures: `tools/fixtures/record.sh <scenario> <id> -- <command> [args...]`, anonymizes serials/WWNs (tools/fixtures/anonymize.ts; TODO 3.5).
- Parsers are pure static functions with fixture tests (daemon/CLAUDE.md:8-9), e.g. LsblkParser, ZfsDatasetParser, ZfsSnapshotParser (DP/Storage). 32 files reference ICommandRunner.
- Rule: "No read request ever spawns a process" (daemon/CLAUDE.md:25) = convention, not enforced in code that I saw.

### Storage backend

- `IStorageBackend`: `ReadAsync(ct) -> PoolList`, `WatchAsync(ct) -> IAsyncEnumerable<string>` (yields once per change). DP/Storage/IStorageBackend.cs. Only implementation registered: `ZfsBackend` (Program.cs:112; DP/Storage/Zfs/ZfsBackend.cs). Btrfs/mdadm backends: none in code.
- Other storage pieces: DiskInventory, PoolInventory, StorageUsage, DiskProbe, StorageFacts, DatasetPlanner, PoolImportScanner, StoragePlanner, CommandPlanStore, CommandPlanRunner, SystemStateService (Program.cs:110-121). Plans: ZfsPlans, ZfsDatasetPlans, SnapshotPlans, ZfsScrubPlans, ImportPlans.
- DiskGuard (DP/Storage/DiskGuard.cs) = refusal of disks with data; `/etc/pithyx/test-disks` allowed serials only on dev (DP/Hosting/DeploymentEnvironment.cs). Product rule: destructive storage actions need disk serial confirmation (CLAUDE.md; i18n/en/storage.json `needsSerialConfirmation`, `no_serial`).
- Topics: `storage/disks`, `storage/pools`, `storage/alerts`.

### Database and migrations

- SQLite via Dapper, explicit SQL; `SqliteConnectionFactory`; DB path `PITHYX_DB` else /var/lib/pithyx/pithyx.db (prod) or `.pithyx-dev/pithyx.db` (DP/Hosting/DaemonPaths.cs).
- `MigrationRunner` (DP/Database/MigrationRunner.cs): PRAGMA journal_mode=WAL; table `schema_version(version, name, applied_at)` STRICT; each migration in own transaction; failure -> exit 1.
- Before migrating a non-empty DB: `VACUUM INTO "<db>.pre-<NNNN>-<yyyyMMddTHHmmssZ>.bak"` (:48-52), NNNN = first pending version.
- DB has versions unknown to daemon -> `DatabaseNewerThanDaemonException` -> exit 65, log Critical "...a newer daemon wrote it. Start that daemon, or point PITHYX_DB at another database".
- Migrations embedded from DP/Database/Migrations/NNNN_name.sql (Migration.Embedded()). 26 today:
  - 0001 users_sessions; 0002 desktop_layouts; 0003 desktop_layout_version; 0004 desktop_items_grid; 0005 shares; 0006 jobs (never resumed, finished jobs deleted after 7 days); 0007 trash; 0008 snapshots; 0009 storage_health (mutes only); 0010 storage_app; 0011 share_datasets; 0012 notifications (max 200/user, 30 days); 0013 linux_accounts (uid ranges); 0014 appearance_region; 0015 restore_points (daily, keep 7); 0016 user_disabled_share_acls; 0017 file_services; 0018 reapply_share_acls; 0019 groups; 0020 grants; 0021 webdav_ftp; 0022 system_app_ids (org.pithyx.\*); 0023 apps; 0024 app_storage; 0025 store; 0026 network.
- uid/gid never reused (migration 0013, UidAllocator).
- Backup: config backup (DP/Backup) covers pithyx.db, Samba passdb, TLS certs, SSH host keys (daemon/CLAUDE.md:42); new tables must belong to a backup part or be runtime state (`BackupDatabase`). Endpoints /api/system/backup, /inspect, /restore.
- Logs live in separate `logs.db` (DP/Logs/LogStore.cs:13), schema version 1 (LogDatabase.cs:15).

### Paths on the box

- Table in daemon/CLAUDE.md:44-61 (copy). Code-confirmed: /var/lib/pithyx/pithyx.db, /var/log/pithyx (DaemonPaths), /usr/lib/pithyx/desktop, /var/cache/pithyx (`PITHYX_CACHE_DIR`), /etc/pithyx/{environment,test-disks,test-nics} (DeploymentEnvironment), /var/lib/pithyx/tls (Program.cs:79 `tls`), /var/lib/pithyx/catalogs (Program.cs:92), /run/pithyx/backup (Program.cs:131), app pool paths `/<pool>/.pithyx/...` (AppStorageOptions FolderRoot "/").
- Env overrides: PITHYX_DB, PITHYX_LOG_DIR, PITHYX_DESKTOP_DIR, PITHYX_CACHE_DIR (DaemonPaths.cs).
- `/etc/pithyx/environment` = `dev` -> dev; anything else/missing = prod (DeploymentEnvironment.Load).
- `/etc/pithyx/update-source.json`: D338 plan; not checked in DP.
- Check if `/run/pithyx` API socket exists: unknown (daemon/CLAUDE.md says "API socket"; Kestrel only ListenAnyIP in Program.cs).

### Questions developers will ask

- Where is the database? /var/lib/pithyx/pithyx.db; override PITHYX_DB. (DaemonPaths.cs)
- Why does the daemon exit with 65? DB written by newer daemon; fix: run newer build or other DB. (MigrationRunner.cs, DaemonExitCode.cs)
- Why exit 78? Started from checkout without PITHYX_DB on a machine that has /etc/pithyx/environment. (DaemonPaths.IsSecondInstallation)
- How are migrations named/applied? NNNN_name.sql, transactional, backup .bak first.
- Can I call zfs directly? No, only through ICommandRunner. (daemon/CLAUDE.md:8)
- Which port? 7484 fixed (+80 redirect on a box). PITHYX_DEV_PORT only in Development builds (Program.cs:50-53).
- Is there an ORM? No: Dapper + SQL files.
- Does the daemon run as root? Yes, "only privileged process" (CLAUDE.md); desktop never elevated.

## developers/daemon/api.md

### REST

- OpenAPI doc: packages/api-client/openapi.json, "Pithyx API" version "v1", no servers, no securitySchemes (security: 0 operations). Served at `GET /api/openapi.json` anonymous (DP/Hosting/OpenApiSetup.cs:14; Program.cs:228). Scalar UI `/api/docs` in Development only (Program.cs:229-231).
- Counts (openapi.json): 128 paths, 160 operations (GET 59, POST 57, PUT 26, PATCH 6, DELETE 12), 240 component schemas, 270 ErrorCode enum values.
- Tags and operation counts: App API 15, Apps 17, Auth 8, Desktop 10, FileServices 2, Files 15, Installer 8, Jobs 3, Logs 6, Notifications 5, Permissions 5, Setup 2, Shares 9, Storage 19, Store 12, System 15, Users 9.
- Path prefixes (count): /api/app 12, /api/apps 14, /api/auth 8, /api/desktop 8, /api/file-services 1, /api/files 14, /api/groups 3, /api/health 1, /api/install-id 1, /api/installer 6, /api/jobs 3, /api/logs 4, /api/notifications 4, /api/permissions 1, /api/setup 1, /api/shares 7, /api/storage 14, /api/store 11, /api/system 11, /api/users 4.
- Not in OpenAPI: `/ws` (ExcludeFromDescription, WebSocketEndpoint.cs:25), `/webdav/` (FileServices/WebDav.cs:130), `/apps/<id>/...` frontend+proxy (Apps/Proxy, AppFrontendHosting), desktop static files. WS messages are added to the document as component schemas (OpenApiSetup.cs:122).
- Anonymous endpoints: GET /api/health -> `{status:"ok", mode}` (Program.cs:238... HealthResponse), GET/POST setup, POST /api/auth/login, /login/totp, /recover, /logout, GET /api/install-id, /api/openapi.json.
- Auth endpoints (AuthEndpoints.cs:27-42): POST /api/setup; POST /api/auth/login, /login/totp, /recover, /logout; GET /api/auth/session; POST /api/auth/totp/enroll, /totp/confirm, /totp/disable; GET /api/setup.
- JSON: camelCase string enums, strict numbers, RespectNullableAnnotations (Program.cs:63-70). All properties always serialized; nullable as null (OpenApiSetup.cs:23 comment).
- Optimistic concurrency via `If-Match` for layouts/desktop items (migration 0003/0004 comments). Details unverified.
- Daemon API for apps: `/api/app/**` (host token, policy `appToken`), tags "App API"; `POST /api/apps/{appId}/tokens` (cookie + CSRF). (Policies.cs; CHANGELOG A11)
- Error responses: see errors page. 429 `auth.rate.limited` sends `Retry-After` header from `params.retryAfterSeconds` (ErrorHandling.cs).
- Pagination/filters: not surveyed; logs GET /api/logs default limit 200 (LogService.DefaultLimit).
- Realtime throttle: stats topics 1 Hz (ResourceMonitor.Interval 1 s; SystemInfoPublisher 1 s).
- Request header: X-Request-Id returned on every response (RequestIdMiddleware.HeaderName); `Server-Timing` header (Hosting/ServerTiming.cs).

### Authentication and CSRF

- Session cookie name `pithyx_session` (SessionService.CookieName). Flags: HttpOnly, SameSite=Strict, Path=/, IsEssential; `Secure` only when request is HTTPS (AuthEndpoints.cs:171; comment: plain HTTP by IP must work).
- Cookie value = random token; DB stores SHA-256 (`Secrets.Sha256`, SessionService.DeleteAsync). Session id exposed = hex SHA-256 of token (IdOf).
- Lifetimes: idle (sliding) 7 days (`IdleLifetime`), absolute 30 days (`AbsoluteLifetime`); "touch" at most once per minute (`_touchInterval`). SessionService.cs:14-16. Open WebSocket keeps session alive; checked hourly (WebSocketEndpoint `_sessionCheckInterval`).
- CSRF: every non-GET/HEAD/OPTIONS request under /api must carry header `X-Pithyx-Request: 1`, else `auth.csrf.header_missing` (CsrfMiddleware.cs). No CORS approved. Client constant `CSRF_HEADER` in packages/api-client/src/index.ts:17.
- Unauthenticated/expired -> 401 `auth.session.expired` (SessionAuthenticationHandler.cs:66).
- Login flow: POST /api/auth/login -> user or TOTP challenge (challenge lifetime 5 min, LoginChallenges.cs:11) -> POST /api/auth/login/totp with TOTP code or recovery code. 10 recovery codes of 10 characters (AuthService.cs:17-18). Recovery key: prefix `PTHX`, 26 characters (AuthService.cs:15-16), POST /api/auth/recover.
- Rate limit (LoginRateLimiter.cs): only FAILED password/TOTP/recovery attempts count; 5 per (client address + user name), 30 per client address, fixed 5-minute window; error `auth.rate.limited` with `params.retryAfterSeconds` + Retry-After header. Also used by file protocols.
- Passwords: Argon2id, 64 MiB memory, 3 passes, parallelism 1, 16-byte salt, 32-byte hash (PasswordHasher.cs:20-25). Min length 8 (CredentialRules.MinPasswordLength), max 1024; common-password list refused (`auth.password.too_common`); user names `^[a-z_][a-z0-9_-]{0,31}$`, reserved names list; display name max 64.
- App tokens (not cookie): host and backend tokens 5 min, one-time WS tickets 30 s (CLAUDE.md Apps; Apps/AppTokens.cs); scheme in Apps/AppTokenAuthenticationHandler. Backend tokens only for proxy (bearer or `?pithyx-token=`), tickets `?pithyx-ticket=`.
- Roles: `admin` role; claims `pithyx:right` for special rights/protocols/apps (Policies.RightClaim). Special rights: right:admin, right:desktopLogin, right:sshLogin, right:power (Permissions/Grant.cs:34-37).
- Not in code: passkeys (TODO 3.3: out of scope until TLS decision).

### WebSocket and topics

- Endpoint `GET /ws` (WebSocketEndpoint.Path), authenticated by session cookie; one socket per client. Non-WS request -> `realtime.upgrade.required`.
- Origin check: browser Origin must match host, else close 4403 (IsSameOrigin, :47,127); non-browser clients without Origin accepted. Rejections are sent as close codes after the handshake.
- Max client message 16 KB (`MaxMessageBytes`, :18) else close 1009. Per-connection outbox capacity 256 (TopicHub.OutboxCapacity :18); overflow -> close 1008. KeepAlive ping 30 s.
- Close codes (Realtime/WebSocketCloseCode.cs): 1000 NormalClosure, 1001 DaemonRestart (reconnect), 1008 ClientTooSlow (reconnect), 1009 MessageTooBig (reconnect), 4401 SessionEnded (go to login, no reconnect), 4403 Forbidden (no reconnect). Client mapping `closeActions` in packages/api-client/src/realtime.ts: reconnect | login | stop | none; unknown -> reconnect.
- Client messages `ClientMessage(type, id, topic)`: `sub`, `unsub`. Server: `ack`(id), `snap`(id, topic, data), `event`(topic, data), `error`(id, code, params); all carry `requestId` (Messages.cs). On sub: snapshot first then changes only. Error codes: realtime.message.invalid, realtime.topic.invalid, realtime.topic.unknown, realtime.upgrade.required.
- Wildcards `+` (one level) and `#` (rest) (TopicPattern.cs); patterns kept per connection and matched on publish; permission checked for every snapshot and event (TopicDefinition.IsAllowed). Throttle: `MinInterval` per topic, newer values replace unsent ones. `ExactOnly` topics never match wildcards. `ForUser` filters per user.
- Each client message gets own request id/Activity "ws.message" linked to connection (WebSocketEndpoint.cs:203).
- Topics today (openapi TopicSnapshots; constants): `system/info` (1 Hz), `system/resources` (1 Hz), `system/processes` (1 Hz), `system/network`, `storage/disks`, `storage/pools`, `storage/alerts`, `shares/list`, `jobs`, `jobs/+`, `notifications`, `session/permissions`, `files/+/+` (folder watch), `thumbnails/+/+`, `apps`, `apps/admin`, `apps/+/context` (per user; window lease), `store/admin`. 18 patterns.
- Window lease: topic `apps/<id>/context`; 2-minute grace on disconnect (CLAUDE.md; AppLeases in Apps/AppContexts.cs:137 - grace value not verified).
- Not verified: layout/desktop topics (none in list).

### Generated client

- Package `@pithyx/api-client` (private, version 0.0.0, packages/api-client/package.json); deps openapi-fetch ^0.17.0, openapi-typescript ^7.13.0.
- Daemon build writes openapi.json: csproj `OpenApiDocumentsDirectory` -> packages/api-client, `--file-name openapi` (Pithyx.Daemon.csproj:5-6); build runs the app as `GetDocument.Insider` and skips side effects (`BuildTimeDocument.IsGenerating`).
- `pnpm generate` = `dotnet build daemon/Pithyx.slnx && pnpm --filter @pithyx/api-client generate && pnpm --filter @pithyx/sdk generate` (root package.json). Script: packages/api-client/scripts/generate.ts writes src/generated/{schema.ts, error-codes.ts, close-codes.ts}.
- Exports: createClient wrapper with CSRF middleware, `PithyxApiError(status, code, params, requestId)`, `errorCodes`, `ErrorCode`, `closeCodes`, `closeAction(s)`, `TopicName`, `ClientMessage/ServerMessage/TopicSnapshots/TopicEvents` types (src/index.ts).
- Drift check: `pnpm check:repo` (tools/checks/repo-check.ts) regenerates and fails if stale; CONTRIBUTING + README say the same. Never edit generated files; on merge conflicts take one side and regenerate (CLAUDE.md).
- Is the client published? No: private. @pithyx/sdk is the public package.

### Stability

- CLAUDE.md "Misc": stable contracts (breaking = major bump): REST/WS API, SDK API, manifest schema, extension points, error codes. ErrorCodes.cs:8-10 says never rename/remove a code without major version.
- Product is pre-alpha, no release yet (README Status, SECURITY.md). openapi version string "v1" fixed; no `/api/v1` prefix in paths.
- No deprecation policy written down in code (unknown).

### Questions developers will ask

- How do I call the API from a script? Sign in via POST /api/auth/login, keep cookie `pithyx_session`, send `X-Pithyx-Request: 1` on writes. Wait: login itself is POST so needs the header too (CsrfMiddleware applies to all non-safe methods under /api, including anonymous ones).
- Is there an API token for scripts? Only app tokens (apps); no personal access tokens in code (unknown/none found).
- Where is the API reference at runtime? /api/openapi.json (all builds); /api/docs only in Development.
- WebSocket from curl/Node? Works without Origin; needs session cookie.
- Rate limits other than login? None found.
- Are timestamps UTC? Yes: ISO 8601 UTC text (migrations; CLAUDE.md Misc).

## developers/daemon/errors-and-logging.md

### Error codes

- Format `area.object.reason`, lowercase, dots, underscores inside words (CLAUDE.md). Constants in DP/Errors/ErrorCodes.cs (270 `public const string`), each with `[ErrorStatus(HttpStatusCode.X)]`; `ErrorCodes.All` built by reflection; `StatusOf(code)` throws for unknown (ErrorCodes.cs:1231).
- Translations: i18n/en/errors.json + i18n/de/errors.json = 278 codes each = 270 daemon + 8 `sdk.*` (SDK transport codes, `sdkErrorCodes` in sdk/src/errors.ts). Areas (en): storage 57, app 49, fs 34, network 26, auth 19, store 18, system 14, users 14, installer 11, desktop 9, sdk 8, logs 5, realtime 4, request 4, fileservices 2, notifications 2, jobs 1, permissions 1.
- TS union `ErrorCode` generated from `ErrorCode` schema enum (270) into packages/api-client/src/generated/error-codes.ts.
- Test enforcing C# <-> JSON parity: probably under daemon/tests/.../Errors (not opened) -> unknown.
- Framework fallbacks (ProblemMapping.cs): 400 -> request.body.invalid, 404 -> request.route.not_found, 405 -> request.method.not_allowed, 415 -> request.media_type.unsupported, 416 -> fs.range.not_satisfiable, other -> system.internal.unexpected. Validation problems -> `request.body.invalid` with `params.fields` (sorted names).
- Throw `new PithyxException(code, params?, inner?)` (Errors/PithyxException.cs). Auth-handler/middleware: `WriteProblemAsync(context, code)`.
- Examples with status: auth.csrf.header_missing, auth.rate.limited (429 + Retry-After), auth.session.expired (401), realtime.upgrade.required, system.internal.unexpected (500), app.backend.unavailable (503, CHANGELOG).

### Problem details

- RFC 9457 `application/problem+json` (OpenApiSetup.ProblemMediaType). Body `ApiProblem(type, title, status, detail, code, params, requestId)` (Errors/ApiProblem.cs). `type` defaults "about:blank". `detail` = English text from embedded i18n/en/errors.json with ICU params (ErrorMessages.Detail; invariant culture); falls back to the code string. `traceId` extension removed.
- Client rule: API never returns translated text; clients translate `code` with `params` (CLAUDE.md). German informal "du", ICU plurals.
- Unknown code in response -> replaced by system.internal.unexpected (ErrorHandling.cs Customize).
- Unexpected exceptions logged Error "Unexpected error in {Method} {Path}"; expected ones Information "{Method} {Path} failed with {ErrorCode}".
- Only `/api` paths answer bare status codes with problem details (Program.cs:215).

### Request ids

- Request id = W3C trace id (32 hex chars) of current Activity (Tracing.CurrentRequestId). Every request, WS message ("ws.message"), job ("job.run", JobService.cs:258) and background refresh (storage.\*) has an Activity; `Tracing.SourceName = "Pithyx.Daemon"`, sampling AllData, DefaultIdFormat W3C forced (Hosting/Tracing.cs).
- Returned in response header `X-Request-Id` (RequestIdMiddleware) and problem body `requestId`; ASP.NET Core accepts incoming `traceparent` (default behavior; not specifically tested here -> verify before claiming).
- Passed to external commands as `TRACEPARENT` env; to proxied app backends as `traceparent` header (CHANGELOG A13).
- UI never shows the request id; it goes to console and logs (CLAUDE.md). Support: `grep <request id> .pithyx-dev/logs/daemon.json` (README).
- `Server-Timing` header emitted from activity (ServerTiming.cs; details unread).

### Structured logs

- Serilog, compact JSON with rendered message (`RenderedCompactJsonFormatter`) to: console (journald in prod), file `daemon.json` in log dir (20 MiB per file, `rollOnFileSizeLimit`, 10 files retained), and Log Center store sink (Hosting/LoggingSetup.cs:7-8,18-36).
- Levels: Information default; Microsoft.AspNetCore Warning; Yarp Warning; Microsoft.Hosting.Lifetime Information.
- Log dir: `PITHYX_LOG_DIR` else /var/log/pithyx (prod) or .pithyx-dev/logs. Property `RequestId` set by RequestIdEnricher.
- Log Center (DP/Logs): `logs.db` in log dir, entries queued in memory (20 000 capacity, LogStore.QueueCapacity) and written in batches: flush every 1 s or 1000 entries (LogWriter.FlushInterval, MaxBatch); maintenance every 10 min. Sources: `daemon`, `system/<unit>`, `app/<id>` (LogSources). System journal read by one JournalReader (journalctl follow); container output via journald label com.pithyx.app.
- Settings defaults (LogSettings.Default, LogContracts.cs:57): retention 30 days (1-365), max size 4 GiB (64 MiB-4 GiB), archive off, archive retention 365 days (1-3650), archive max 10 GiB (256 MiB-1 TiB). Archive copy daily (LogArchive.SyncInterval) to `<pool>/.pithyx/system/logs/archive.db` (RelativePath "logs/archive.db").
- Endpoints: GET /api/logs, /api/logs/sources, /api/logs/export, GET/PUT /api/logs/settings (admin), POST /api/apps/{app}/logs (core apps only). App log limiter: burst 200, 20 per second, report every minute (AppLogLimiter.cs:11-13).
- App log levels: info/warn/error always sent; debug only while admin switched on (D305; Store).
- Command logs: see command runner section.

### Secrets

- `SecretMaskingEnricher` (Hosting): property NAMES matching regex `password|passphrase|secret|token|cookie|authorization|recovery_?key|totp` (case-insens.) are replaced by `***` at any nesting depth; text values containing `pithyx-token=`, `pithyx-ticket=` or `bearer <x>` get the credential replaced by `***`.
- Command args masked via `SensitiveArgumentIndexes`; stdin never logged.
- Passwords: Argon2id; sessions stored as SHA-256; TOTP/recovery details in Auth.
- Support bundle with logs/config/system info without secrets, memory dumps opt-in: CLAUDE.md rule; support bundle code NOT searched (unknown if exists).
- App tokens/tickets masked in logs (CHANGELOG A11).

### Questions developers will ask

- Add a new error code? Add const + [ErrorStatus] in ErrorCodes.cs, text in i18n/en and de errors.json, run `pnpm generate`. (CONTRIBUTING checklist, README)
- Where are logs? journald, /var/log/pithyx/daemon.json (rolling), logs.db (Log Center UI).
- How to find everything for one failed request? Take `requestId` from the problem body (or X-Request-Id) and search logs.
- Is request id a UUID? No, 32-hex W3C trace id.
- Are logs sent anywhere? No (principle: 100 percent local; CLAUDE.md).

## developers/daemon/development.md

### Building

- Prereqs: .NET SDK 10.0.401+ (global.json), Node 24 (.nvmrc; package.json engines >=24.21.0), pnpm via `corepack enable`, git (README Prerequisites).
- `pnpm install` (also installs lefthook git hooks via `prepare`). `dotnet build daemon/Pithyx.slnx`. `pnpm build` = `pnpm -r build`. `pnpm generate` regenerates clients (builds daemon first).
- Scripts (package.json): dev, design, design:shots, design:test, lint, typecheck, test, e2e, build, docs:api, format, format:check, generate, check:repo, check:bundle, check:docs, check:commits, check:dco.
- Hooks (lefthook.yml): pre-commit prettier, eslint, repo-check, shellcheck, dotnet-format whitespace; commit-msg `tools/checks/commit-msg.ts`; pre-push refuses `master` and `dev`.
- CI (.github/workflows/ci.yml): jobs changes, repo, daemon, frontend, e2e (label `e2e`), website. Daemon job: `dotnet test --solution daemon/Pithyx.slnx --configuration Release --no-build --filter-not-trait Category=StorageIntegration|DockerIntegration|NetworkIntegration|TimingBudget` (ci.yml:75-76); TimingBudget runs in its own step (ci.yml:185-186).
- Desktop dev server: `pnpm dev` -> http://localhost:5173, proxies /api and /ws to daemon; `PITHYX_DAEMON_URL` overrides http://localhost:7484 (README).

### Simulation mode

- Start: `PITHYX_SIMULATE=1 dotnet watch --project daemon/src/Pithyx.Daemon` (README). Any Linux incl. WSL; native Windows/macOS unsupported.
- Env vars (all read in DP): PITHYX_SIMULATE (=1), PITHYX_FIXTURES (default daemon/tests/fixtures), PITHYX_SIMULATE_SCENARIO (default "default"), PITHYX_ETC_DIR (fake /etc/pithyx, default <fixtures>/etc), PITHYX_DB, PITHYX_LOG_DIR, PITHYX_CACHE_DIR, PITHYX_DESKTOP_DIR, PITHYX_DEV_PORT (Development build only), PITHYX_FTP_PORT (FTP port; 21 real, 0 simulated), PITHYX_INSTALLER=1.
- Simulation: fake accounts files from <fixtures>/etc/{passwd,shadow,group}, no Apply; app storage in `<db dir>/pools`; Docker not applied; file catalogs allowed (`AllowFile`); seeded shares in `<db dir>/shares` (ShareService.SeedSimulationAsync).
- State dir `.pithyx-dev/` (git-ignored) holds db, logs, cache; delete it (daemon stopped) to start over. First run: desktop asks to create admin and shows recovery key once.
- Check `/etc/pithyx/environment`: a Pithyx machine (file exists) + no PITHYX_DB + dev/sim -> exit 78 (DaemonPaths.IsSecondInstallation). Contributor tip: set PITHYX_DB when your machine has Pithyx installed.
- Also `Dev` builds only: Scalar docs at /api/docs.

### Tests

- C# tests: daemon/tests/Pithyx.Daemon.Tests (folders mirror modules: Apps, Auth, Backup, Commands, Database, Errors, FileServices, Files, Hosting, Installer, Jobs, Layouts, Localization, Logs, Monitoring, Network, Notifications, Permissions, Realtime, Shares, Storage, SystemSettings, Users, Support).
- `DaemonFactory` (Support/DaemonFactory.cs): WebApplicationFactory<Program>, in-memory daemon, temp data dir, sets PITHYX_SIMULATE=1 + PITHYX_LOG_DIR/DB/CACHE_DIR, `FakeTimeProvider Time` (starts now, moves only when advanced), `CreateApiClient()` adds `X-Pithyx-Request: 1`.
- Other support: ScriptedCommandRunner, ListLogger, FtpClient, TestUsers, RepositoryFiles.
- What to test (CLAUDE.md): parsers, decision logic, permission checks, manifest validation, update state machine; not getters/DTOs/pass-through controllers/React components.
- Shared corpora: sdk/schema/fixtures/ (manifest, compose, catalog) run in Vitest and xUnit.
- Run: `dotnet test` in daemon/ (CONTRIBUTING); frontend `pnpm test`; E2E: `pnpm --filter @pithyx/e2e exec playwright install chromium`, then `pnpm build && pnpm e2e` (starts own daemon in simulation, fresh DB; env PITHYX_E2E_DATA, PITHYX_E2E_PORT; e2e/playwright.config.ts).
- Design tests: `pnpm design:test` (overflow at phone/tablet/laptop, en+de).
- Trait categories: StorageIntegration (Storage/Integration/FileVdevs.cs:17), DockerIntegration (Apps/Docker/DockerIntegrationTests.cs:32), NetworkIntegration (Network/NetworkIntegrationTests.cs:28), TimingBudget (Files/ListingBudgetTests.cs:95). Never in GitHub CI (except TimingBudget separate step).
- Warnings are errors (C# and TS); `dotnet format whitespace` in hook.

### Integration tests

- Contributor-facing: need a Debian 13 VM with ZFS, root, scsi_debug, Docker, networkd; README "Dev VM" (4 vCPU, 8 GB, UEFI, 32 GB system disk, six 10 GB data disks with serials VYXTEST01..06); `sudo tools/dev-vm/provision.sh` (idempotent; exit 2 = reboot and rerun; writes `dev` to /etc/pithyx/environment and test serials to /etc/pithyx/test-disks; installs ZFS DKMS, mdadm, btrfs-progs, smartmontools, Samba, NFS, Docker, QEMU; Secure Boot must be off).
- `tools/dev-vm/testpool.sh create|destroy|wipe` (refuses non-test disks): `testpool.sh create pithyxtest --layout mirror VYXTEST01 VYXTEST02`.
- `tools/dev-vm/integration.sh [--filter <xunit class>] [--disk-pulls] [--docker] [--network [--record]]` (usage header in script):
  - needs /etc/pithyx/environment = dev, run as normal user (sudo only for test run), builds Release first; stops pithyx-dev.service during run and restarts it.
  - default: StorageIntegration on sparse files in /dev/shm + scsi_debug RAM disks; never touches system disk or declared test disks.
  - `--disk-pulls`: also pulls scsi_debug disks from running pool; can wedge ZFS until reboot (OpenZFS 2.3.9), only on fresh boot (D270).
  - `--docker`: DockerIntegration, configures Docker with box's own files, restores after.
  - `--network`: NetworkIntegration on veth ptest0..2 in namespace pithyx-nettest with dnsmasq; needs /etc/pithyx/test-nics exactly ptest0 ptest1 ptest2; `--record` copies kernel/networkd files into daemon/tests/fixtures/network.
  - `--docker` and `--network` mutually exclusive.
- Other dev-vm scripts: install-dev-daemon.sh (`--yes`, `--no-build`, `--uninstall`), reset-dev-db.sh, seed-dev-accounts.sh, docker-spike.sh, disk-failure-test.sh, idle-check.sh, provision.sh, pithyx-dev.service.
- OWNER'S DEV BOX ONLY (do not document as contributor steps; internal): dev01 specifics (pithyx-dev.service on 7484, owner/claude/e2e accounts and ~gabriel/.config files, "never touch sda", pve02, runner server, install-dev-daemon deploy rules, wave/worktree workflow, merge/squash rules, owner-only npm publishing, image/build.sh timings, D-numbers). Root CLAUDE.md "Dev VM (dev01)", "Current state", "Known pitfalls" are internal.
- Contributor-safe from CLAUDE.md: `/etc/pithyx/environment` dev/prod; test serials; testpool wipe after `zpool destroy`; pitfalls: load spoils timings (check uptime), manual `workflow_dispatch` CI has no BASE_SHA.

### Questions developers will ask

- Do I need ZFS to hack on the daemon? No, simulation uses fixtures.
- "No simulation fixture for: ..."? Record with tools/fixtures/record.sh.
- Port 7484 busy? Stop other daemon (`ss -ltnp | grep 7484`) or use PITHYX_DEV_PORT in a Development build.
- Exit code 65/78 in dev? See architecture; delete `.pithyx-dev/` or set PITHYX_DB.
- Which tests run in CI? All except the four categories above.
- Can I run storage tests on my laptop? No (root, ZFS); only on a VM via integration.sh.
- Commit format / sign-off? See project/contributing.

## reference/api.md

- Endpoints/Schemas should be generated from packages/api-client/openapi.json; no generator exists in website/ (website/scripts has only check-build.ts; website/src/lib has bundles, docs, errorCodes, faqCategories, i18n, landingIcons, routeMiddleware, sections). No existing script turning OpenAPI into Markdown. Scalar UI exists only in Development builds.
- Numbers (see developers/daemon/api.md): 128 paths, 160 operations, 240 schemas, 17 tags, 18 WS topic patterns, 270 error codes, 6 close codes.
- Operation summaries/descriptions: openapi.json has `description` on many operations (e.g. snapshots endpoints "Administrators only. ..."); tags group them; `operationId` set via `.WithName` (e.g. GetHealth, Login, Setup).
- Generation idea (facts only): import openapi.json from `../../packages/api-client/openapi.json` in an Astro component like `website/src/lib/errorCodes.ts` imports i18n JSON; group by tag; Markdown twin via `toPlainMarkdown` in website/src/lib/docs.ts (check converts component output? unknown, errorCodesMarkdown() is the pattern used for errors).
- Not described in OpenAPI: /ws, /webdav, /apps proxy (list above) -> need hand-written section.
- Auth note for every endpoint: no security scheme in doc; default is cookie + `X-Pithyx-Request`.
- Open: whether /api/app/\*\* (App API, 15 ops) belongs in this public reference or in the SDK area.

## reference/errors.mdx

- Page exists: website/src/content/docs/docs/reference/errors.mdx (status draft, `generated: error-codes`), renders `<ErrorCodes />` (website/src/components/ErrorCodes.astro).
- Source: website/src/lib/errorCodes.ts imports i18n/en/errors.json and i18n/de/errors.json, flattens nested keys to dotted codes, sorts by code, groups by first segment (`errorAreas`), `errorCodesMarkdown(locale)` builds the Markdown twin.
- Count: 278 codes per locale = 270 daemon + 8 `sdk.*` (the page therefore lists SDK transport codes too). Areas: see errors-and-logging.
- Message placeholders `{name}` = ICU params.
- No HTTP status per code in the page (statuses only in ErrorCodes.cs attributes). Possible addition; unknown if wanted.

## reference/glossary.md (term: meaning; evidence)

- Box: a machine running Pithyx; i18n/en/settings.json `info.intro` "What runs on this box".
- Pithyx / pithyxd: product / daemon binary; pithyx.service.
- Daemon: only privileged process, C#; CLAUDE.md.
- Desktop: web desktop UI (React); runs unprivileged; CLAUDE.md.
- Administrator: user with every right (right:admin); Permissions/Grant.cs:34; users.json `adminHasAll`.
- Owner: the first administrator created at setup? Not defined in code; "owner" in repo = project owner/dev (do not use as product term). Open question.
- Recovery key: shown once at setup/installer, prefix `PTHX`, only way to reset admin password (auth.json `forgot.adminIntro`; AuthService.cs:15). Without it: reinstall.
- Recovery codes: 10 one-time TOTP backup codes, 10 chars each (AuthService.cs:17-18).
- TOTP / two-factor: optional per user; /api/auth/totp/\*.
- Session: cookie `pithyx_session`; idle 7 d, absolute 30 d.
- Special rights: admin, desktopLogin, sshLogin, power (restart/shutdown); Grant.cs:34-37; users.json `rights`.
- Grant: right of a subject (user/group/app) on a resource; union, no deny; migration 0020.
- Resource: `right:*`, `protocol:*`, `share:<id>`, `app:<id>` (Grant.cs).
- Group: Linux group, gid 30000-39999, never reused; built-in `users` (gid 100) holds everyone; users.json groups.intro; migration 0019.
- User: Linux account uid 20000-29999 (migration 0013).
- Protocol (file service): SMB, SFTP, WebDAV, FTP, per user + per box (Resources.Protocols; settings.json fileServices.\*). WebDAV at /webdav/; NFS in CLAUDE.md but not in Resources (open).
- Shared folder (share): own ZFS dataset `<pool>/pithyx/shares/<name>` with access none/read/read-write (migration 0011, settings.json shares.intro).
- Pool: ZFS pool of disks; storage.json.
- Vdev: group of disks inside a pool (mirror, RAID-Z1/2/3, stripe); storage.json lines 39-40,124-129.
- Dataset: ZFS file system with own space/quota/compression; storage.json advanced.datasets.explain.
- Restore point: automatic daily snapshot of a shared folder, kept 7 days, on by default (migration 0015; settings.json shares restorePoints).
- Snapshot: `pithyx-<kind>-<utc>` names, manual ones never pruned (OpenAPI descriptions).
- Scrub: reads every block, repairs from redundancy (storage.json health.scrub.intro).
- Resilver: rebuild onto replaced/returned disk (storage.json pools.degradedHint).
- Hot spare: waits in pool, takes over on failure (storage.json actions.spareAdd.explain).
- Quota (Limit): dataset space limit (storage.json:245-285).
- Recycle bin (trash): per share hidden `.pithyx-trash`, retention per share (migration 0007; files.json trash).
- Disk serial confirmation: needed for disks with data (storage.json `needsSerialConfirmation`).
- Command plan: reviewed list of storage commands before running (CommandPlan.cs); details unread.
- Job: background long operation with progress, topic `jobs/+`, not resumed after restart (migration 0006).
- Box job: job started by the box itself (updates), `jobs.user_id` nullable (D334).
- Topic: WebSocket subscription channel (api page).
- Snapshot (WS): initial full state `snap` message (not ZFS) - disambiguate.
- Request id: 32-hex trace id (errors page).
- Error code: `area.object.reason`.
- Log Center: Logs app; logs.db; sources daemon, system/\*, app/<id>.
- Archive (logs): optional copy on a pool, daily.
- Configuration backup: database, Samba passdb, TLS certs, SSH host keys; parts incl. "Apps", "users", "storage", "sharedFolders", "system" (settings.json backup.categories.\*).
- App: static frontend + optional backend containers, packaged `.pithyxapp` (CLAUDE.md Apps; D300/D301).
- App id: reversed domain + name, 3-5 parts; `org.pithyx.*` for system apps (AppId; D302).
- System (core) app: runs in-process, id org.pithyx.<name>, in the image (migration 0022).
- Sideload: app installed from a file, unverified (store.json source.sideload).
- App pool: pool chosen by admin at first install for app data, setting `apps.pool`; dataset `<pool>/pithyx/apps/<id>`; Docker data root on it (migration 0023; daemon/CLAUDE.md).
- Backend: Compose-based containers of an app, one instance for all users, behind proxy (CLAUDE.md).
- Lifecycle `always` / `onDemand`; idleTimeout default 10 min (CHANGELOG A15).
- Window lease: counting of open app windows through topic `apps/<id>/context`, 2 min grace (CLAUDE.md).
- Host token / backend token / ticket: app tokens (5 min / 5 min / 30 s one-time).
- Permission (app): manifest permission an admin can revoke (store.json permission.\*); elevated rights: root, `directory.read`, `docker.socket`/`host.full` refused in v0.1.
- Storage request (folder mapping): app asks for folders mapped into a shared folder (store.json detail.storage.intro).
- Catalog: Git repository of apps the Store reads; official/community/custom (migration 0025; D328).
- Channel: stable (`catalog` branch) / beta (`catalog-next`) (store.json settings.channel).
- Deploy key: SSH read-only key the box generates for a private catalog (store.json settings.catalogs.deployKeyHint).
- Update policy: automatic / notify / none (store.json updates.policies).
- Rollback: back to previous app version with its snapshot (D334).
- Sandbox: iframe `allow-scripts allow-forms` for third-party apps (CLAUDE.md).
- Simulation mode: daemon with fixtures (README).
- Live system / installer: ISO environment with the web installer (PITHYX_INSTALLER).
- Management card: skip (internal dev term).

## reference/manifest-schema.md

- Source: sdk/schema/manifest.v1.schema.json (469 lines), JSON Schema draft 2020-12, `$id` https://pithyx.org/schema/manifest.v1.schema.json, title AppManifest, additionalProperties set at top.
- Top-level required: manifestVersion, id, version, name, author, entry, sdk, permissions, window.
- Top-level properties (18): $schema, manifestVersion, id, version, name, description, author, license, homepage, entry, sdk, pithyx, dataVersion, permissions, window, storage, fileHandlers, backend.
- $defs (21): VersionRange, AppId, NameText, LongText, LocalizedName, LocalizedText, HttpUrl, AppAuthor, Permission, ElevatedPermission, WindowSize, WindowManifest, ServiceName, Name, StorageRequest, FileHandler, BackendEndpoint, BackendPath, BackendHealth, BackendSchedule, BackendManifest.
- Existing generator: sdk/scripts/generate-manifest.ts (`pnpm --filter @pithyx/sdk generate`, part of root `pnpm generate`) writes sdk/src/manifest.generated.ts TS types from the schema; no doc/Markdown generator in website/.
- Fields table could be generated by reading the JSON schema in an Astro component (same pattern as errorCodes.ts); descriptions come from `description` keys (check coverage: unknown).
- Related schemas in sdk/schema/: compose.v1.rules.json, images-lock.v1.schema.json, catalog.v1.schema.json, index.v1.schema.json, index-signature.v1.schema.json, maintainers.v1.schema.json, catalog.v1.rules.json, fixtures/. Validation in daemon: Apps/Manifest/ManifestValidator (JsonSchema.Net); CLI: `pithyx validate`.
- Error codes: app.manifest.\* (e.g. app.manifest.pithyx_invalid) in errors.json.
- `backend.resources` removed (CHANGELOG A6); `pithyx` range and `dataVersion` added (S1).
- Stability: manifest schema is a stable contract (major bump on break).

## project/changelog.md

- CHANGELOG.md (265 lines): Keep a Changelog 1.1.0 + SemVer; only an `[Unreleased]` section so far (Added/...); entries are long single bullets with D/step refs. No release yet.
- Rule: every user-visible or developer-relevant change adds a line under Unreleased (CLAUDE.md).
- Product SemVer tags `vX.Y.Z` on master with constellation codename (plan); npm packages own line, tag `sdk-vX.Y.Z`, currently 1.0.0-rc.x (CHANGELOG/CLAUDE.md).
- Question: embed CHANGELOG.md in the site (import at build) vs link to GitHub. Unknown.

## project/contributing.md (CONTRIBUTING.md, 58 lines)

- Branches: master stable (release PRs from dev + hotfixes), dev integration (beta builds), work in feature/ fix/ hotfix/<name>, PRs target dev, hotfixes from master merged into master and dev; pre-push hook refuses direct push to master/dev.
- Commits: `<gitmoji> <type>(<scope>): <subject>`, Unicode emoji, English, imperative, no em dashes; types feat fix docs style refactor perf test build ci chore revert; PR titles same; checked by commit-msg hook and CI.
- DCO: `git commit -s`, trailer matches author; CI rejects unsigned (tools/checks/dco.ts).
- Setup: README "Development"; `pnpm install` installs hooks.
- PR checklist (8 items): build/lint/tests (`dotnet test` in daemon/, `pnpm lint`, `typecheck`, `test`, `build`, `check:repo`), generated clients current, en+de i18n keys, error code translations, TODO.md + CHANGELOG, docs in website/ en+de (D339), no em dashes/non-English outside translations, sign-offs.
- Internal-only (not for public page): one PR run on runner at a time, squash subject <=100 chars, owner-only merges to master, worktrees, label `e2e` usage (CLAUDE.md Workflow/CI).
- Code style (CLAUDE.md): short English comments, warnings are errors, no abstraction without second implementation, no em dashes.
- Hmm: CONTRIBUTING says "Contributing... pre-alpha, expect things to move fast"; no code of conduct file found (ls root).

## project/license.md

- LICENSE = GNU AGPL v3 (19 November 2007). NOTICE: AGPL-3.0 + section 7(b) additional term: UI must keep attribution text in About page ("Based on Pithyx (https://github.com/pithyx/pithyx), created by the Pithyx contributors and licensed under the GNU AGPL-3.0."); modified versions must mark themselves as modified (5(a)). NOTICE ends "REVIEW REQUIRED: not checked by a lawyer yet. Must be reviewed before first public release."
- NOTICE says "The SDK in sdk/ is licensed separately under MIT (sdk/LICENSE)". LICENSE files also in packages/ui and packages/cli; package.json license MIT in sdk, packages/ui, packages/cli.
- desktop/public/third-party-notices.json: 8 entries (fonts IBM Plex Sans 1.1.0 OFL-1.1, Sora OFL-1.1, JetBrains Mono 2.304 OFL-1.1, Tabler Icons for React 3.48.0 MIT, ...); license texts in desktop/public/licenses/ (pdfjs-dist, react-aria-components, seclists, skiasharp, tabler-icons-react) and fonts/. About page shows notices (CLAUDE.md Misc). Daemon: common-passwords.LICENSE.txt in DP/Auth.
- Open: NOTICE mentions only sdk/ as MIT, not packages/ui and packages/cli (CLAUDE.md says all three MIT).

## project/privacy.md

- Principles (CLAUDE.md): 100 percent local: no telemetry, phone-home, cloud accounts, project-run services the box depends on; crash reports stay on box; user shares support bundle manually.
- Code-confirmed: .NET CLI telemetry opt-out in unit (`DOTNET_CLI_TELEMETRY_OPTOUT=1`, pithyx.service); logs local; website: no analytics/external fonts/CDN (website/CLAUDE.md).
- What the box does contact (facts to state honestly): catalogs by git (https/ssh, default GitHub `pithyx/apps-official`, daily check, D334), update channel (project GitHub releases by default, D338 plan; code not verified), Docker registry pulls for catalog apps, optional Let's Encrypt (plan), time servers. These are user-visible settings; confirm in code before claiming "no outbound".
- Fresh installs only, no import from other NAS.

## project/security.md (SECURITY.md, 11 lines)

- Report privately via GitHub security advisories: https://github.com/pithyx/pithyx/security/advisories/new; no public issues; include version/commit, repro steps, impact; "You will get an answer as soon as possible" (NO numeric response time); supported versions: none yet (pre-alpha).
- No PGP key, no security email, no disclosure timeline in file.
- Security-relevant design facts in code for the page: session cookie flags, CSRF header, login rate limits, Argon2id, secret masking, iframe sandbox + CSP headers (CLAUDE.md Apps), update signatures Ed25519 (D338).

## Not in code (plan only)

- HTTPS 443, self-signed + trust wizard, Let's Encrypt, passkeys, service worker (CLAUDE.md Network; Program.cs:202).
- sd_notify watchdog (daemon/CLAUDE.md Operations). HybridCache. QEMU/KVM VMs (daemon/CLAUDE.md "Virtual machines"). Power profiles/spindown (unchecked). 60 s network confirm IS in code (CHANGELOG 14.2, migration 0026).
- Pool encryption, key vault (step 9) - no i18n keys found for key vault/unlock.
- NFS (CLAUDE.md Users and security; Resources.Protocols has only smb, sftp, webdav, ftp).
- SSD cache (step 13), web installer/updates parts: D294-D298, D338 (build side exists in tools/release).
- Support bundle code: not located.
- D311 elevated rights (A16) reserved; `docker.socket`/`host.full` refused in v0.1.

## Open questions / contradictions

1. Password minimum: TODO.md 3.3 says 12 characters; code CredentialRules.MinPasswordLength = 8 (and comment says desktop warns but never refuses weak). Which is right for the docs? (code = 8)
2. TLS/443: CLAUDE.md "Network" states HTTPS on 443, port 80 redirects; code listens plain HTTP 7484 (+80 redirect to 7484). Docs must say HTTP on 7484 today.
3. watchdog/sd_notify and HybridCache claimed in daemon/CLAUDE.md, absent in code and unit.
4. Does the daemon honor an incoming `traceparent` (TODO 3.1 says "accept or create")? ASP.NET default does; no test read.
5. "API socket /run/pithyx/" in paths table: no Unix-socket listener found in Program.cs.
6. Is there a test asserting ErrorCodes <-> errors.json parity (270 vs 278 incl. 8 sdk.\*)? Not opened.
7. OpenAPI "System" tag has 15 ops but path count says /api/system 11; tags and prefixes differ (PATCH/PUT on /api/shares etc.). Counts are per-operation vs per-path; fine but verify when generating.
8. NOTICE lists only sdk/ as MIT; ui and cli are also MIT.
9. SECURITY.md has no response times and no supported-versions table beyond "none".
10. Public vs internal: root CLAUDE.md sections (dev01, owner, Claude accounts) are internal; confirm they must not appear on public docs.
11. Terms "owner" and "management card": not product terms; decide glossary scope.
12. Window lease grace (2 min) and app token lifetimes: taken from CLAUDE.md/CHANGELOG, constants not located in this pass.
13. Is /api/openapi.json anonymous in production builds (MapOpenApi AllowAnonymous at Program.cs:228, unconditional)? Yes per code; consider noting.
14. Rate limits: only login is limited in code; no general API rate limit found.
15. website: no OpenAPI/manifest doc generator exists; decide build-time Astro component vs checked-in generated Markdown.
