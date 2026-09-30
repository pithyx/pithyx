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
 *    the app answers with `reply` (used for `beforeClose` and `beforeLogout`).
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

export type SizeClass = 'compact' | 'medium' | 'expanded';

/**
 * A file the user handed to the app (opened with it, or picked in a desktop dialog). The app never learns where the
 * file is: `ref` is an opaque handle, valid for this user and app, that only the SDK file API accepts.
 */
export interface FileRef {
  ref: string;
  /** File name with extension, for display. */
  name: string;
  /** MIME type, when known. */
  type?: string;
  size: number;
  /** Last change, ISO 8601 UTC. */
  modified: string;
  /** Whether the app may write it (`files.write` and the user's rights on it). */
  writable: boolean;
}

export interface LaunchArgs {
  /** App-defined arguments, for example a view to show. */
  args: unknown;
  /** A file to open, only of a type the manifest's `fileHandlers` declare. */
  file?: FileRef;
}

/** Everything an app needs to start, sent once in `init`. */
export interface AppContext {
  app: { id: AppId; version: string };
  /** Id of this window, unique per desktop tab. */
  instance: string;
  /** The signed-in user, for display. A backend identifies the user only by `X-Pithyx-User` (see `auth.token`). */
  user: { id: number; name: string; displayName: string | null };
  /**
   * BCP 47 tag of the user's language, for example `de-DE`. The SDK sets it as `lang` on the app's root element; the
   * formatters and the optional i18n helper follow it.
   */
  locale: string;
  /** IANA time zone of the user, for example `Europe/Berlin`. Dates cross the API as UTC. */
  timeZone: string;
  theme: Theme;
  sizeClass: SizeClass;
  launch: LaunchArgs;
  /** The state this window saved with `session.save` before a reload or on another device, or null. */
  session: unknown;
  /** Permissions this app holds now; `permissionRevoked` removes one. */
  permissions: string[];
  /** Every method name the desktop supports; check before calling one added after 1.0. */
  capabilities: string[];
  host: { version: string; protocol: number };
}

/** A notification the app shows. The app translates the texts; it knows the locale from the context. */
export interface NotificationRequest {
  title: string;
  body?: string;
  level?: 'info' | 'success' | 'warning' | 'error';
}

/** How serious a log entry is. The Log Center filters by it; `debug` is for details only needed while looking for a fault. */
export type LogLevel = 'debug' | 'info' | 'warn' | 'error';

/** Fields that describe a log entry beyond its message, for example `{ file: 'a.jpg', attempt: 2 }`. */
export type LogData = Record<string, string | number | boolean | null>;

/** One entry of the app's log, as the SDK sends it (see `app.log`). */
export interface LogRecord {
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
  /** Asks for a new free size. The desktop may clamp it; the result is the size the window got. */
  'window.setSize': {
    params: { width: number; height: number };
    result: { width: number; height: number };
  };
  /**
   * Writes entries to the app's log in the Log Center (source `app/<id>`, with the signed-in user). The SDK batches
   * `app.log` calls into this method; apps do not call it directly. At most 100 entries per call; the
   * daemon keeps each app within a rate limit and drops the rest. No permission needed (D257).
   */
  'log.write': { params: { entries: LogRecord[] }; result: undefined };
  /** Shows a notification. Needs the `notifications` permission. */
  notify: { params: NotificationRequest; result: undefined };
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
  'storage.remove': { params: { key: string }; result: undefined };
  'storage.keys': { params: Record<string, never>; result: string[] };
  /**
   * A token for the app's own backend at `/apps/<id>/api/`, valid 5 minutes. Send it as `Authorization: Bearer`.
   * Needs the `backend` permission. Ask again for a new one; do not cache it past `expiresAt`.
   *
   * The daemon's proxy checks the token and hands the backend the user as `X-Pithyx-User` (and `X-Pithyx-Groups`);
   * that header is the only identity a backend may trust, never anything the app sends in a request.
   */
  'auth.token': { params: Record<string, never>; result: { token: string; expiresAt: string } };
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
   * until it finishes. Deleting moves to the share's recycle bin unless `permanent`.
   */
  'files.run': { params: FileOperation; result: JobInfo };
  /**
   * Earlier versions of a file or folder from the snapshots of the share's dataset, newest first; also of an entry
   * deleted since. A file is listed once per distinct content and not while it equals the current file. Empty when
   * the share has no snapshots. Needs read access to the share.
   */
  'files.versions': { params: { location: FileLocation; path: string }; result: FileVersion[] };
  /**
   * Copies a version back as a job: next to the current entry as "name (version date)", or over it with
   * `overwrite` (files are replaced, folders merged). Needs write access to the share.
   */
  'files.restoreVersion': {
    params: { location: FileLocation; path: string; snapshot: string; overwrite?: boolean };
    result: JobInfo;
  };
  /** Cancels a job the app started; a finished job stays as it is. */
  'jobs.cancel': { params: { job: number }; result: undefined };
  /** One page of a share's recycle bin, newest first. Needs read access to the share. */
  'trash.list': {
    params: { location: FileLocation; cursor?: string; limit?: number };
    result: TrashPage;
  };
  /**
   * Puts items back where they were, recreating missing folders, as a job. A taken name gets a free one unless
   * `onConflict` says otherwise.
   */
  'trash.restore': {
    params: { location: FileLocation; ids: number[]; onConflict?: ConflictPolicy };
    result: JobInfo;
  };
  /** Deletes items of the recycle bin for good, as a job. Only after the user confirmed it. */
  'trash.delete': { params: { location: FileLocation; ids: number[] }; result: JobInfo };
  /** Deletes everything in the recycle bin for good, as a job. Only after the user confirmed it. */
  'trash.empty': { params: { location: FileLocation }; result: JobInfo };
  /**
   * Shows the desktop's error dialog for a problem, with its translated text and the request id to copy. Use it for
   * failures the user has to know about; the app does not need its own error texts for daemon codes.
   */
  'ui.showError': { params: { problem: Problem }; result: undefined };
}

export type MethodName = keyof Methods;

/**
 * Events the desktop sends. An app ignores events it does not know, so new events are a minor change.
 */
export interface Events {
  /** A `single` app was launched again; the new launch arguments. */
  launch: LaunchArgs;
  focus: Record<string, never>;
  blur: Record<string, never>;
  minimize: Record<string, never>;
  restore: Record<string, never>;
  themeChange: Theme;
  localeChange: { locale: string; timeZone: string };
  sizeClassChange: { sizeClass: SizeClass };
  /** The user opened a file with the app while its window was open. Only types from `fileHandlers`. */
  openFile: { file: FileRef };
  /** A new version of the app is installed; reload to use it (unsaved work first). */
  appUpdated: { version: string };
  /** The app's backend stopped answering; calls to it fail until `backendRestored`. */
  backendUnavailable: Record<string, never>;
  backendRestored: Record<string, never>;
  /**
   * An administrator revoked a permission of the app. The SDK removes it from `context.permissions`; calls that need
   * it fail with `app.permission.denied` from now on. Revoking `backend` or access to the app ends its windows.
   */
  permissionRevoked: { permission: string };
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

export type EventName = keyof Events;

/** Questions the desktop asks and waits for, each with the answer it expects. */
export interface Queries {
  /**
   * The user closes the window. Answer `allow: false` to keep it open (for example to ask about unsaved changes).
   * The desktop waits at most 5 seconds and then offers to close the window anyway.
   */
  beforeClose: { data: Record<string, never>; answer: { allow: boolean } };
  /** The user signs out. Save what you can; the desktop waits at most 2 seconds. It cannot be refused. */
  beforeLogout: { data: Record<string, never>; answer: Record<string, never> };
}

export type QueryName = keyof Queries;

/** The desktop waits this long for an answer to `beforeClose`. */
export const BEFORE_CLOSE_TIMEOUT = 5000;
/** The desktop waits this long for an answer to `beforeLogout`. */
export const BEFORE_LOGOUT_TIMEOUT = 2000;
/**
 * An app waits this long for a response before a call fails with `sdk.request.timeout`. Calls that move a whole file
 * (`files.upload`) take as long as the transfer and have no time limit.
 */
export const REQUEST_TIMEOUT = 15_000;

/** Calls without a time limit, because they last as long as a transfer. */
export const UNTIMED_METHODS: readonly string[] = ['files.upload'];

/** What the desktop posts to the iframe window, with the app's port transferred. */
export interface ConnectMessage {
  type: 'pithyx:connect';
}

export type AppMessage =
  | { type: 'hello'; protocol: number; sdk: string }
  | { type: 'request'; id: number; method: string; params: unknown }
  | { type: 'reply'; id: number; answer: unknown };

export type HostMessage =
  | { type: 'init'; protocol: number; context: AppContext }
  | { type: 'refuse'; problem: Problem }
  | { type: 'response'; id: number; ok: true; result: unknown }
  | { type: 'response'; id: number; ok: false; problem: Problem }
  | { type: 'event'; event: string; data: unknown }
  | { type: 'query'; id: number; query: string; data: unknown };

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
