/**
 * The wire protocol between an app and the desktop. Both transports carry exactly these messages: the in-process
 * transport for core apps and a `MessagePort` for sandboxed third-party apps. Everything is structured-clone data;
 * functions, DOM nodes and class instances never cross.
 *
 * Handshake:
 * 1. The desktop creates a `MessageChannel` per window, keeps one port and posts the other to the app's iframe as
 *    `{ type: 'pithyx:connect' }` with the port transferred. The iframe is sandboxed without `allow-same-origin`, so
 *    the target origin is `*`; the port is what identifies the app from then on, never message data.
 * 2. The app sends `hello` with its protocol and SDK version on that port.
 * 3. The desktop answers `init` with the {@link AppContext}, or `refuse` with a problem (for example
 *    `sdk.protocol.unsupported`), and closes the port after a refusal.
 * 4. From then on the app sends `request`s and gets one `response` each; the desktop sends `event`s, and `query`s that
 *    the app answers with `reply` (used for `beforeClose` and `beforeUnload`).
 *
 * Every call that crosses the transport is asynchronous and fails with a problem (RFC 9457 fields `code`, `params`,
 * `detail`, `requestId`). Only local reads (the context, `supports`, the formatters) and listener registration are
 * synchronous.
 *
 * An app controls its own windows only: every `window.*` method acts on the window that called it. No method brings
 * a window to the front, focuses it or opens another one; the desktop does that only for a user action (a click in
 * the taskbar, a launch, "Open with"). A method added later that does so must require a user action in that window.
 */
import type { Problem } from './errors';
import type {
  ConflictPolicy,
  FileEntry,
  FileLocation,
  ShareLocation,
  FileOperation,
  FileVersion,
  FolderChange,
  FolderPage,
  JobInfo,
  ShareInfo,
  SortKey,
  SortOrder,
  TrashPage,
  UploadProgress,
} from './files';
import type { AppId } from './manifest';
import type { Theme } from './theme';

/** The size class of the app's window: compact below 600 px, medium up to 1023 px, expanded above (see the desktop). */
export type SizeClass = 'compact' | 'medium' | 'expanded';

/**
 * A file the user handed to the app (opened with it, or picked in a desktop dialog). The app never learns where the
 * file is: `ref` is an opaque handle that only the SDK file API accepts. It lasts as long as the window's connection:
 * closing the window, reloading the app or reloading the desktop ends it, and a stale handle fails with
 * `sdk.request.invalid`.
 */
export interface FileRef {
  /** Opaque handle; only the SDK file API accepts it. */
  ref: string;
  /** File name with extension, for display. */
  name: string;
  /** MIME type, when known. */
  type?: string;
  /** Size in bytes. */
  size: number;
  /** Last change, ISO 8601 UTC. */
  modified: string;
  /** Whether the app may write it (`files.write` and the user's rights on it). */
  writable: boolean;
}

/** Why and with what the app's window was opened, in the context and with `launch`. */
export interface LaunchArgs {
  /** App-defined arguments, for example a view to show. */
  args: unknown;
  /** A file to open, only of a type the manifest's `fileHandlers` declare. */
  file?: FileRef;
  /** The user clicked a notification of the app while no window of it was open (see `notificationAction`). */
  notification?: NotificationActivation;
  /** The user chose one of the manifest's `fileActions` for a selection in Files. */
  action?: FileActionLaunch;
}

/**
 * What a `fileActions` entry hands to the app: the selection as a share, a folder and names. The app reads and changes
 * them through the files API with the user's rights, which needs `files.shares`.
 */
export interface FileActionLaunch {
  /** The `id` of the action in the manifest. */
  id: string;
  /** The share the selection is in. */
  location: ShareLocation;
  /** The folder of the selection, relative to the share; `""` is its root. */
  folder: string;
  /** The selected files and folders in that folder, by name. */
  names: string[];
}

/** One of the user's groups. The built-in group every user belongs to is left out. */
export interface GroupRef {
  /** Stable id (the group's Linux gid). */
  id: number;
  /** The group's name. */
  name: string;
}

/**
 * The state of the app's backend: `stopped` (not running, for example an `onDemand` backend without windows),
 * `starting`, `running`, `updating` (a new version is being installed) or `unavailable` (it failed, or it cannot start
 * and `reason` says why). A request to a `stopped` or `starting` backend wakes it and waits until it runs (at most 30
 * seconds); while it is `updating` or `unavailable` the proxy answers 503 `app.backend.unavailable`.
 */
export type BackendStatus = 'stopped' | 'starting' | 'running' | 'updating' | 'unavailable';

/**
 * Why an `unavailable` backend cannot start until an administrator acts: `storage` (a required storage request has no
 * folder yet; `context.storage` shows it as `unmapped`), `pool` (the app pool is locked or missing) or `device` (a device
 * the app needs, declared under `backend.elevated` and not in `backend.optionalDevices`, is not plugged in; the backend
 * starts by itself once it is back).
 */
export type BackendUnavailableReason = 'storage' | 'pool' | 'device';

/** The app's backend, as the window sees it. */
export interface BackendInfo {
  /** Whether the backend runs; `backendStatus` follows it. */
  status: BackendStatus;
  /** Why the backend cannot start, only while it is `unavailable` for that reason; `backendStatus` follows it. */
  reason?: BackendUnavailableReason;
  /** The names of the endpoints the manifest declares under `backend.endpoints`; `api` is the default one. */
  endpoints: string[];
}

/**
 * The state of a storage the manifest requests: `ready` (mapped to a folder that can be reached), `unmapped` (no
 * administrator assigned a folder yet), `locked` (its pool is encrypted and locked) or `unavailable` (its pool or
 * folder is missing).
 */
export type StorageState = 'ready' | 'unmapped' | 'locked' | 'unavailable';

/** A storage request of the manifest and where it stands. */
export interface StorageInfo {
  /** The `id` of the request in the manifest; reach it as `{ storage: id }`. */
  id: string;
  /** Whether the storage can be used now. */
  state: StorageState;
  /** What the mapping allows (never more than the manifest requests), or null while it is unmapped. */
  access: 'read' | 'readWrite' | null;
}

/** Whether `debug` entries of `app.log` go to the daemon (`debug`) or only to the browser console (`info`). */
export type AppLogLevel = 'debug' | 'info';

/** Everything an app needs to start, sent once in `init`. No permission is needed for any of it. */
export interface AppContext {
  /** The app itself. */
  app: {
    /** The app id from the manifest, such as `com.example.notes`. */
    id: AppId;
    /** The installed version of the app. */
    version: string;
  };
  /** Id of this window, unique per desktop tab. */
  instance: string;
  /**
   * The signed-in user, for display and to adapt the interface. A backend identifies the user only by the
   * `X-Pithyx-User` headers the proxy sets (see `backend.token`), never by anything the window sends.
   */
  user: {
    /** Stable id of the user: the Linux uid, also `X-Pithyx-User-Id` for the backend; never given to another user. */
    id: number;
    /** The user name, lower case. */
    name: string;
    /** The name to show, if the user set one. */
    displayName: string | null;
    /** The user's groups; listing other users and groups needs `directory.read`. */
    groups: GroupRef[];
    /** Whether the user is an administrator. */
    isAdmin: boolean;
    /** Whether the user signs in with a second factor. */
    twoFactor: boolean;
  };
  /** The user's language for texts, a BCP 47 language tag such as `de`; `locale` starts with it. */
  language: string;
  /**
   * BCP 47 tag for formats: the user's language with the region of the box's formats, for example `de-AT`. The SDK
   * sets it as `lang` on the app's root element; the formatters (`app.format`) follow it.
   */
  locale: string;
  /** IANA time zone of the user, for example `Europe/Berlin`. Dates cross the API as UTC. */
  timeZone: string;
  /** The desktop's look; the SDK has already applied it. `themeChange` replaces it. */
  theme: Theme;
  /** The width class of the window; `sizeClassChange` follows it. */
  sizeClass: SizeClass;
  /** Why and with what the window was opened. */
  launch: LaunchArgs;
  /** The state this window saved with `session.save` before a reload or on another device, or null. */
  session: unknown;
  /** Permissions this app holds now; `permissionsChange` replaces the list. */
  permissions: string[];
  /** Every method name the desktop supports; check before calling one (see `supports`). */
  capabilities: string[];
  /** The box the app runs on: its name (host name) and the Pithyx version. */
  box: {
    /** The box's host name. */
    name: string;
    /** The Pithyx version of the box. */
    version: string;
  };
  /** Whether the window can be seen (not minimized); `visibilityChange` follows it. */
  visible: boolean;
  /** Whether the desktop reaches the box; `connectionChange` follows it. */
  online: boolean;
  /** Whether `debug` log entries reach the daemon; `logLevelChange` follows it. */
  logLevel: AppLogLevel;
  /** The app's backend, or null for an app without one. `backendStatus` follows its status. */
  backend: BackendInfo | null;
  /** The storage requests of the manifest with their state; `storageChange` replaces the list. */
  storage: StorageInfo[];
  /** The desktop that serves the app. */
  host: {
    /** The SDK version of the desktop. */
    version: string;
    /** The protocol version both sides use. */
    protocol: number;
  };
}

/** A button of a notification. */
export interface NotificationAction {
  /** Comes back in `notificationAction`; 1 to 64 characters. */
  id: string;
  /** Plain text, 1 to 40 characters, translated by the app. */
  label: string;
}

/** What the app learns when the user clicks one of its notifications. */
export interface NotificationActivation {
  /** The id `notify` resolved with. */
  notification: string;
  /** The `id` of the button the user clicked, or null for a click on the notification itself. */
  action: string | null;
  /** The `data` the app passed to `notify`, or null. */
  data: unknown;
}

/**
 * A notification the app shows. The app translates the texts; it knows the locale from the context. It appears as a
 * toast with the app's icon and stays in the user's notification history (at most 200 entries, 30 days).
 */
export interface NotificationRequest {
  /** Plain text, 1 to 200 characters. */
  title: string;
  /** Plain text, at most 1000 characters. */
  body?: string;
  /** How the toast looks; `info` when left out. */
  level?: 'info' | 'success' | 'warning' | 'error';
  /** At most 2 buttons. A click sends `notificationAction`, or starts the app with `launch.notification`. */
  actions?: NotificationAction[];
  /** Data that comes back with `notificationAction`, at most 4 KB as JSON. */
  data?: unknown;
  /**
   * Replaces the app's earlier notification with the same tag for this user, for example one per chat. 1 to 64
   * characters.
   */
  tag?: string;
}

/** A token for the app's backend. */
export interface BackendToken {
  /** The token, 256 bits; never log it. */
  token: string;
  /** ISO 8601 UTC; ask again before then. */
  expiresAt: string;
}

/** A one-time ticket for one WebSocket connection to the app's backend, valid 30 seconds. */
export interface BackendTicket {
  /** The ticket, for `?pithyx-ticket=`. */
  ticket: string;
  /** ISO 8601 UTC. */
  expiresAt: string;
}

/** A user of the box, as `directory.users` lists it. Deactivated users are left out. */
export interface DirectoryUser {
  /** Stable id of the user: the Linux uid, also `X-Pithyx-User-Id` for the backend; never given to another user. */
  id: number;
  /** The user name, lower case. */
  name: string;
  /** The name to show, if the user set one. */
  displayName: string | null;
  /** Ids of the user's groups (see `directory.groups`); the built-in group every user belongs to is left out. */
  groups: number[];
}

/** A group of the box, as `directory.groups` lists it. The built-in group every user belongs to is left out. */
export interface DirectoryGroup {
  /** Stable id (the group's Linux gid). */
  id: number;
  /** The group's name. */
  name: string;
  /** Free text, may be empty. */
  description: string;
}

/** A point in CSS pixels, relative to the top left corner of the app's window content. */
export interface Point {
  /** Distance from the left edge. */
  x: number;
  /** Distance from the top edge. */
  y: number;
}

/** An entry of a context menu the desktop draws for the app; the app translates the labels. */
export type ContextMenuItem =
  | {
      /** Comes back from `contextMenu.show` when the user picks the entry; 1 to 64 characters. */
      id: string;
      /** Plain text, 1 to 100 characters. */
      label: string;
      /** Shows the entry greyed out; it cannot be picked. */
      disabled?: boolean;
      /** Shows a check mark. */
      checked?: boolean;
      /** Marks a destructive entry, such as Delete. */
      danger?: boolean;
      /** A key hint shown next to the label, for example `Ctrl+C`. The app handles the keys itself. */
      shortcut?: string;
    }
  | {
      /** A line between groups of entries. */
      separator: true;
    };

/** How serious a log entry is. The Log Center filters by it; `debug` is for details only needed while looking for a fault. */
export type LogLevel = 'debug' | 'info' | 'warn' | 'error';

/** Fields that describe a log entry beyond its message, for example `{ file: 'a.jpg', attempt: 2 }`. */
export type LogData = Record<string, string | number | boolean | null>;

/** One entry of the app's log, as the SDK sends it (see `app.log`). */
export interface LogRecord {
  /** How serious the entry is. */
  level: LogLevel;
  /** At most 4000 characters; the SDK cuts longer ones. */
  message: string;
  /** At most 20 fields with texts of at most 1000 characters; the SDK cuts the rest. */
  data?: Record<string, string>;
}

/**
 * Every method an app can call: parameters and result. A method name is part of the stable API; a new method is a
 * minor change and appears in {@link AppContext.capabilities}.
 */
export interface Methods {
  /** Sets the window title (shown in the title bar and the taskbar). At most 200 characters. */
  'window.setTitle': { params: { title: string }; result: undefined };
  /** Closes the window. `beforeClose` is not sent for a close the app asked for. */
  'window.close': { params: Record<string, never>; result: undefined };
  /**
   * Marks the window as having unsaved changes or not. Once an app called it, the desktop asks `beforeClose` only
   * while the window is dirty; an app that never calls it is asked on every close.
   */
  'window.setDirty': { params: { dirty: boolean }; result: undefined };
  /**
   * Makes the window's taskbar entry pulse until the user looks at it. It never brings the window to the front or
   * takes the focus.
   */
  'window.requestAttention': { params: Record<string, never>; result: undefined };
  /**
   * Ends the desktop's loading placeholder of the window. Only needed with the manifest's `window.loading.untilReady`;
   * otherwise the placeholder goes when the app connects. Calling it again does nothing.
   */
  'window.ready': { params: Record<string, never>; result: undefined };
  /** Asks for a new free size. The desktop may clamp it; the result is the size the window got. */
  'window.setSize': {
    params: { width: number; height: number };
    result: { width: number; height: number };
  };
  /**
   * Writes entries to the app's log in the Log Center (source `app/<id>`, with the signed-in user). The SDK batches
   * `app.log` calls into this method; apps do not call it directly. At most 100 entries per call; the
   * daemon keeps each app within a rate limit and drops the rest. No permission needed.
   */
  'log.write': { params: { entries: LogRecord[] }; result: undefined };
  /** Shows a notification and resolves with its id. Needs the `notifications` permission. */
  notify: { params: NotificationRequest; result: { id: string } };
  /** A counter on the app's taskbar entry: a positive integer shows it, 0 or null removes it. */
  setBadge: { params: { count: number | null }; result: undefined };
  /**
   * Saves small state of this window (the current folder, a scroll position) with the desktop layout, at most 16 KB
   * as JSON. It comes back in the init context after a reload and with "Get desktop from ...".
   */
  'session.save': { params: { state: unknown }; result: undefined };
  /** Reads a value the app stored for this user, or null. */
  'storage.get': { params: { key: string }; result: unknown };
  /**
   * Stores a value for this user, on the daemon (sandboxed apps have no `localStorage`). Keys have at most 128
   * characters, values at most 64 KB as JSON, and an app keeps at most 1 MB per user.
   */
  'storage.set': { params: { key: string; value: unknown }; result: undefined };
  /** Removes a value the app stored for this user; a missing key is no error. */
  'storage.remove': { params: { key: string }; result: undefined };
  /** Every key the app stored for this user. */
  'storage.keys': { params: Record<string, never>; result: string[] };
  /**
   * A token for the app's backend, valid 5 minutes and for every endpoint of the app. `app.backend.fetch` and
   * `app.backend.url` send it for the app (as `Authorization: Bearer` or `?pithyx-token=`). The daemon's proxy checks
   * it and hands the backend the user as `X-Pithyx-User`, `X-Pithyx-User-Id`, `X-Pithyx-Groups` and
   * `X-Pithyx-Admin`; those headers are the only identity a backend may trust. With `renew` the desktop drops the
   * token it holds and issues a new one: the SDK asks so after the proxy refused a token as `app.token.invalid`, for
   * example because the box restarted.
   */
  'backend.token': { params: { renew?: boolean }; result: BackendToken };
  /** A one-time ticket for one WebSocket to an endpoint of the app's backend (`app.backend.socket` uses it). */
  'backend.ticket': { params: { endpoint: string }; result: BackendTicket };
  /** Every user of the box. Needs `directory.read`. */
  'directory.users': { params: Record<string, never>; result: DirectoryUser[] };
  /** Every group of the box. Needs `directory.read`. */
  'directory.groups': { params: Record<string, never>; result: DirectoryGroup[] };
  /**
   * Shows a context menu that the desktop draws at `at` (at most 50 entries) and resolves with the `id` of the entry
   * the user picked, or null when the menu was dismissed.
   */
  'contextMenu.show': { params: { at: Point; items: ContextMenuItem[] }; result: string | null };
  /** Copies text to the clipboard, at most 1 MB. Call it only for a user action, such as a click on Copy. */
  'clipboard.writeText': { params: { text: string }; result: undefined };
  /** Opens an `https:`, `http:` or `mailto:` URL in a new browser tab, without a reference to the desktop. */
  openUrl: { params: { url: string }; result: undefined };
  /**
   * The file API (served from step 6 on; check `supports`). The only way an app reaches files: sandboxed apps have no
   * file system, and the daemon checks the app's permissions and the user's rights on every call.
   *
   * Opens the desktop's file dialog; resolves with the files the user picked, empty when cancelled. Needs `files.read`.
   */
  'files.pick': {
    params: { multiple?: boolean; extensions?: string[] };
    result: FileRef[];
  };
  /** Opens the desktop's save dialog; resolves with the new file, or null when cancelled. Needs `files.write`. */
  'files.saveAs': {
    params: { suggestedName: string; data: Blob };
    result: FileRef | null;
  };
  /** Reads a file the app was given. Needs `files.read`. */
  'files.read': { params: { file: FileRef }; result: Blob };
  /** Replaces the contents of a writable file. Needs `files.write`. */
  'files.write': { params: { file: FileRef; data: Blob }; result: FileRef };
  /**
   * A URL for `<img>`, `<video>` or `fetch` that streams the file (with range requests) without the desktop session,
   * valid 5 minutes and only for this file. Needs `files.read`.
   */
  'files.url': { params: { file: FileRef }; result: { url: string; expiresAt: string } };
  /**
   * Lists a folder one page at a time (default 100 entries, at most 1000), in natural name order unless `sort` says
   * otherwise. Pass the page's `nextCursor` for the next one. Needs `files.shares` for a share.
   */
  'files.list': {
    params: {
      location: FileLocation;
      path: string;
      cursor?: string;
      limit?: number;
      sort?: SortKey;
      order?: SortOrder;
    };
    result: FolderPage;
  };
  /** A handle for a file in a named storage or a share, for `files.read`, `files.write` and `files.url`. */
  'files.open': { params: { location: FileLocation; path: string }; result: FileRef };

  /*
   * The file manager API. Everything below needs `files.shares` for shares; the daemon checks the user's rights
   * on every call. Methods that change files need write access to the share.
   */

  /** The shares the user can use, with the user's access to each; `sharesChanged` says when to ask again. */
  'files.shares': { params: Record<string, never>; result: ShareInfo[] };
  /**
   * Starts live changes of a folder: `folderChanged` events with the returned `watch` id arrive while the watch is
   * open. Watch only folders on screen and call `files.unwatch` when leaving them; a watch never wakes a disk.
   */
  'files.watch': { params: { location: FileLocation; path: string }; result: { watch: string } };
  /** Ends a watch started with `files.watch`. */
  'files.unwatch': { params: { watch: string }; result: undefined };
  /**
   * Uploads a file into a folder. The file appears only when it is complete; an existing name fails with
   * `fs.entry.exists` unless `overwrite`. With an `upload` id, `uploadProgress` events report the bytes sent.
   */
  'files.upload': {
    params: {
      location: FileLocation;
      folder: string;
      name: string;
      data: Blob;
      overwrite?: boolean;
      upload?: string;
    };
    result: FileEntry;
  };
  /** Downloads a file to the user's device through the browser. */
  'files.download': { params: { location: FileLocation; path: string }; result: undefined };
  /** Creates a folder; its parent must exist. */
  'files.createFolder': { params: { location: FileLocation; path: string }; result: FileEntry };
  /** Renames an entry within its folder; an existing name is never replaced (`fs.entry.exists`). */
  'files.rename': {
    params: { location: FileLocation; path: string; name: string };
    result: FileEntry;
  };
  /**
   * Starts a copy, move or delete as a job and resolves with it at once; `jobChanged` events follow its progress
   * until it finishes. Deleting moves to the share's recycle bin unless `permanent`. Source and target may each be a
   * share or a storage request.
   */
  'files.run': { params: FileOperation; result: JobInfo };
  /**
   * Earlier versions of a file or folder from the snapshots of the share's dataset, newest first; also of an entry
   * deleted since. A file is listed once per distinct content and not while it equals the current file. Empty when
   * the share has no snapshots. Needs read access to the share.
   */
  'files.versions': { params: { location: ShareLocation; path: string }; result: FileVersion[] };
  /**
   * Copies a version back as a job: next to the current entry as "name (version date)", or over it with
   * `overwrite` (files are replaced, folders merged). Needs write access to the share.
   */
  'files.restoreVersion': {
    params: { location: ShareLocation; path: string; snapshot: string; overwrite?: boolean };
    result: JobInfo;
  };
  /** Cancels a job the app started; a finished job stays as it is. */
  'jobs.cancel': { params: { job: number }; result: undefined };
  /** One page of a share's recycle bin, newest first. Needs read access to the share. */
  'trash.list': {
    params: { location: ShareLocation; cursor?: string; limit?: number };
    result: TrashPage;
  };
  /**
   * Puts items back where they were, recreating missing folders, as a job. A taken name gets a free one unless
   * `onConflict` says otherwise.
   */
  'trash.restore': {
    params: { location: ShareLocation; ids: number[]; onConflict?: ConflictPolicy };
    result: JobInfo;
  };
  /** Deletes items of the recycle bin for good, as a job. Only after the user confirmed it. */
  'trash.delete': { params: { location: ShareLocation; ids: number[] }; result: JobInfo };
  /** Deletes everything in the recycle bin for good, as a job. Only after the user confirmed it. */
  'trash.empty': { params: { location: ShareLocation }; result: JobInfo };
  /**
   * Shows the desktop's error dialog for a problem, with its translated text and the request id to copy. Use it for
   * failures the user has to know about; the app does not need its own error texts for daemon codes.
   */
  'ui.showError': { params: { problem: Problem }; result: undefined };
}

/** The name of a method an app can call. */
export type MethodName = keyof Methods;

/**
 * Events the desktop sends. An app ignores events it does not know, so new events are a minor change.
 */
export interface Events {
  /** A `single` app was launched again; the new launch arguments. */
  launch: LaunchArgs;
  /** The window got the focus. */
  focus: Record<string, never>;
  /** The window lost the focus. */
  blur: Record<string, never>;
  /** The window was minimized. */
  minimize: Record<string, never>;
  /** The window was shown again after it was minimized. */
  restore: Record<string, never>;
  /** The look changed (light or dark, accent, density); the SDK has applied it and updated `context.theme`. */
  themeChange: Theme;
  /** The user changed language, formats or time zone. The SDK updates the context and `app.format` follows. */
  localeChange: {
    /** The new language for texts. */
    language: string;
    /** The new BCP 47 tag for formats. */
    locale: string;
    /** The new IANA time zone. */
    timeZone: string;
  };
  /** The window crossed a width class. The SDK updates `context.sizeClass`. */
  sizeClassChange: {
    /** The new width class. */
    sizeClass: SizeClass;
  };
  /** The user opened a file with the app while its window was open. Only types from `fileHandlers`. */
  openFile: {
    /** The file to open. */
    file: FileRef;
  };
  /** Another version of the app is installed, by an update or a rollback; reload to use it (unsaved work first). */
  appUpdated: {
    /** The new version. */
    version: string;
  };
  /**
   * The window was minimized or shown again. Minimizing does not change the frame's own visibility, so this is the
   * way to pause work nobody sees. The SDK updates `context.visible`.
   */
  visibilityChange: {
    /** Whether the window can be seen. */
    visible: boolean;
  };
  /** The desktop lost or regained the connection to the box, for example while it restarts. Updates `context.online`. */
  connectionChange: {
    /** Whether the desktop reaches the box. */
    online: boolean;
  };
  /** The backend's status changed. The SDK updates `context.backend.status` and `context.backend.reason`. */
  backendStatus: {
    /** The new status. */
    status: BackendStatus;
    /** Why the backend cannot start, only while it is `unavailable` for that reason. */
    reason?: BackendUnavailableReason;
  };
  /**
   * An administrator granted or revoked permissions of the app; `permissions` is the complete new list. The SDK
   * replaces `context.permissions`; calls that need a revoked permission fail with `app.permission.denied`. Revoking
   * access to the app ends its windows.
   */
  permissionsChange: {
    /** Every permission the app holds now. */
    permissions: string[];
  };
  /** A storage mapping changed, or its pool was locked or came back. The SDK replaces `context.storage`. */
  storageChange: {
    /** Every storage request with its state now. */
    storage: StorageInfo[];
  };
  /** The user clicked a notification of the app or one of its buttons while a window of the app was open. */
  notificationAction: NotificationActivation;
  /** An administrator switched debug logging for the app on or off. The SDK updates `context.logLevel`. */
  logLevelChange: {
    /** The new log level. */
    level: AppLogLevel;
  };
  /**
   * The list of shares the user can use may have changed: a share was created, renamed, described or deleted, or the
   * user's access changed. Call `files.shares` again. Sent only to apps that hold `files.shares`; it carries nothing,
   * so it never tells an app about shares the user cannot use.
   */
  sharesChanged: Record<string, never>;
  /** Changes of a folder watched with `files.watch`. */
  folderChanged: FolderChange;
  /** Progress of an upload that has an `upload` id. */
  uploadProgress: UploadProgress;
  /** A job the app started changed: progress, then its final state. */
  jobChanged: JobInfo;
}

/** The name of an event the desktop sends. */
export type EventName = keyof Events;

/** Questions the desktop asks and waits for, each with the answer it expects. */
export interface Queries {
  /**
   * The user closes the window. Answer `allow: false` to keep it open (for example to ask about unsaved changes).
   * The desktop waits at most 5 seconds and then offers to close the window anyway. See `window.setDirty`.
   */
  beforeClose: {
    /** Carries nothing. */
    data: Record<string, never>;
    /** Whether the window may close. */
    answer: {
      /** False keeps the window open. */
      allow: boolean;
    };
  };
  /**
   * The window is about to go away: the user signs out (`logout`), the box shuts down or restarts, or Pithyx installs
   * an update. Save what you can; the desktop waits at most 2 seconds. It cannot be refused.
   */
  beforeUnload: {
    /** Why the window goes away. */
    data: {
      /** Sign-out, shutdown, restart or update. */
      reason: UnloadReason;
    };
    /** Nothing; the answer is ignored. */
    answer: Record<string, never>;
  };
}

/** Why `beforeUnload` is asked. */
export type UnloadReason = 'logout' | 'shutdown' | 'restart' | 'update';

/** The name of a question the desktop asks. */
export type QueryName = keyof Queries;

/** The desktop waits this long for an answer to `beforeClose`. */
export const BEFORE_CLOSE_TIMEOUT = 5000;
/** The desktop waits this long for an answer to `beforeUnload`. */
export const BEFORE_UNLOAD_TIMEOUT = 2000;
/**
 * An app waits this long for a response before a call fails with `sdk.request.timeout`. Calls that move a whole file
 * (`files.upload`, `files.read`, `files.write`) take as long as the transfer, and the dialogs of `files.pick` and
 * `files.saveAs` as long as the user; none of them has a time limit.
 */
export const REQUEST_TIMEOUT = 15_000;

/** Calls without a time limit, because they last as long as a transfer or as the user takes. */
export const UNTIMED_METHODS: readonly string[] = [
  'files.upload',
  'files.read',
  'files.write',
  'files.pick',
  'files.saveAs',
];

/** What the desktop posts to the iframe window, with the app's port transferred. */
export interface ConnectMessage {
  /** Always `pithyx:connect`. */
  type: 'pithyx:connect';
}

/** A message from the app to the desktop. */
export type AppMessage =
  | {
      /** The first message on the port. */
      type: 'hello';
      /** The protocol version the app speaks. */
      protocol: number;
      /** The SDK version the app was built with. */
      sdk: string;
    }
  | {
      /** A call of a method. */
      type: 'request';
      /** Unique per connection; the `response` carries it back. */
      id: number;
      /** A method name of {@link Methods}. */
      method: string;
      /** The method's parameters. */
      params: unknown;
    }
  | {
      /** The answer to a `query`. */
      type: 'reply';
      /** The `id` of the query. */
      id: number;
      /** The answer the question expects. */
      answer: unknown;
    };

/** A message from the desktop to the app. */
export type HostMessage =
  | {
      /** The answer to `hello`: the app may start. */
      type: 'init';
      /** The protocol version both sides use. */
      protocol: number;
      /** Everything the app needs to start. */
      context: AppContext;
    }
  | {
      /** The answer to `hello` when the desktop cannot serve the app; the port closes after it. */
      type: 'refuse';
      /** Why, for example `sdk.protocol.unsupported`. */
      problem: Problem;
    }
  | {
      /** The result of a `request`. */
      type: 'response';
      /** The `id` of the request. */
      id: number;
      /** The call succeeded. */
      ok: true;
      /** The method's result. */
      result: unknown;
    }
  | {
      /** The failure of a `request`. */
      type: 'response';
      /** The `id` of the request. */
      id: number;
      /** The call failed. */
      ok: false;
      /** Why it failed. */
      problem: Problem;
    }
  | {
      /** An event of {@link Events}. */
      type: 'event';
      /** The event name. */
      event: string;
      /** The event's data. */
      data: unknown;
    }
  | {
      /** A question of {@link Queries} the app answers with `reply`. */
      type: 'query';
      /** Unique per connection; the `reply` carries it back. */
      id: number;
      /** The question's name. */
      query: string;
      /** What the question carries. */
      data: unknown;
    };

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

/** Checks the shape of a message from an app. Anything else is dropped without an answer. */
export function isAppMessage(value: unknown): value is AppMessage {
  if (!isRecord(value)) {
    return false;
  }
  switch (value.type) {
    case 'hello':
      return typeof value.protocol === 'number' && typeof value.sdk === 'string';
    case 'request':
      return (
        Number.isSafeInteger(value.id) && typeof value.method === 'string' && 'params' in value
      );
    case 'reply':
      return Number.isSafeInteger(value.id) && 'answer' in value;
    default:
      return false;
  }
}

/** Checks the shape of a message from the desktop. */
export function isHostMessage(value: unknown): value is HostMessage {
  if (!isRecord(value)) {
    return false;
  }
  switch (value.type) {
    case 'init':
      return typeof value.protocol === 'number' && isRecord(value.context);
    case 'refuse':
      return isRecord(value.problem);
    case 'response':
      return Number.isSafeInteger(value.id) && typeof value.ok === 'boolean';
    case 'event':
      return typeof value.event === 'string';
    case 'query':
      return Number.isSafeInteger(value.id) && typeof value.query === 'string';
    default:
      return false;
  }
}
