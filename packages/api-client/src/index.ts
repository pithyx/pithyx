import createClient, { type Client, type Middleware } from 'openapi-fetch';
import type { ErrorCode } from './generated/error-codes';
import type { components, paths } from './generated/schema';

export { errorCodes, type ErrorCode } from './generated/error-codes';
export type { components, paths } from './generated/schema';

export type ApiProblem = components['schemas']['ApiProblem'];

/** Messages of the WebSocket at `/ws`. */
export type ClientMessage = components['schemas']['ClientMessage'];
export type ServerMessage = components['schemas']['ServerMessage'];
export type TopicSnapshots = components['schemas']['TopicSnapshots'];
export type TopicEvents = components['schemas']['TopicEvents'];
/** A topic the daemon publishes, or the pattern of a topic family. */
export type TopicName = keyof TopicSnapshots;

export { closeCodes, type CloseCodeName } from './generated/close-codes';
export { closeAction, closeActions, type CloseAction } from './realtime';
export type ApiClient = Client<paths>;

const SAFE_METHODS = new Set(['GET', 'HEAD', 'OPTIONS']);

/** Header the daemon requires on state-changing requests (CSRF protection together with SameSite cookies). */
export const CSRF_HEADER = 'X-Pithyx-Request';

/** A failed API call. Show the translation of `code` with `params`, and offer `requestId` for support. */
export class PithyxApiError extends Error {
  readonly status: number;
  readonly code: ErrorCode;
  readonly params: Record<string, unknown>;
  readonly requestId: string;

  constructor(status: number, problem: ApiProblem) {
    super(problem.detail);
    this.name = 'PithyxApiError';
    this.status = status;
    this.code = problem.code;
    this.params = problem.params;
    this.requestId = problem.requestId;
  }
}

/**
 * A request that never reached the box: the connection is lost or the box is restarting. It carries the SDK's code
 * `sdk.box.unreachable`, so an app sees the same code, and nothing about it is in the box's logs.
 */
export class BoxUnreachableError extends Error {
  readonly code = 'sdk.box.unreachable';
  readonly params: Record<string, unknown> = {};

  constructor(cause?: unknown) {
    super('The box could not be reached', { cause });
    this.name = 'BoxUnreachableError';
  }
}

/** What a failed `fetch` means: an aborted request stays as it is, anything else did not reach the box. */
export function unreachableError(error: unknown): unknown {
  return error instanceof DOMException && error.name === 'AbortError'
    ? error
    : new BoxUnreachableError(error);
}

function isProblem(value: unknown): value is ApiProblem {
  return typeof value === 'object' && value !== null && 'code' in value && 'requestId' in value;
}

const csrf: Middleware = {
  onRequest({ request }) {
    if (!SAFE_METHODS.has(request.method)) {
      request.headers.set(CSRF_HEADER, '1');
    }
    return request;
  },
};

const unreachable: Middleware = {
  onError({ error }) {
    const mapped = unreachableError(error);
    return mapped instanceof Error ? mapped : undefined;
  },
};

export function createApiClient(baseUrl = ''): ApiClient {
  const client = createClient<paths>({ baseUrl });
  client.use(csrf, unreachable);
  return client;
}

/**
 * Returns the data of an openapi-fetch result or throws a {@link PithyxApiError}. A successful response without a
 * body (204) returns undefined, which its type already says.
 */
export function unwrap<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (result.error !== undefined || !result.response.ok) {
    const { response, error } = result;
    if (isProblem(error)) {
      throw new PithyxApiError(response.status, error);
    }
    throw new PithyxApiError(response.status, {
      type: 'about:blank',
      title: response.statusText,
      status: response.status,
      detail: `Unexpected response ${String(response.status)}`,
      code: 'system.internal.unexpected',
      params: {},
      requestId: response.headers.get('X-Request-Id') ?? '',
    });
  }
  return result.data as T;
}
