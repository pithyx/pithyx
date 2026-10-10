import {
  backendUrl,
  DEFAULT_ENDPOINT,
  socketUrl,
  TICKET_PARAMETER,
  TOKEN_PARAMETER,
  windowOrigin,
} from './backend';
import { isProblem, sdkError, toProblem, PithyxError } from './errors';
import type {
  ConflictPolicy,
  FileEntry,
  FileLocation,
  FileOperation,
  FolderPage,
  JobInfo,
  ShareInfo,
  SortKey,
  SortOrder,
  TrashPage,
  FileVersion,
} from './files';
import { createFormatters, type Formatters } from './format';
import {
  isHostMessage,
  REQUEST_TIMEOUT,
  UNTIMED_METHODS,
  type AppContext,
  type BackendToken,
  type ContextMenuItem,
  type DirectoryGroup,
  type DirectoryUser,
  type Events,
  type EventName,
  type HostMessage,
  type MethodName,
  type Methods,
  type FileRef,
  type LogData,
  type LogLevel,
  type LogRecord,
  type NotificationRequest,
  type Point,
  type Queries,
  type QueryName,
} from './protocol';
import { applyTheme } from './theme';
import type { Endpoint } from './transport';
import { PROTOCOL_VERSION, SDK_VERSION } from './version';

const LOG_DELAY = 250;
const LOG_BATCH = 100;
const LOG_BUFFER = 500;
const LOG_MESSAGE = 4000;
const LOG_FIELDS = 20;
const LOG_FIELD = 1000;
/** A cached backend token is used until this long before it expires. */
const TOKEN_MARGIN = 30_000;

/** Cuts a log entry to the limits the desktop and the daemon accept; values become text. */
export function logRecord(level: LogLevel, message: string, data?: LogData): LogRecord {
  // Plain JavaScript callers may pass anything; logging never throws.
  const text: unknown = message;
  const record: LogRecord = { level, message: String(text).slice(0, LOG_MESSAGE) };
  if (data !== undefined) {
    const fields = Object.entries(data)
      .slice(0, LOG_FIELDS)
      .map(([key, value]) => [key.slice(0, 100), String(value).slice(0, LOG_FIELD)] as const);
    if (fields.length > 0) {
      record.data = Object.fromEntries(fields);
    }
  }
  return record;
}

const consoleMethods: Record<LogLevel, (...data: unknown[]) => void> = {
  debug: (...data) => {
    console.debug(...data);
  },
  info: (...data) => {
    console.info(...data);
  },
  warn: (...data) => {
    console.warn(...data);
  },
  error: (...data) => {
    console.error(...data);
  },
};

/** Every `app.log` entry also appears in the browser console, at its own level (D305). */
function mirrorToConsole(level: LogLevel, message: string, data?: LogData) {
  if (data === undefined) {
    consoleMethods[level](message);
  } else {
    consoleMethods[level](message, data);
  }
}

/** The context after an event: the SDK keeps the values an event changes current. */
function updatedContext(context: AppContext, event: string, data: unknown): AppContext {
  switch (event) {
    case 'themeChange':
      return { ...context, theme: data as AppContext['theme'] };
    case 'localeChange': {
      const { language, locale, timeZone } = data as Events['localeChange'];
      return { ...context, language, locale, timeZone };
    }
    case 'sizeClassChange':
      return { ...context, sizeClass: (data as Events['sizeClassChange']).sizeClass };
    case 'permissionsChange':
      return { ...context, permissions: [...(data as Events['permissionsChange']).permissions] };
    case 'visibilityChange':
      return { ...context, visible: (data as Events['visibilityChange']).visible };
    case 'connectionChange':
      return { ...context, online: (data as Events['connectionChange']).online };
    case 'logLevelChange':
      return { ...context, logLevel: (data as Events['logLevelChange']).level };
    case 'storageChange':
      return { ...context, storage: [...(data as Events['storageChange']).storage] };
    case 'backendStatus':
      return context.backend === null
        ? context
        : {
            ...context,
            backend: { ...context.backend, status: (data as Events['backendStatus']).status },
          };
    default:
      return context;
  }
}

/**
 * A listener for one event of the desktop, registered with `app.on`.
 *
 * @param data - What the event carries; see {@link Events} for each event.
 */
export type EventHandler<E extends EventName> = (data: Events[E]) => void;

/**
 * Answers one question of the desktop, registered with `app.answer`; may answer asynchronously.
 *
 * @param data - What the desktop asks with; see {@link Queries} for each question.
 * @returns The answer the question expects, or a promise of it.
 */
export type QueryHandler<Q extends QueryName> = (
  data: Queries[Q]['data'],
) => Queries[Q]['answer'] | Promise<Queries[Q]['answer']>;

/**
 * The API an app works with. The same object for core apps and sandboxed apps; only the transport differs. Every
 * method that talks to the desktop returns a promise and rejects with a {@link PithyxError}.
 *
 * Every such call can also reject with `sdk.request.timeout` (no answer within 15 seconds, see
 * {@link REQUEST_TIMEOUT}), `sdk.transport.closed` (the window closed or the desktop signed out),
 * `sdk.request.invalid` (parameters outside the limits the method states) and `sdk.method.unknown` (the desktop does
 * not serve the method to this app; check {@link PithyxApp.supports} first). The permissions are the manifest's
 * `permissions` that an administrator granted; `context.permissions` lists the ones the app holds now.
 */
export interface PithyxApp {
  /**
   * What the desktop told the app at start. The SDK keeps it current with the events: `theme`, `language`, `locale`,
   * `timeZone`, `sizeClass`, `permissions`, `visible`, `online`, `logLevel`, `backend.status` and `storage`. Needs no
   * permission. Reading it after the connection closed throws `sdk.transport.closed`.
   */
  readonly context: AppContext;
  /** Dates, numbers, sizes and lists in the user's language and time zone. Needs no permission. */
  readonly format: Formatters;
  /** The app's own window; every call acts on the window that makes it. Needs no permission. */
  readonly window: {
    /**
     * Sets the title in the title bar and the taskbar, at most 200 characters.
     *
     * @param title - The new title, plain text.
     * @throws `sdk.request.invalid` when the title is longer than 200 characters.
     */
    setTitle(title: string): Promise<void>;
    /** Closes the window without asking `beforeClose`. */
    close(): Promise<void>;
    /**
     * Asks for a new free size; resolves with the size the desktop gave the window.
     *
     * @param size - The wished size in CSS pixels; the desktop may clamp it.
     * @returns The size the window got.
     * @throws `sdk.request.invalid` when a value is not a finite number.
     */
    setSize(size: {
      /** Width in CSS pixels. */
      width: number;
      /** Height in CSS pixels. */
      height: number;
    }): Promise<{
      /** Width the window got, in CSS pixels. */
      width: number;
      /** Height the window got, in CSS pixels. */
      height: number;
    }>;
    /**
     * Marks the window as having unsaved changes or not. Once called, `beforeClose` is asked only while the window is
     * dirty.
     *
     * @param dirty - Whether the window has unsaved changes.
     */
    setDirty(dirty: boolean): Promise<void>;
    /** Makes the taskbar entry pulse until the user looks; never takes the focus. */
    requestAttention(): Promise<void>;
  };
  /**
   * Shows a notification and resolves with its id. Needs `notifications`. A click on one of its `actions` (or on the
   * notification) comes back as `notificationAction`, or as `launch.notification` when no window was open.
   *
   * @param notification - Title, body, level, buttons, data and tag; the limits are on {@link NotificationRequest}.
   * @returns The id of the notification, which `notificationAction` carries.
   * @throws `app.permission.denied` without the `notifications` permission.
   * @throws `sdk.request.invalid` when a text, the buttons or the data exceed their limits.
   */
  notify(notification: NotificationRequest): Promise<{
    /** The id of the notification. */
    id: string;
  }>;
  /**
   * A counter on the app's taskbar entry; 0 or null removes it. Needs no permission.
   *
   * @param count - A whole number of at least 0, or null.
   * @throws `sdk.request.invalid` when the count is negative or not a whole number.
   */
  setBadge(count: number | null): Promise<void>;
  /**
   * The app's log in the Log Center, where administrators see it next to the system's logs (source `app/<id>`, with
   * the signed-in user). Every entry also appears in the browser console. `info`, `warn` and `error` always go to the
   * daemon, `debug` only while an administrator switched debug logging on for the app (`context.logLevel`). Calls
   * return at once and never throw: the SDK collects entries for a moment and sends them together, and drops entries
   * when the app logs faster than its limit (bursts of 200, then 20 a second). Never log secrets such as passwords or
   * tokens. Messages are cut at 4000 characters, `data` at 20 fields of 1000 characters. Needs no permission.
   *
   * @example
   * ```ts
   * app.log.warn('Upload retried', { file: name, attempt: 2 });
   * ```
   */
  readonly log: {
    /**
     * Details only needed while looking for a fault; reaches the daemon only while debug logging is on.
     *
     * @param message - What happened, in English.
     * @param data - Fields that describe it, for example `{ file: 'a.jpg' }`.
     */
    debug(message: string, data?: LogData): void;
    /**
     * Something worth knowing happened.
     *
     * @param message - What happened, in English.
     * @param data - Fields that describe it.
     */
    info(message: string, data?: LogData): void;
    /**
     * Something went wrong but the app carries on.
     *
     * @param message - What happened, in English.
     * @param data - Fields that describe it.
     */
    warn(message: string, data?: LogData): void;
    /**
     * Something failed.
     *
     * @param message - What happened, in English.
     * @param data - Fields that describe it.
     */
    error(message: string, data?: LogData): void;
    /**
     * The same as `info`, for code written against `console.log`.
     *
     * @param message - What happened, in English.
     * @param data - Fields that describe it.
     */
    log(message: string, data?: LogData): void;
  };
  /** Small state of this window that survives a reload and moves with "Get desktop from ...". Needs no permission. */
  readonly session: {
    /**
     * Saves the window's state with the desktop layout, at most 16 KB as JSON.
     *
     * @param state - Any JSON value, for example the open folder and the scroll position.
     * @throws `app.storage.too_large` when the state is larger than 16 KB as JSON.
     */
    save(state: unknown): Promise<void>;
    /**
     * The state saved before the last reload or on another device, or null. Never rejects.
     *
     * @returns The saved state, or null.
     */
    restore(): Promise<unknown>;
  };
  /**
   * Values the app keeps per user on the box; sandboxed apps have no `localStorage`. Keys have at most 128 characters,
   * values at most 64 KB as JSON, and an app keeps at most 1 MB per user. Needs no permission.
   */
  readonly storage: {
    /**
     * The value stored under `key`, or null.
     *
     * @param key - 1 to 128 characters.
     * @returns The stored value, or null when there is none.
     * @throws `app.storage.key_invalid` when the key is empty or longer than 128 characters.
     */
    get<T = unknown>(key: string): Promise<T | null>;
    /**
     * Stores a value under `key`, replacing an earlier one.
     *
     * @param key - 1 to 128 characters.
     * @param value - Any JSON value, at most 64 KB as JSON.
     * @throws `app.storage.key_invalid` when the key is empty or longer than 128 characters.
     * @throws `app.storage.too_large` when the value is larger than 64 KB or the app would keep more than 1 MB for
     * the user.
     */
    set(key: string, value: unknown): Promise<void>;
    /**
     * Removes the value under `key`, if there is one; a missing key is no error.
     *
     * @param key - 1 to 128 characters.
     * @throws `app.storage.key_invalid` when the key is empty or longer than 128 characters.
     */
    remove(key: string): Promise<void>;
    /**
     * Every key the app stored for this user.
     *
     * @returns The keys, in no particular order.
     */
    keys(): Promise<string[]>;
  };
  /**
   * The app's own backend, through the daemon's proxy (D303). Paths are relative to an endpoint: the default `api` at
   * `/apps/<id>/api/`, any other endpoint `<name>` at `/apps/<id>/api-<name>/`. The SDK adds a token for the signed-in
   * user; the backend learns the user from the proxy's `X-Pithyx-*` headers. A name the manifest does not declare
   * fails with `sdk.backend.endpoint_unknown`. Needs no permission; an app without a backend has no endpoint.
   *
   * @example
   * ```ts
   * const messages = await (await app.backend.fetch('/messages')).json();
   * const stats = await app.backend.fetch('/stats', { endpoint: 'metrics' });
   * avatar.src = await app.backend.url('/avatar/1');
   * const live = await app.backend.socket('/live');
   * ```
   */
  readonly backend: {
    /**
     * A full URL with the token as `?pithyx-token=`, for `<img>`, `<video>` or a download link. It is valid as long as
     * its token, at least 30 seconds and at most 5 minutes; ask again for a new one.
     *
     * @param path - The path below the endpoint, with an optional query; it may not leave the endpoint with `..`.
     * @param options - The endpoint; `api` when left out.
     * @returns The absolute URL with the token.
     * @throws `sdk.backend.endpoint_unknown` when the app has no backend or the manifest declares no such endpoint.
     * @throws `sdk.request.invalid` when the path leaves the endpoint.
     * @throws `app.backend.missing` when the app has no backend.
     * @throws `app.app.not_found` when the user may no longer open the app.
     */
    url(
      path: string,
      options?: {
        /** The name of an endpoint the manifest declares; `api` when left out. */
        endpoint?: string;
      },
    ): Promise<string>;
    /**
     * `fetch` against the backend with `Authorization: Bearer <token>` and without cookies. Resolves with the response
     * whatever its status; a request that does not reach the box fails with `sdk.backend.unreachable`.
     *
     * @param path - The path below the endpoint, with an optional query; it may not leave the endpoint with `..`.
     * @param init - What `fetch` takes, plus the endpoint.
     * @returns The backend's response, also for 4xx and 5xx; the proxy answers 503 `app.backend.unavailable` while
     * the backend is updating or unavailable.
     * @throws `sdk.backend.endpoint_unknown` when the app has no backend or the manifest declares no such endpoint.
     * @throws `sdk.backend.unreachable` when the request gets no answer.
     * @throws `sdk.request.invalid` when the path leaves the endpoint.
     */
    fetch(
      path: string,
      init?: RequestInit & {
        /** The name of an endpoint the manifest declares; `api` when left out. */
        endpoint?: string;
      },
    ): Promise<Response>;
    /**
     * Opens a WebSocket to the backend with a one-time ticket (`?pithyx-ticket=`, valid 30 seconds).
     *
     * @param path - The path below the endpoint; it may not leave the endpoint with `..`.
     * @param options - The endpoint and the subprotocols.
     * @returns The WebSocket, still connecting; `wss:` on HTTPS and `ws:` on plain HTTP.
     * @throws `sdk.backend.endpoint_unknown` when the app has no backend or the manifest declares no such endpoint.
     * @throws `sdk.request.invalid` when the path leaves the endpoint.
     */
    socket(
      path: string,
      options?: {
        /** The name of an endpoint the manifest declares; `api` when left out. */
        endpoint?: string;
        /** WebSocket subprotocols, as `new WebSocket` takes them. */
        protocols?: string | string[];
      },
    ): Promise<WebSocket>;
    /**
     * The current token, for clients that build their own requests. The SDK keeps it until 30 seconds before it
     * expires and fetches a new one after that or after `permissionsChange`.
     *
     * @returns The token and when it expires.
     * @throws `app.backend.missing` when the app has no backend.
     * @throws `app.app.not_found` when the user may no longer open the app.
     */
    token(): Promise<BackendToken>;
  };
  /** The users and groups of the box. Needs `directory.read`; the app's own user is in `context.user` without it. */
  readonly directory: {
    /**
     * Every active user of the box.
     *
     * @returns The users, without deactivated ones.
     * @throws `app.permission.denied` without the `directory.read` permission.
     */
    users(): Promise<DirectoryUser[]>;
    /**
     * Every group of the box, without the built-in group of all users.
     *
     * @returns The groups.
     * @throws `app.permission.denied` without the `directory.read` permission.
     */
    groups(): Promise<DirectoryGroup[]>;
  };
  /** A context menu drawn by the desktop, also over the edge of the app's window. Needs no permission. */
  readonly contextMenu: {
    /**
     * Shows a menu drawn by the desktop at `at` (CSS pixels in the app's window) and resolves with the `id` of the
     * picked entry, or null when the user dismissed it. Call it from a `contextmenu` or click handler.
     *
     * @param at - Where the menu opens, relative to the window's content.
     * @param items - 1 to 50 entries and separators.
     * @returns The `id` of the picked entry, or null.
     * @throws `sdk.request.invalid` when there are no entries, more than 50, or an entry exceeds its limits.
     */
    show(at: Point, items: ContextMenuItem[]): Promise<string | null>;
  };
  /** The clipboard, through the desktop. Needs no permission. */
  readonly clipboard: {
    /**
     * Copies text, also over plain HTTP where the frame has no Clipboard API. Only for a user action.
     *
     * @param text - At most 1 MB of text.
     * @throws `sdk.request.invalid` when the text is longer than 1 MB.
     */
    writeText(text: string): Promise<void>;
  };
  /**
   * Opens an `https:`, `http:` or `mailto:` URL in a new browser tab, without a reference to the desktop. Needs no
   * permission.
   *
   * @param url - An absolute URL of at most 2048 characters.
   * @throws `sdk.request.invalid` for any other scheme (`javascript:`, `data:`) or a longer URL.
   */
  openUrl(url: string): Promise<void>;
  /**
   * Files. The first group works on files the user handed to the app (`files.read`, `files.write`); the second is the
   * file manager API over shares and named storage (`files.shares` for shares). See {@link Methods} for every call.
   *
   * A `FileLocation` is `{ storage: id }` for a storage request of the manifest or `{ share: id }` for a share. The
   * daemon checks the app's permissions and the user's own rights on every call: a share needs `files.shares`, its
   * content also `files.read` or `files.write`, and changes need write access to the share (`fs.share.read_only`
   * otherwise). Common failures: `app.permission.denied`, `fs.share.not_found`, `fs.share.unavailable` (its pool is
   * missing or locked), `app.storage.not_found` (no such storage request or not mapped), `fs.path.invalid`,
   * `fs.entry.not_found` and `fs.entry.access_denied`. Third-party apps get a subset of these methods so far; check
   * {@link PithyxApp.supports}.
   */
  readonly files: {
    /**
     * Lets the user pick files in the desktop's file dialog. Resolves with an empty list when the user cancels.
     * Needs `files.read`; must follow a user action in the window.
     *
     * @param options - Whether several files may be picked and which extensions are offered.
     * @returns The picked files, empty when the user cancelled.
     * @throws `app.permission.denied` without the `files.read` permission.
     */
    pick(options?: {
      /** Whether the user may pick more than one file. */
      multiple?: boolean;
      /** Extensions without dot, for example `['jpg', 'png']`; every file when left out. */
      extensions?: string[];
    }): Promise<FileRef[]>;
    /**
     * Lets the user choose where to save `data` in the desktop's save dialog. Resolves with null when the user
     * cancels. Needs `files.write`; must follow a user action in the window.
     *
     * @param suggestedName - The name the dialog proposes.
     * @param data - The content to save.
     * @returns The saved file, or null when the user cancelled.
     * @throws `app.permission.denied` without the `files.write` permission.
     */
    saveAs(suggestedName: string, data: Blob): Promise<FileRef | null>;
    /**
     * The content of a file the app was handed. Needs `files.read`.
     *
     * @param file - A handle from `pick`, `open`, `launch.file` or `openFile`.
     * @returns The content.
     * @throws `app.permission.denied` without the `files.read` permission.
     * @throws `fs.entry.not_found` when the file is gone.
     */
    read(file: FileRef): Promise<Blob>;
    /**
     * Replaces the content of a writable file the app was handed. Needs `files.write`.
     *
     * @param file - A handle whose `writable` is true.
     * @param data - The new content.
     * @returns The handle with the new size and change time.
     * @throws `app.permission.denied` without the `files.write` permission.
     * @throws `fs.share.read_only` when the user may only read the share.
     * @throws `fs.volume.full` when the pool has no space left.
     */
    write(file: FileRef, data: Blob): Promise<FileRef>;
    /**
     * A short-lived URL for `<img>`, `<video>` or `fetch` with range requests, valid 5 minutes and only for this file.
     * Needs `files.read`.
     *
     * @param file - A handle from `pick`, `open`, `launch.file` or `openFile`.
     * @returns The URL and when it expires.
     * @throws `app.permission.denied` without the `files.read` permission.
     */
    url(file: FileRef): Promise<{
      /** The URL, valid for this file only. */
      url: string;
      /** When the URL stops working, ISO 8601 UTC. */
      expiresAt: string;
    }>;
    /**
     * A handle for a file in a named storage or a share, for `read`, `write` and `url`. A share needs `files.shares`.
     *
     * @param location - A storage request or a share.
     * @param path - The file, relative to the location.
     * @returns The handle; `writable` says whether the app may write it.
     * @throws `app.permission.denied` without `files.shares` for a share.
     * @throws `fs.entry.not_found` when there is no such file.
     */
    open(location: FileLocation, path: string): Promise<FileRef>;
    /**
     * The shares the user can use, with the user's access to each. Needs `files.shares`. The `sharesChanged` event says
     * when the list may have changed.
     *
     * @returns The shares, without their place on the host.
     * @throws `app.permission.denied` without the `files.shares` permission.
     */
    shares(): Promise<ShareInfo[]>;
    /**
     * One page of a folder (default 100 entries, at most 1000), in natural name order unless `sort` says otherwise.
     * Pass the page's `nextCursor` as `cursor` for the next one; cursors stay valid while the folder changes. A share
     * needs `files.shares`.
     *
     * @param location - A storage request or a share.
     * @param path - The folder, relative to the location; `""` for its root.
     * @param page - Cursor, size and order of the page.
     * @returns One page of entries.
     * @throws `app.permission.denied` without `files.shares` for a share.
     * @throws `fs.entry.not_found` or `fs.entry.not_folder` when the path is no folder.
     * @throws `fs.cursor.invalid` when the cursor belongs to another listing.
     */
    list(
      location: FileLocation,
      path: string,
      page?: {
        /** The `nextCursor` of the previous page. */
        cursor?: string;
        /** Entries per page, 100 when left out, at most 1000. */
        limit?: number;
        /** What to sort by; `name` when left out. */
        sort?: SortKey;
        /** Ascending or descending; `asc` when left out. */
        order?: SortOrder;
      },
    ): Promise<FolderPage>;
    /**
     * Starts live changes of a folder and resolves with the id that its `folderChanged` events carry. Watch only
     * folders on screen and call `unwatch` when leaving them; a watch never wakes a sleeping disk. Needs `files.shares`.
     *
     * @param location - A share.
     * @param path - The folder, relative to the share.
     * @returns The watch id.
     * @throws `app.permission.denied` without the `files.shares` permission.
     */
    watch(location: FileLocation, path: string): Promise<string>;
    /**
     * Ends a watch; no more `folderChanged` events come for it.
     *
     * @param watch - The id `watch` resolved with.
     */
    unwatch(watch: string): Promise<void>;
    /**
     * Uploads `data` as `name` into `folder`. The file appears only when it is complete. An existing name fails with
     * `fs.entry.exists` unless `overwrite`; with an `upload` id, `uploadProgress` events report the bytes sent. The
     * call has no time limit, since it lasts as long as the transfer. A share needs `files.shares` and write access.
     *
     * @param location - A storage request or a share.
     * @param folder - The folder, relative to the location.
     * @param name - The file name, 1 to 255 characters without `/`.
     * @param data - The content.
     * @param options - Whether to replace an existing file and the id for progress events.
     * @returns The new entry.
     * @throws `fs.entry.exists` when the name is taken and `overwrite` is not set.
     * @throws `fs.share.read_only` when the user may only read the share.
     * @throws `fs.volume.full` when the pool has no space left.
     * @throws `fs.upload.incomplete` when the transfer broke off.
     * @throws `sdk.transfer.cancelled` when the user cancelled the upload in the taskbar.
     */
    upload(
      location: FileLocation,
      folder: string,
      name: string,
      data: Blob,
      options?: {
        /** Replace a file of the same name. Set it only when the user asked for it. */
        overwrite?: boolean;
        /** An id of the app's choice; `uploadProgress` events carry it. */
        upload?: string;
      },
    ): Promise<FileEntry>;
    /**
     * Downloads a file to the user's device through the browser. Needs `files.shares`.
     *
     * @param location - A share.
     * @param path - The file, relative to the share.
     * @throws `app.permission.denied` without the `files.shares` permission.
     */
    download(location: FileLocation, path: string): Promise<void>;
    /**
     * Creates a folder at `path`; its parent must exist and the name must be free (`fs.entry.exists`). A share needs
     * `files.shares` and write access.
     *
     * @param location - A storage request or a share.
     * @param path - The new folder, relative to the location.
     * @returns The new entry.
     * @throws `fs.entry.exists` when the name is taken.
     * @throws `fs.entry.not_found` when the parent folder does not exist.
     * @throws `fs.share.read_only` when the user may only read the share.
     */
    createFolder(location: FileLocation, path: string): Promise<FileEntry>;
    /**
     * Renames the entry at `path` to `name` within its folder; an existing name is never replaced. Needs `files.shares`
     * and write access.
     *
     * @param location - A share.
     * @param path - The entry, relative to the share.
     * @param name - The new name, 1 to 255 characters without `/`.
     * @returns The renamed entry.
     * @throws `fs.entry.exists` when the name is taken.
     * @throws `fs.name.invalid` when the name is not allowed.
     */
    rename(location: FileLocation, path: string, name: string): Promise<FileEntry>;
    /**
     * Starts a copy, move or delete as a job and resolves with it at once; `jobChanged` events follow its progress
     * until it finishes. A whole folder is named with `{ all: true, except }`, never with every name. Deleting moves
     * to the share's recycle bin unless `permanent`, which only an explicit request of the user may set. Needs
     * `files.shares` and write access.
     *
     * @param operation - What to do with which entries, and where to.
     * @returns The job as it started; failures of single entries end up in its `problem`.
     * @throws `fs.operation.invalid` when the operation does not fit (a target for delete, none for copy).
     * @throws `fs.operation.into_itself` when a folder would be copied or moved into itself.
     */
    run(operation: FileOperation): Promise<JobInfo>;
    /**
     * Earlier versions of the entry at `path` from the snapshots of the share's dataset, newest first; also of an
     * entry deleted since. Empty when the share has no snapshots. Needs `files.shares` and read access to the share.
     *
     * @param location - A share.
     * @param path - The entry, relative to the share; not its root.
     * @returns The versions, newest first.
     */
    versions(location: FileLocation, path: string): Promise<FileVersion[]>;
    /**
     * Copies a version back as a job: next to the current entry as "name (version date)", or over it with
     * `overwrite` (files are replaced, folders merged), which only an explicit request of the user may set. Needs
     * `files.shares` and write access.
     *
     * @param location - A share.
     * @param path - The entry, relative to the share; not its root.
     * @param snapshot - The `snapshot` of a version from `versions`.
     * @param options - Whether to replace the current entry.
     * @returns The job as it started.
     * @throws `fs.version.not_found` when the snapshot or the entry in it is gone.
     */
    restoreVersion(
      location: FileLocation,
      path: string,
      snapshot: string,
      options?: {
        /** Replace the current entry instead of restoring next to it. */
        overwrite?: boolean;
      },
    ): Promise<JobInfo>;
  };
  /** The jobs the app started with `files.run`, `files.restoreVersion` and `trash.*`. */
  readonly jobs: {
    /**
     * Cancels a job the app started; a job that already finished stays as it is.
     *
     * @param job - The `id` of the job.
     * @throws `jobs.job.not_found` when there is no such job of the user.
     */
    cancel(job: number): Promise<void>;
  };
  /**
   * The recycle bin of a share. Needs `files.shares`; reading it needs read access to the share, changing it write
   * access.
   */
  readonly trash: {
    /**
     * One page of the bin, newest first, with the share's retention.
     *
     * @param location - A share.
     * @param page - Cursor and size of the page.
     * @returns One page of items.
     * @throws `app.permission.denied` without the `files.shares` permission.
     */
    list(
      location: FileLocation,
      page?: {
        /** The `nextCursor` of the previous page. */
        cursor?: string;
        /** Items per page. */
        limit?: number;
      },
    ): Promise<TrashPage>;
    /**
     * Puts items back where they were, recreating missing folders, as a job. A taken name gets a free one ("name
     * (2)") unless `onConflict` says otherwise.
     *
     * @param location - A share.
     * @param ids - 1 to 1000 item ids from `list`.
     * @param onConflict - What to do with a taken name; `rename` when left out.
     * @returns The job as it started.
     */
    restore(location: FileLocation, ids: number[], onConflict?: ConflictPolicy): Promise<JobInfo>;
    /**
     * Deletes items of the bin for good, as a job. Only after the user confirmed it.
     *
     * @param location - A share.
     * @param ids - 1 to 1000 item ids from `list`.
     * @returns The job as it started.
     */
    delete(location: FileLocation, ids: number[]): Promise<JobInfo>;
    /**
     * Deletes everything in the bin for good, as a job. Only after the user confirmed it.
     *
     * @param location - A share.
     * @returns The job as it started.
     */
    empty(location: FileLocation): Promise<JobInfo>;
  };
  /** The desktop's own dialogs. Needs no permission. */
  readonly ui: {
    /**
     * Shows the desktop's error dialog for a failure, with the translated text of its code and the request id to
     * copy, so the app needs no texts of its own for daemon errors.
     *
     * @param error - A {@link PithyxError}, a problem, or anything else, which shows as an unexpected error.
     */
    showError(error: unknown): Promise<void>;
  };
  /**
   * Whether the desktop supports a method. Methods added after 1.0 may be missing on an older desktop; a call to a
   * method it does not support fails with `sdk.method.unknown`.
   *
   * @param method - A method name of {@link Methods}, such as `files.watch`.
   * @returns Whether `context.capabilities` lists it.
   */
  supports(method: MethodName): boolean;
  /**
   * Listens to an event of the desktop (see {@link Events}). Returns a function that stops listening.
   *
   * @param event - The event name.
   * @param handler - Called with the event's data each time it arrives.
   * @returns A function that removes the handler.
   */
  on<E extends EventName>(event: E, handler: EventHandler<E>): () => void;
  /**
   * Answers a question of the desktop (`beforeClose`, `beforeUnload`). One handler per question; the last one wins.
   * The answer to `beforeUnload` is ignored: it cannot be refused, only used to save.
   *
   * @param query - The question.
   * @param handler - Returns the answer, also asynchronously within the question's time limit.
   */
  answer<Q extends QueryName>(query: Q, handler: QueryHandler<Q>): void;
  /**
   * Low-level call of any method, for methods newer than this SDK's typed helpers. It needs what the method needs and
   * fails like it.
   *
   * @param method - A method name of {@link Methods}.
   * @param params - The method's parameters.
   * @returns The method's result.
   * @throws `sdk.method.unknown` when the desktop does not serve the method.
   */
  call<M extends MethodName>(
    method: M,
    params: Methods[M]['params'],
  ): Promise<Methods[M]['result']>;
}

/** How {@link createApp} and `connect` set up the app. */
export interface CreateAppOptions {
  /** Where the theme tokens go; the SDK sets them on `document.documentElement` by default. Null to skip. */
  themeRoot?: HTMLElement | null;
  /** Time limit of a call in milliseconds; {@link REQUEST_TIMEOUT} when left out. */
  timeout?: number;
  /** The box's address for backend URLs; by default taken from the window's own URL. */
  origin?: string;
}

/**
 * Connects an app over a transport end: sends `hello`, waits for `init`, and returns the app API. Rejects with
 * `sdk.protocol.unsupported` when the desktop refuses the app's protocol version. Core apps use it with
 * {@link inProcessPair}; sandboxed apps call `connect` instead.
 *
 * @param endpoint - The app's end of the transport.
 * @param options - Theme root, call time limit and the box's address.
 * @returns The app API, once the desktop sent `init`.
 * @throws `sdk.protocol.unsupported` when the desktop does not speak the SDK's protocol version.
 */
export function createApp(endpoint: Endpoint, options: CreateAppOptions = {}): Promise<PithyxApp> {
  const timeout = options.timeout ?? REQUEST_TIMEOUT;
  const themeRoot =
    options.themeRoot === undefined
      ? typeof document === 'undefined'
        ? null
        : document.documentElement
      : options.themeRoot;
  const pending = new Map<
    number,
    { resolve: (value: unknown) => void; reject: (error: PithyxError) => void }
  >();
  const listeners = new Map<string, Set<(data: unknown) => void>>();
  const answers = new Map<string, (data: unknown) => unknown>();
  let nextId = 1;
  let context: AppContext | undefined;
  const format = createFormatters(() => context ?? { locale: 'en', timeZone: 'UTC' });

  function applyLocale() {
    if (themeRoot !== null && context !== undefined) {
      themeRoot.lang = context.locale;
    }
  }

  return new Promise<PithyxApp>((resolveApp, rejectApp) => {
    function call(method: string, params: unknown): Promise<unknown> {
      const id = nextId++;
      return new Promise((resolve, reject) => {
        const timer = UNTIMED_METHODS.includes(method)
          ? undefined
          : setTimeout(() => {
              pending.delete(id);
              reject(sdkError('sdk.request.timeout', { method }));
            }, timeout);
        pending.set(id, {
          resolve: (value) => {
            clearTimeout(timer);
            resolve(value);
          },
          reject: (error) => {
            clearTimeout(timer);
            reject(error);
          },
        });
        endpoint.post({ type: 'request', id, method, params });
      });
    }

    function emit(event: string, data: unknown) {
      if (context !== undefined) {
        // The context follows the changes, so an app can read the current values at any time.
        context = updatedContext(context, event, data);
        if (event === 'themeChange' && themeRoot !== null) {
          applyTheme(context.theme, themeRoot);
        } else if (event === 'localeChange') {
          applyLocale();
        } else if (event === 'permissionsChange') {
          // A new token carries the new permissions; the old one is not used again.
          backendToken = undefined;
        }
      }
      for (const handler of listeners.get(event) ?? []) {
        handler(data);
      }
    }

    async function reply(id: number, query: string, data: unknown) {
      const handler = answers.get(query);
      let answer: unknown = query === 'beforeClose' ? { allow: true } : {};
      if (handler !== undefined) {
        try {
          answer = await handler(data);
        } catch {
          // A failing handler must not keep a window open or block a sign-out.
        }
      }
      // Only beforeClose can be refused; every other question gets an empty answer, whatever the handler returned.
      endpoint.post({ type: 'reply', id, answer: query === 'beforeClose' ? answer : {} });
    }

    function handle(message: HostMessage) {
      switch (message.type) {
        case 'init': {
          if (context !== undefined) {
            return;
          }
          context = message.context;
          if (themeRoot !== null) {
            applyTheme(context.theme, themeRoot);
          }
          applyLocale();
          resolveApp(api);
          return;
        }
        case 'refuse':
          endpoint.close();
          rejectApp(new PithyxError(message.problem));
          return;
        case 'response': {
          const entry = pending.get(message.id);
          if (entry === undefined) {
            return;
          }
          pending.delete(message.id);
          if (message.ok) {
            entry.resolve(message.result);
          } else {
            entry.reject(
              new PithyxError(
                isProblem(message.problem)
                  ? message.problem
                  : { code: 'system.internal.unexpected', params: {} },
              ),
            );
          }
          return;
        }
        case 'event':
          emit(message.event, message.data);
          return;
        case 'query':
          void reply(message.id, message.query, message.data);
          return;
      }
    }

    // Log entries wait a moment and go together; a full buffer drops the newest, like the daemon's rate limit.
    const logBuffer: LogRecord[] = [];
    let logTimer: ReturnType<typeof setTimeout> | undefined;
    function flushLog() {
      logTimer = undefined;
      const entries = logBuffer.splice(0, LOG_BATCH);
      if (entries.length > 0 && context?.capabilities.includes('log.write') === true) {
        call('log.write', { entries }).catch(() => undefined);
      }
      if (logBuffer.length > 0) {
        logTimer = setTimeout(flushLog, LOG_DELAY);
      }
    }
    function log(level: LogLevel, message: string, data?: LogData) {
      mirrorToConsole(level, message, data);
      if (logBuffer.length >= LOG_BUFFER || (level === 'debug' && context?.logLevel !== 'debug')) {
        return;
      }
      logBuffer.push(logRecord(level, message, data));
      logTimer ??= setTimeout(flushLog, LOG_DELAY);
    }

    function requirePermission(permission: string) {
      if (context?.permissions.includes(permission) !== true) {
        throw new PithyxError({ code: 'app.permission.denied', params: { permission } });
      }
    }

    // One token for all endpoints, shared by concurrent calls and kept until shortly before it expires.
    let backendToken: Promise<BackendToken> | undefined;
    function token(): Promise<BackendToken> {
      const current = backendToken;
      backendToken = (current ?? typed('backend.token', {})).then((value) =>
        Date.parse(value.expiresAt) - Date.now() > TOKEN_MARGIN
          ? value
          : typed('backend.token', {}),
      );
      const fresh = backendToken;
      fresh.catch(() => {
        if (backendToken === fresh) {
          backendToken = undefined;
        }
      });
      return fresh;
    }

    function endpointUrl(path: string, endpoint = DEFAULT_ENDPOINT): URL {
      if (api.context.backend?.endpoints.includes(endpoint) !== true) {
        throw sdkError('sdk.backend.endpoint_unknown', { endpoint });
      }
      return backendUrl(options.origin ?? windowOrigin(), api.context.app.id, path, endpoint);
    }

    function typed<M extends MethodName>(
      method: M,
      params: Methods[M]['params'],
    ): Promise<Methods[M]['result']> {
      return call(method, params);
    }

    const api: PithyxApp = {
      get context() {
        if (context === undefined) {
          throw sdkError('sdk.transport.closed');
        }
        return context;
      },
      format,
      window: {
        setTitle: async (title) => {
          await call('window.setTitle', { title });
        },
        close: async () => {
          await call('window.close', {});
        },
        setSize: async (size) =>
          (await call('window.setSize', size)) as { width: number; height: number },
        setDirty: async (dirty) => {
          await typed('window.setDirty', { dirty });
        },
        requestAttention: async () => {
          await typed('window.requestAttention', {});
        },
      },
      notify: async (notification) => typed('notify', notification),
      setBadge: async (count) => {
        await typed('setBadge', { count });
      },
      log: {
        debug: (message, data) => {
          log('debug', message, data);
        },
        info: (message, data) => {
          log('info', message, data);
        },
        warn: (message, data) => {
          log('warn', message, data);
        },
        error: (message, data) => {
          log('error', message, data);
        },
        log: (message, data) => {
          log('info', message, data);
        },
      },
      session: {
        save: async (state) => {
          await call('session.save', { state });
        },
        restore: () => Promise.resolve(context?.session ?? null),
      },
      storage: {
        get: async <T>(key: string) => (await call('storage.get', { key })) as T | null,
        set: async (key, value) => {
          await call('storage.set', { key, value });
        },
        remove: async (key) => {
          await call('storage.remove', { key });
        },
        keys: async () => (await call('storage.keys', {})) as string[],
      },
      backend: {
        url: async (path, { endpoint } = {}) => {
          const url = endpointUrl(path, endpoint);
          url.searchParams.set(TOKEN_PARAMETER, (await token()).token);
          return url.href;
        },
        fetch: async (path, init = {}) => {
          const { endpoint, ...request } = init;
          const url = endpointUrl(path, endpoint);
          const headers = new Headers(request.headers);
          headers.set('Authorization', `Bearer ${(await token()).token}`);
          try {
            return await fetch(url, { ...request, headers, credentials: 'omit' });
          } catch (error) {
            // fetch fails with a TypeError when the request never got an answer; an abort stays an abort.
            if (error instanceof TypeError) {
              throw sdkError('sdk.backend.unreachable', { endpoint: endpoint ?? DEFAULT_ENDPOINT });
            }
            throw error;
          }
        },
        socket: async (path, { endpoint = DEFAULT_ENDPOINT, protocols } = {}) => {
          const url = socketUrl(endpointUrl(path, endpoint));
          const { ticket } = await typed('backend.ticket', { endpoint });
          url.searchParams.set(TICKET_PARAMETER, ticket);
          return new WebSocket(url, protocols);
        },
        token,
      },
      directory: {
        users: async () => {
          requirePermission('directory.read');
          return typed('directory.users', {});
        },
        groups: async () => {
          requirePermission('directory.read');
          return typed('directory.groups', {});
        },
      },
      contextMenu: {
        show: async (at, items) => typed('contextMenu.show', { at, items }),
      },
      clipboard: {
        writeText: async (text) => {
          await typed('clipboard.writeText', { text });
        },
      },
      openUrl: async (url) => {
        await typed('openUrl', { url });
      },
      files: {
        pick: async (options = {}) => typed('files.pick', options),
        saveAs: async (suggestedName, data) => typed('files.saveAs', { suggestedName, data }),
        read: async (file) => typed('files.read', { file }),
        write: async (file, data) => typed('files.write', { file, data }),
        url: async (file) => typed('files.url', { file }),
        open: async (location, path) => typed('files.open', { location, path }),
        shares: async () => typed('files.shares', {}),
        list: async (location, path, page = {}) => typed('files.list', { location, path, ...page }),
        watch: async (location, path) => (await typed('files.watch', { location, path })).watch,
        unwatch: async (watch) => {
          await typed('files.unwatch', { watch });
        },
        upload: async (location, folder, name, data, options = {}) =>
          typed('files.upload', { location, folder, name, data, ...options }),
        download: async (location, path) => {
          await typed('files.download', { location, path });
        },
        createFolder: async (location, path) => typed('files.createFolder', { location, path }),
        rename: async (location, path, name) => typed('files.rename', { location, path, name }),
        run: async (operation) => typed('files.run', operation),
        versions: async (location, path) => typed('files.versions', { location, path }),
        restoreVersion: async (location, path, snapshot, options = {}) =>
          typed('files.restoreVersion', { location, path, snapshot, ...options }),
      },
      jobs: {
        cancel: async (job) => {
          await typed('jobs.cancel', { job });
        },
      },
      trash: {
        list: async (location, page = {}) => typed('trash.list', { location, ...page }),
        restore: async (location, ids, onConflict) =>
          typed('trash.restore', {
            location,
            ids,
            ...(onConflict === undefined ? {} : { onConflict }),
          }),
        delete: async (location, ids) => typed('trash.delete', { location, ids }),
        empty: async (location) => typed('trash.empty', { location }),
      },
      ui: {
        showError: async (error) => {
          await typed('ui.showError', { problem: toProblem(error) });
        },
      },
      supports: (method) => context?.capabilities.includes(method) ?? false,
      on: (event, handler) => {
        const set = listeners.get(event) ?? new Set();
        const wrapped = handler as (data: unknown) => void;
        set.add(wrapped);
        listeners.set(event, set);
        return () => {
          set.delete(wrapped);
        };
      },
      answer: (query, handler) => {
        answers.set(query, handler as (data: unknown) => unknown);
      },
      call: async (method, params) => await call(method, params),
    };

    endpoint.listen((message) => {
      if (isHostMessage(message)) {
        handle(message);
      }
    });
    endpoint.post({ type: 'hello', protocol: PROTOCOL_VERSION, sdk: SDK_VERSION });
  });
}
