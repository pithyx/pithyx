/**
 * The desktop's side of the protocol (`@pithyx/sdk/host`). The desktop serves every app window with one connection;
 * the connection is the app's identity, so a handler always knows which app and window called, whatever the message
 * claims. Apps do not use this module.
 */
import { sdkError, toProblem, PithyxError } from './errors';
import {
  isAppMessage,
  type AppContext,
  type AppMessage,
  type Events,
  type EventName,
  type MethodName,
  type Methods,
  type Queries,
  type QueryName,
} from './protocol';
import type { Endpoint } from './transport';
import { isProtocolSupported, MIN_PROTOCOL_VERSION, PROTOCOL_VERSION } from './version';

export type MethodHandlers = {
  [M in MethodName]?: (
    params: Methods[M]['params'],
  ) => Methods[M]['result'] | Promise<Methods[M]['result']>;
};

/**
 * Methods outside the public protocol that a host serves to its own apps, each with its own parameter check. They are
 * not versioned, not listed in `capabilities`, and a host never gives them to a third-party app.
 */
export type HostExtensions = Record<
  string,
  { validate(params: unknown): boolean; handle(params: unknown): unknown }
>;

export interface ServeOptions {
  /** The init context; `capabilities` is filled in from the handlers. */
  context: Omit<AppContext, 'capabilities'>;
  methods: MethodHandlers;
  /** Host-only methods for the host's own apps; see {@link HostExtensions}. */
  extensions?: HostExtensions;
  /** Called when the app completed the handshake. */
  onReady?: (hello: { protocol: number; sdk: string }) => void;
}

export interface AppConnection {
  emit<E extends EventName>(event: E, data: Events[E]): void;
  /** Asks the app and waits; resolves undefined when it does not answer in time. */
  ask<Q extends QueryName>(
    query: Q,
    data: Queries[Q]['data'],
    timeout: number,
  ): Promise<Queries[Q]['answer'] | undefined>;
  close(): void;
}

const MAX_TITLE = 200;
const MAX_KEY = 128;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isSize(value: unknown): boolean {
  return isRecord(value) && Number.isFinite(value.width) && Number.isFinite(value.height);
}

function isKey(value: unknown): boolean {
  return (
    isRecord(value) &&
    typeof value.key === 'string' &&
    value.key.length > 0 &&
    value.key.length <= MAX_KEY
  );
}

function isFileRef(value: unknown): boolean {
  return isRecord(value) && typeof value.ref === 'string' && value.ref.length > 0;
}

function isLocation(value: unknown): boolean {
  return (
    isRecord(value) &&
    (typeof value.storage === 'string' || typeof value.share === 'string') &&
    Object.keys(value).length === 1
  );
}

/** "" for the root, otherwise names joined by "/": no leading or doubled slash, no "." or "..". */
function isRelativePath(value: unknown): boolean {
  return (
    typeof value === 'string' &&
    value.length <= 4096 &&
    !value.includes('\0') &&
    (value === '' || value.split('/').every((part) => part !== '' && part !== '.' && part !== '..'))
  );
}

const isBlob = (value: unknown) => typeof Blob !== 'undefined' && value instanceof Blob;

const MAX_NAMES = 1000;

function isName(value: unknown): boolean {
  return (
    typeof value === 'string' && value.length > 0 && value.length <= 255 && !/[/\0]/.test(value)
  );
}

function isNames(value: unknown): boolean {
  return Array.isArray(value) && value.length <= MAX_NAMES && value.every(isName);
}

function isIds(value: unknown): boolean {
  return (
    Array.isArray(value) &&
    value.length > 0 &&
    value.length <= MAX_NAMES &&
    value.every((id) => Number.isSafeInteger(id))
  );
}

function isOneOf(value: unknown, allowed: readonly string[], optional = true): boolean {
  return (
    (optional && value === undefined) || (typeof value === 'string' && allowed.includes(value))
  );
}

const CONFLICTS = ['fail', 'skip', 'overwrite', 'rename'] as const;

function isSelection(value: unknown): boolean {
  if (
    !isRecord(value) ||
    !isLocation(value.location) ||
    !isRelativePath(value.folder) ||
    !isRecord(value.items)
  ) {
    return false;
  }
  const items = value.items;
  return items.all === true
    ? isNames(items.except)
    : isNames(items.names) && (items.names as unknown[]).length > 0;
}

function isOperation(p: unknown): boolean {
  if (
    !isRecord(p) ||
    !isOneOf(p.kind, ['copy', 'move', 'delete'], false) ||
    !isSelection(p.source)
  ) {
    return false;
  }
  const hasTarget = p.target !== undefined;
  if (hasTarget !== (p.kind !== 'delete')) {
    return false;
  }
  return (
    (!hasTarget ||
      (isRecord(p.target) && isLocation(p.target.location) && isRelativePath(p.target.path))) &&
    isOneOf(p.onConflict, CONFLICTS) &&
    (p.permanent === undefined || typeof p.permanent === 'boolean')
  );
}

const isPage = (p: Record<string, unknown>) =>
  (p.cursor === undefined || typeof p.cursor === 'string') &&
  (p.limit === undefined || Number.isSafeInteger(p.limit));

const inLocation = (p: unknown): p is Record<string, unknown> =>
  isRecord(p) && isLocation(p.location);

/** Parameter checks per method; a call that fails its check never reaches the handler. */
const validators: Record<MethodName, (params: unknown) => boolean> = {
  'window.setTitle': (p) =>
    isRecord(p) && typeof p.title === 'string' && p.title.length <= MAX_TITLE,
  'window.close': isRecord,
  'window.setSize': isSize,
  notify: (p) =>
    isRecord(p) &&
    typeof p.title === 'string' &&
    (p.body === undefined || typeof p.body === 'string') &&
    (p.level === undefined || ['info', 'success', 'warning', 'error'].includes(p.level as string)),
  'session.save': (p) => isRecord(p) && 'state' in p,
  'storage.get': isKey,
  'storage.set': (p) => isKey(p) && isRecord(p) && 'value' in p,
  'storage.remove': isKey,
  'storage.keys': isRecord,
  'auth.token': isRecord,
  'files.pick': (p) =>
    isRecord(p) &&
    (p.multiple === undefined || typeof p.multiple === 'boolean') &&
    (p.extensions === undefined ||
      (Array.isArray(p.extensions) && p.extensions.every((e) => typeof e === 'string'))),
  'files.saveAs': (p) => isRecord(p) && typeof p.suggestedName === 'string' && isBlob(p.data),
  'files.read': (p) => isRecord(p) && isFileRef(p.file),
  'files.write': (p) => isRecord(p) && isFileRef(p.file) && isBlob(p.data),
  'files.url': (p) => isRecord(p) && isFileRef(p.file),
  'files.list': (p) =>
    inLocation(p) &&
    isRelativePath(p.path) &&
    isPage(p) &&
    isOneOf(p.sort, ['name', 'size', 'modified']) &&
    isOneOf(p.order, ['asc', 'desc']),
  'files.open': (p) => isRecord(p) && isLocation(p.location) && isRelativePath(p.path),
  'files.shares': isRecord,
  'files.watch': (p) => inLocation(p) && isRelativePath(p.path),
  'files.unwatch': (p) => isRecord(p) && typeof p.watch === 'string',
  'files.upload': (p) =>
    inLocation(p) &&
    isRelativePath(p.folder) &&
    isName(p.name) &&
    isBlob(p.data) &&
    (p.overwrite === undefined || typeof p.overwrite === 'boolean') &&
    (p.upload === undefined || typeof p.upload === 'string'),
  'files.download': (p) => inLocation(p) && isRelativePath(p.path),
  'files.createFolder': (p) => inLocation(p) && isRelativePath(p.path),
  'files.rename': (p) => inLocation(p) && isRelativePath(p.path) && isName(p.name),
  'files.run': isOperation,
  'files.versions': (p) => inLocation(p) && isRelativePath(p.path) && p.path !== '',
  'files.restoreVersion': (p) =>
    inLocation(p) &&
    isRelativePath(p.path) &&
    p.path !== '' &&
    isName(p.snapshot) &&
    (p.overwrite === undefined || typeof p.overwrite === 'boolean'),
  'jobs.cancel': (p) => isRecord(p) && Number.isSafeInteger(p.job),
  'trash.list': (p) => inLocation(p) && isPage(p),
  'trash.restore': (p) => inLocation(p) && isIds(p.ids) && isOneOf(p.onConflict, CONFLICTS),
  'trash.delete': (p) => inLocation(p) && isIds(p.ids),
  'trash.empty': inLocation,
  'ui.showError': (p) => isRecord(p) && isRecord(p.problem) && typeof p.problem.code === 'string',
};

function isMethod(name: string): name is MethodName {
  return Object.hasOwn(validators, name);
}

/** Serves one app window over a transport end. */
export function serveApp(endpoint: Endpoint, options: ServeOptions): AppConnection {
  let ready = false;
  let closed = false;
  let nextQuery = 1;
  const waiting = new Map<number, (answer: unknown) => void>();
  const capabilities = Object.keys(options.methods).filter(isMethod);

  async function answer(id: number, method: string, params: unknown) {
    try {
      const extension =
        options.extensions !== undefined && Object.hasOwn(options.extensions, method)
          ? options.extensions[method]
          : undefined;
      if (!isMethod(method) && extension !== undefined) {
        if (!extension.validate(params)) {
          throw sdkError('sdk.request.invalid', { method });
        }
        const result = await extension.handle(params);
        if (!closed) {
          endpoint.post({ type: 'response', id, ok: true, result });
        }
        return;
      }
      if (!isMethod(method)) {
        throw sdkError('sdk.method.unknown', { method });
      }
      const handler = options.methods[method] as ((params: unknown) => unknown) | undefined;
      if (handler === undefined) {
        throw sdkError('sdk.method.unknown', { method });
      }
      if (!validators[method](params)) {
        throw sdkError('sdk.request.invalid', { method });
      }
      const result = await handler(params);
      if (!closed) {
        endpoint.post({ type: 'response', id, ok: true, result });
      }
    } catch (error) {
      if (!closed) {
        endpoint.post({ type: 'response', id, ok: false, problem: toProblem(error) });
      }
    }
  }

  function handle(message: AppMessage) {
    if (message.type === 'hello') {
      if (ready) {
        return;
      }
      if (!isProtocolSupported(message.protocol, MIN_PROTOCOL_VERSION, PROTOCOL_VERSION)) {
        const problem = sdkError('sdk.protocol.unsupported', {
          protocol: message.protocol,
          min: MIN_PROTOCOL_VERSION,
          max: PROTOCOL_VERSION,
        }).toProblem();
        endpoint.post({ type: 'refuse', problem });
        connection.close();
        return;
      }
      ready = true;
      endpoint.post({
        type: 'init',
        protocol: Math.min(message.protocol, PROTOCOL_VERSION),
        context: { ...options.context, capabilities },
      });
      options.onReady?.({ protocol: message.protocol, sdk: message.sdk });
      return;
    }
    // Nothing but the handshake is accepted before the handshake.
    if (!ready) {
      return;
    }
    if (message.type === 'request') {
      void answer(message.id, message.method, message.params);
    } else {
      const resolve = waiting.get(message.id);
      waiting.delete(message.id);
      resolve?.(message.answer);
    }
  }

  const connection: AppConnection = {
    emit: (event, data) => {
      if (ready && !closed) {
        endpoint.post({ type: 'event', event, data });
      }
    },
    ask: (query, data, timeout) =>
      new Promise((resolve) => {
        if (!ready || closed) {
          resolve(undefined);
          return;
        }
        const id = nextQuery++;
        const timer = setTimeout(() => {
          waiting.delete(id);
          resolve(undefined);
        }, timeout);
        waiting.set(id, (value) => {
          clearTimeout(timer);
          resolve(value as never);
        });
        endpoint.post({ type: 'query', id, query, data });
      }),
    close: () => {
      if (closed) {
        return;
      }
      closed = true;
      for (const resolve of waiting.values()) {
        resolve(undefined);
      }
      waiting.clear();
      endpoint.close();
    },
  };

  endpoint.listen((message) => {
    if (!closed && isAppMessage(message)) {
      handle(message);
    }
  });
  return connection;
}

export { sdkError, PithyxError };
