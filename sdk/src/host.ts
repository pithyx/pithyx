/**
 * The desktop's side of the protocol (`@vyxis/sdk/host`). The desktop serves every app window with one connection;
 * the connection is the app's identity, so a handler always knows which app and window called, whatever the message
 * claims. Apps do not use this module.
 */
import { sdkError, toProblem, VyxisError } from './errors';
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

export interface ServeOptions {
  /** The init context; `capabilities` is filled in from the handlers. */
  context: Omit<AppContext, 'capabilities'>;
  methods: MethodHandlers;
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

export { sdkError, VyxisError };
