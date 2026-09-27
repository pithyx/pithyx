import { isProblem, sdkError, VyxisError } from './errors';
import { createFormatters, type Formatters } from './format';
import {
  isHostMessage,
  REQUEST_TIMEOUT,
  type AppContext,
  type Events,
  type EventName,
  type HostMessage,
  type MethodName,
  type Methods,
  type FileEntry,
  type FileLocation,
  type FileRef,
  type NotificationRequest,
  type Queries,
  type QueryName,
} from './protocol';
import { applyTheme } from './theme';
import type { Endpoint } from './transport';
import { PROTOCOL_VERSION, SDK_VERSION } from './version';

type EventHandler<E extends EventName> = (data: Events[E]) => void;
type QueryHandler<Q extends QueryName> = (
  data: Queries[Q]['data'],
) => Queries[Q]['answer'] | Promise<Queries[Q]['answer']>;

/**
 * The API an app works with. The same object for core apps and sandboxed apps; only the transport differs. Every
 * method that talks to the desktop returns a promise and rejects with a {@link VyxisError}.
 */
export interface VyxisApp {
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
  /** The file API; see {@link Methods} for what each call needs. Served from step 6 on (`supports('files.list')`). */
  readonly files: {
    pick(options?: { multiple?: boolean; extensions?: string[] }): Promise<FileRef[]>;
    saveAs(suggestedName: string, data: Blob): Promise<FileRef | null>;
    read(file: FileRef): Promise<Blob>;
    write(file: FileRef, data: Blob): Promise<FileRef>;
    url(file: FileRef): Promise<{ url: string; expiresAt: string }>;
    list(
      location: FileLocation,
      path: string,
      page?: { cursor?: string; limit?: number },
    ): Promise<{ entries: FileEntry[]; nextCursor: string | null }>;
    open(location: FileLocation, path: string): Promise<FileRef>;
  };
  /** Whether the desktop supports a method; methods added after 1.0 may be missing on an older desktop. */
  supports(method: MethodName): boolean;
  /** Listens to a lifecycle event. Returns a function that stops listening. */
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
export function createApp(endpoint: Endpoint, options: CreateAppOptions = {}): Promise<VyxisApp> {
  const timeout = options.timeout ?? REQUEST_TIMEOUT;
  const themeRoot =
    options.themeRoot === undefined
      ? typeof document === 'undefined'
        ? null
        : document.documentElement
      : options.themeRoot;
  const pending = new Map<
    number,
    { resolve: (value: unknown) => void; reject: (error: VyxisError) => void }
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

  return new Promise<VyxisApp>((resolveApp, rejectApp) => {
    function call(method: string, params: unknown): Promise<unknown> {
      const id = nextId++;
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => {
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
          rejectApp(new VyxisError(message.problem));
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
              new VyxisError(
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

    const api: VyxisApp = {
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
        pick: async (options = {}) => (await call('files.pick', options)) as FileRef[],
        saveAs: async (suggestedName, data) =>
          (await call('files.saveAs', { suggestedName, data })) as FileRef | null,
        read: async (file) => (await call('files.read', { file })) as Blob,
        write: async (file, data) => (await call('files.write', { file, data })) as FileRef,
        url: async (file) =>
          (await call('files.url', { file })) as { url: string; expiresAt: string },
        list: async (location, path, page = {}) =>
          (await call('files.list', { location, path, ...page })) as {
            entries: FileEntry[];
            nextCursor: string | null;
          },
        open: async (location, path) => (await call('files.open', { location, path })) as FileRef,
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
