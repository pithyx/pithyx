/**
 * Addresses of an app's backend. The default endpoint `api` is served at `/apps/<id>/api/`, every other
 * endpoint `<name>` at `/apps/<id>/api-<name>/`; container ports never appear in a URL. The box's address comes from
 * the window's own URL, so the same app works on 443 and on 7484.
 */
import { sdkError } from './errors';

/** The endpoint an app reaches when it names none. */
export const DEFAULT_ENDPOINT = 'api';

/** The query parameter that carries a backend token, for URLs used where no header can be set (`<img src>`). */
export const TOKEN_PARAMETER = 'pithyx-token';

/** The query parameter that carries a one-time WebSocket ticket. */
export const TICKET_PARAMETER = 'pithyx-ticket';

const ENDPOINT_NAME = /^[a-z][a-z0-9-]{0,62}$/;

/** The base path of an endpoint, with a trailing slash. */
export function endpointPath(appId: string, endpoint: string = DEFAULT_ENDPOINT): string {
  if (!ENDPOINT_NAME.test(endpoint)) {
    throw sdkError('sdk.backend.endpoint_unknown', { endpoint });
  }
  const app = encodeURIComponent(appId);
  return endpoint === DEFAULT_ENDPOINT ? `/apps/${app}/api/` : `/apps/${app}/api-${endpoint}/`;
}

/**
 * The absolute URL of `path` (with an optional query) on an endpoint. A path that would leave the endpoint, for
 * example with `..`, is refused, so a token is never sent anywhere else.
 */
export function backendUrl(
  origin: string,
  appId: string,
  path: string,
  endpoint: string = DEFAULT_ENDPOINT,
): URL {
  const base = endpointPath(appId, endpoint);
  const url = new URL(base + path.replace(/^\/+/, ''), origin);
  if (!url.pathname.startsWith(base) || url.origin !== new URL(origin).origin) {
    throw sdkError('sdk.request.invalid', { method: 'backend', path });
  }
  for (const name of [TOKEN_PARAMETER, TICKET_PARAMETER]) {
    if (url.searchParams.has(name)) {
      url.searchParams.delete(name);
    }
  }
  return url;
}

/** The same URL for a WebSocket: `wss:` on HTTPS, `ws:` on plain HTTP. */
export function socketUrl(url: URL): URL {
  const socket = new URL(url);
  socket.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
  return socket;
}

/** The box's address, taken from the window's own URL; a sandboxed frame keeps its URL though its origin is opaque. */
export function windowOrigin(): string {
  if (typeof location === 'undefined') {
    throw sdkError('sdk.transport.closed', {}, 'No window to take the box address from');
  }
  return new URL(location.href).origin;
}
