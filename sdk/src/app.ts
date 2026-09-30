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

type EventHandler<E extends EventName> = (data: Events[E]) => void;
type QueryHandler<Q extends QueryName> = (
  data: Queries[Q]['data'],
) => Queries[Q]['answer'] | Promise<Queries[Q]['answer']>;

/**
 * The API an app works with. The same object for core apps and sandboxed apps; only the transport differs. Every
 * method that talks to the desktop returns a promise and rejects with a {@link PithyxError}.
 */
export interface PithyxApp {
  /**
   * What the desktop told the app at start. `theme`, `locale`, `timeZone`, `sizeClass` and `permissions` stay
   * current.
   */
  readonly context: AppContext;
  /** Dates, numbers, sizes and lists in the user's language and time zone. */
  readonly format: Formatters;
  readonly window: {
    setTitle(title: string): Promise<void>;
    close(): Promise<void>;
    setSize(size: { width: number; height: number }): Promise<{ width: number; height: number }>;
  };
  notify(notification: NotificationRequest): Promise<void>;
  /**
   * The app's log in the Log Center, where administrators see it next to the system's logs (source `app/<id>`, with
   * the signed-in user). Calls return at once and never throw: the SDK collects entries for a moment and sends them
   * together, and drops entries when the app logs faster than its limit (bursts of 200, then 20 a second). Never log
   * secrets such as passwords or tokens. Messages are cut at 4000 characters, `data` at 20 fields of 1000 characters.
   *
   * @example
   * ```ts
   * app.log.warn('Upload retried', { file: name, attempt: 2 });
   * ```
   */
  readonly log: {
    debug(message: string, data?: LogData): void;
    info(message: string, data?: LogData): void;
    warn(message: string, data?: LogData): void;
    error(message: string, data?: LogData): void;
  };
  readonly session: {
    save(state: unknown): Promise<void>;
    /** The state saved before the last reload or on another device, or null. */
    restore(): Promise<unknown>;
  };
  readonly storage: {
    get<T = unknown>(key: string): Promise<T | null>;
    set(key: string, value: unknown): Promise<void>;
    remove(key: string): Promise<void>;
    keys(): Promise<string[]>;
  };
  readonly auth: {
    token(): Promise<{ token: string; expiresAt: string }>;
  };
  /**
   * Files. The first group works on files the user handed to the app (`files.read`, `files.write`); the second is the
   * file manager API over shares and named storage (`files.shares` for shares). See {@link Methods} for every call.
   */
  readonly files: {
    /**
     * Lets the user pick files in the desktop's file dialog. Resolves with an empty list when the user cancels.
     * Needs `files.read`; must follow a user action in the window.
     */
    pick(options?: { multiple?: boolean; extensions?: string[] }): Promise<FileRef[]>;
    /**
     * Lets the user choose where to save `data` in the desktop's save dialog. Resolves with null when the user
     * cancels. Needs `files.write`; must follow a user action in the window.
     */
    saveAs(suggestedName: string, data: Blob): Promise<FileRef | null>;
    /** The content of a file the app was handed. Needs `files.read`. */
    read(file: FileRef): Promise<Blob>;
    /** Replaces the content of a writable file the app was handed. Needs `files.write`. */
    write(file: FileRef, data: Blob): Promise<FileRef>;
    /**
     * A short-lived URL for `<img>`, `<video>` or `fetch` with range requests, valid 5 minutes and only for this file.
     * Needs `files.read`.
     */
    url(file: FileRef): Promise<{ url: string; expiresAt: string }>;
    /** A handle for a file in a named storage or a share, for `read`, `write` and `url`. */
    open(location: FileLocation, path: string): Promise<FileRef>;
    /**
     * The shares the user can use, with the user's access to each. Needs `files.shares`. The `sharesChanged` event says
     * when the list may have changed.
     */
    shares(): Promise<ShareInfo[]>;
    /**
     * One page of a folder (default 100 entries, at most 1000), in natural name order unless `sort` says otherwise.
     * Pass the page's `nextCursor` as `cursor` for the next one; cursors stay valid while the folder changes.
     */
    list(
      location: FileLocation,
      path: string,
      page?: { cursor?: string; limit?: number; sort?: SortKey; order?: SortOrder },
    ): Promise<FolderPage>;
    /**
     * Starts live changes of a folder and resolves with the id that its `folderChanged` events carry. Watch only
     * folders on screen and call `unwatch` when leaving them; a watch never wakes a sleeping disk.
     */
    watch(location: FileLocation, path: string): Promise<string>;
    /** Ends a watch; no more `folderChanged` events come for it. */
    unwatch(watch: string): Promise<void>;
    /**
     * Uploads `data` as `name` into `folder`. The file appears only when it is complete. An existing name fails with
     * `fs.entry.exists` unless `overwrite`; with an `upload` id, `uploadProgress` events report the bytes sent. The
     * call has no time limit, since it lasts as long as the transfer.
     */
    upload(
      location: FileLocation,
      folder: string,
      name: string,
      data: Blob,
      options?: { overwrite?: boolean; upload?: string },
    ): Promise<FileEntry>;
    /** Downloads a file to the user's device through the browser. */
    download(location: FileLocation, path: string): Promise<void>;
    /** Creates a folder at `path`; its parent must exist and the name must be free (`fs.entry.exists`). */
    createFolder(location: FileLocation, path: string): Promise<FileEntry>;
    /** Renames the entry at `path` to `name` within its folder; an existing name is never replaced. */
    rename(location: FileLocation, path: string, name: string): Promise<FileEntry>;
    /**
     * Starts a copy, move or delete as a job and resolves with it at once; `jobChanged` events follow its progress
     * until it finishes. A whole folder is named with `{ all: true, except }`, never with every name. Deleting moves
     * to the share's recycle bin unless `permanent`, which only an explicit request of the user may set.
     */
    run(operation: FileOperation): Promise<JobInfo>;
    /**
     * Earlier versions of the entry at `path` from the snapshots of the share's dataset, newest first; also of an
     * entry deleted since. Empty when the share has no snapshots. Needs read access to the share.
     */
    versions(location: FileLocation, path: string): Promise<FileVersion[]>;
    /**
     * Copies a version back as a job: next to the current entry as "name (version date)", or over it with
     * `overwrite` (files are replaced, folders merged), which only an explicit request of the user may set.
     */
    restoreVersion(
      location: FileLocation,
      path: string,
      snapshot: string,
      options?: { overwrite?: boolean },
    ): Promise<JobInfo>;
  };
  readonly jobs: {
    /** Cancels a job the app started; a job that already finished stays as it is. */
    cancel(job: number): Promise<void>;
  };
  /** The recycle bin of a share. Reading it needs read access to the share, changing it write access. */
  readonly trash: {
    /** One page of the bin, newest first, with the share's retention. */
    list(location: FileLocation, page?: { cursor?: string; limit?: number }): Promise<TrashPage>;
    /**
     * Puts items back where they were, recreating missing folders, as a job. A taken name gets a free one ("name
     * (2)") unless `onConflict` says otherwise.
     */
    restore(location: FileLocation, ids: number[], onConflict?: ConflictPolicy): Promise<JobInfo>;
    /** Deletes items of the bin for good, as a job. Only after the user confirmed it. */
    delete(location: FileLocation, ids: number[]): Promise<JobInfo>;
    /** Deletes everything in the bin for good, as a job. Only after the user confirmed it. */
    empty(location: FileLocation): Promise<JobInfo>;
  };
  readonly ui: {
    /**
     * Shows the desktop's error dialog for a failure, with the translated text of its code and the request id to
     * copy, so the app needs no texts of its own for daemon errors.
     */
    showError(error: unknown): Promise<void>;
  };
  /** Whether the desktop supports a method; methods added after 1.0 may be missing on an older desktop. */
  supports(method: MethodName): boolean;
  /** Listens to an event of the desktop (see {@link Events}). Returns a function that stops listening. */
  on<E extends EventName>(event: E, handler: EventHandler<E>): () => void;
  /** Answers a question of the desktop (`beforeClose`, `beforeLogout`). One handler per question; the last one wins. */
  answer<Q extends QueryName>(query: Q, handler: QueryHandler<Q>): void;
  /** Low-level call of any method, for methods newer than this SDK's typed helpers. */
  call<M extends MethodName>(
    method: M,
    params: Methods[M]['params'],
  ): Promise<Methods[M]['result']>;
}

export interface CreateAppOptions {
  /** Where the theme tokens go; the SDK sets them on `document.documentElement` by default. Null to skip. */
  themeRoot?: HTMLElement | null;
  /** Time limit of a call in milliseconds. */
  timeout?: number;
}

/**
 * Connects an app over a transport end: sends `hello`, waits for `init`, and returns the app API. Rejects with
 * `sdk.protocol.unsupported` when the desktop refuses the app's protocol version.
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
        if (event === 'themeChange') {
          context = { ...context, theme: data as AppContext['theme'] };
          if (themeRoot !== null) {
            applyTheme(context.theme, themeRoot);
          }
        } else if (event === 'localeChange') {
          const change = data as Events['localeChange'];
          context = { ...context, locale: change.locale, timeZone: change.timeZone };
          applyLocale();
        } else if (event === 'permissionRevoked') {
          const { permission } = data as Events['permissionRevoked'];
          context = {
            ...context,
            permissions: context.permissions.filter((granted) => granted !== permission),
          };
        } else if (event === 'sizeClassChange') {
          context = { ...context, sizeClass: (data as Events['sizeClassChange']).sizeClass };
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
      endpoint.post({ type: 'reply', id, answer });
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
      if (logBuffer.length >= LOG_BUFFER) {
        return;
      }
      logBuffer.push(logRecord(level, message, data));
      logTimer ??= setTimeout(flushLog, LOG_DELAY);
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
      },
      notify: async (notification) => {
        await call('notify', notification);
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
      auth: {
        token: async () => (await call('auth.token', {})) as { token: string; expiresAt: string },
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
