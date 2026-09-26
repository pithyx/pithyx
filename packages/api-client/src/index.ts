import createClient, { type Client, type Middleware } from 'openapi-fetch';
import type { ErrorCode } from './generated/error-codes';
import type { components, paths } from './generated/schema';

export { errorCodes, type ErrorCode } from './generated/error-codes';
export type { components, paths } from './generated/schema';

export type ApiProblem = components['schemas']['ApiProblem'];
export type ApiClient = Client<paths>;

const SAFE_METHODS = new Set(['GET', 'HEAD', 'OPTIONS']);

/** Header the daemon requires on state-changing requests (CSRF protection together with SameSite cookies). */
export const CSRF_HEADER = 'X-Vyxis-Request';

/** A failed API call. Show the translation of `code` with `params`, and offer `requestId` for support. */
export class VyxisApiError extends Error {
  readonly status: number;
  readonly code: ErrorCode;
  readonly params: Record<string, unknown>;
  readonly requestId: string;

  constructor(status: number, problem: ApiProblem) {
    super(problem.detail);
    this.name = 'VyxisApiError';
    this.status = status;
    this.code = problem.code;
    this.params = problem.params;
    this.requestId = problem.requestId;
  }
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

export function createApiClient(baseUrl = ''): ApiClient {
  const client = createClient<paths>({ baseUrl });
  client.use(csrf);
  return client;
}

/** Returns the data of an openapi-fetch result or throws a {@link VyxisApiError}. */
export function unwrap<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (result.error !== undefined || result.data === undefined) {
    const { response, error } = result;
    if (isProblem(error)) {
      throw new VyxisApiError(response.status, error);
    }
    throw new VyxisApiError(response.status, {
      type: 'about:blank',
      title: response.statusText,
      status: response.status,
      detail: `Unexpected response ${String(response.status)}`,
      code: 'system.internal.unexpected',
      params: {},
      requestId: response.headers.get('X-Request-Id') ?? '',
    });
  }
  return result.data;
}
