import { randomBytes } from 'node:crypto';
import {
  request as httpRequest,
  type IncomingHttpHeaders,
  type IncomingMessage,
  type OutgoingHttpHeaders,
  type ServerResponse,
} from 'node:http';
import { request as httpsRequest } from 'node:https';
import type { Duplex } from 'node:stream';
import { frontendHeaders } from './frontend';
import type { Holder, TokenStore } from './tokens';

/** The query parameter with a backend token, and the one with a WebSocket ticket (as in `@pithyx/sdk`). */
export const TOKEN_PARAMETER = 'pithyx-token';
export const TICKET_PARAMETER = 'pithyx-ticket';

/** The request of the proxy that matched an endpoint: its name, the URL prefix and the path below it. */
export interface EndpointMatch {
  endpoint: string;
  /** `/apps/<id>/api` or `/apps/<id>/api-<name>`, without the trailing slash; sent as `X-Forwarded-Prefix`. */
  prefix: string;
  /** The path below the prefix, starting with `/`. */
  path: string;
}

const ENDPOINT = /^api(?:-([a-z][a-z0-9-]{0,62}))?$/;

/** Matches `/apps/<id>/api/...` (the endpoint `api`) and `/apps/<id>/api-<name>/...` (D303). */
export function matchEndpoint(pathname: string, appId: string): EndpointMatch | null {
  const base = `/apps/${appId}/`;
  if (!pathname.startsWith(base)) {
    return null;
  }
  const rest = pathname.slice(base.length);
  const slash = rest.indexOf('/');
  const segment = slash < 0 ? rest : rest.slice(0, slash);
  const match = ENDPOINT.exec(segment);
  if (match === null) {
    return null;
  }
  return {
    endpoint: match[1] ?? 'api',
    prefix: `${base}${segment}`,
    path: slash < 0 ? '/' : rest.slice(slash),
  };
}

/** The query without the `pithyx-*` parameters, in its original order and encoding; empty or starting with `?`. */
export function stripQuery(search: string): string {
  const kept = search
    .replace(/^\?/, '')
    .split('&')
    .filter((pair) => {
      if (pair === '') {
        return false;
      }
      const name = pair.split('=')[0] ?? '';
      let decoded: string;
      try {
        decoded = decodeURIComponent(name.replace(/\+/g, ' '));
      } catch {
        decoded = name;
      }
      return !decoded.toLowerCase().startsWith('pithyx-');
    });
  return kept.length === 0 ? '' : `?${kept.join('&')}`;
}

// Hop-by-hop headers (RFC 9110) and what the proxy sets itself.
const DROPPED = new Set([
  'connection',
  'keep-alive',
  'proxy-authenticate',
  'proxy-authorization',
  'proxy-connection',
  'te',
  'trailer',
  'transfer-encoding',
  'host',
  'cookie',
  'authorization',
  'forwarded',
  'traceparent',
  'tracestate',
  // A referrer may carry ?pithyx-token= of a page opened in a tab.
  'referer',
]);

/**
 * Answer headers that act on the whole box's origin rather than on the one answer (D323): cookies, wiping site data,
 * HSTS (which would end the click-through on the box's self-signed certificate), other protocols, reporting, a page
 * refresh and the backend's own CORS answers. The box drops them, and so does the mock.
 */
const ORIGIN_WIDE = new Set([
  'set-cookie',
  'set-cookie2',
  'clear-site-data',
  'strict-transport-security',
  'alt-svc',
  'refresh',
  'report-to',
  'reporting-endpoints',
  'nel',
  'public-key-pins',
  'public-key-pins-report-only',
  'expect-ct',
  'content-security-policy',
  'content-security-policy-report-only',
  'origin-agent-cluster',
  'service-worker-allowed',
  'access-control-allow-origin',
  'access-control-allow-credentials',
  'access-control-allow-methods',
  'access-control-allow-headers',
  'access-control-max-age',
]);

const TRACEPARENT = /^00-([0-9a-f]{32})-[0-9a-f]{16}-[0-9a-f]{2}$/;

/** A new span of the caller's trace, or a new trace. */
function traceparent(incoming: string | undefined): string {
  const trace = TRACEPARENT.exec(incoming ?? '')?.[1] ?? randomBytes(16).toString('hex');
  return `00-${trace}-${randomBytes(8).toString('hex')}-01`;
}

/**
 * The headers a backend gets: the browser's own without `Cookie`, `Authorization`, hop-by-hop headers and anything
 * that claims to come from Pithyx (`X-Pithyx-*`, `X-Forwarded-*`), plus the user the token belongs to and where the
 * request came from (D303, D316). `X-Pithyx-Groups` lists the group names separated by commas.
 */
export function backendHeaders(
  incoming: IncomingHttpHeaders,
  holder: Holder,
  match: EndpointMatch,
  client: { address: string; host: string; protocol: 'http' | 'https' },
  options: { upgrade?: boolean } = {},
): OutgoingHttpHeaders {
  const headers: OutgoingHttpHeaders = {};
  for (const [name, value] of Object.entries(incoming)) {
    const lower = name.toLowerCase();
    if (
      value === undefined ||
      DROPPED.has(lower) ||
      lower.startsWith('x-pithyx-') ||
      lower.startsWith('x-forwarded-') ||
      (lower === 'upgrade' && options.upgrade !== true)
    ) {
      continue;
    }
    headers[lower] = value;
  }
  if (options.upgrade === true) {
    headers.connection = 'Upgrade';
  }
  const incomingTrace = incoming.traceparent;
  headers.traceparent = traceparent(
    Array.isArray(incomingTrace) ? incomingTrace[0] : incomingTrace,
  );
  headers['x-pithyx-user'] = holder.user.name;
  headers['x-pithyx-user-id'] = String(holder.user.id);
  headers['x-pithyx-groups'] = holder.user.groups.map((group) => group.name).join(',');
  headers['x-pithyx-admin'] = holder.user.isAdmin ? 'true' : 'false';
  headers['x-forwarded-prefix'] = match.prefix;
  headers['x-forwarded-for'] = client.address;
  headers['x-forwarded-host'] = client.host;
  headers['x-forwarded-proto'] = client.protocol;
  return headers;
}

/**
 * The headers of a backend's answer for the app, as the box sends them (D323): without hop-by-hop headers and the ones
 * that act on the whole origin, readable from any origin, and with the frontend's sandboxing policy, so a backend's page
 * opened in a tab never runs in the box's origin.
 */
export function answerHeaders(
  incoming: IncomingHttpHeaders,
  internet = false,
): OutgoingHttpHeaders {
  const headers: OutgoingHttpHeaders = {};
  for (const [name, value] of Object.entries(incoming)) {
    const lower = name.toLowerCase();
    if (value === undefined || DROPPED.has(lower) || ORIGIN_WIDE.has(lower)) {
      continue;
    }
    headers[lower] = value;
  }
  // The backend decides how its answers are cached; the rest of the frontend's headers apply.
  for (const [name, value] of Object.entries(frontendHeaders(internet))) {
    if (name !== 'cache-control') {
      headers[name] = value;
    }
  }
  headers['access-control-expose-headers'] = '*';
  return headers;
}

/** The bearer token of a request: the `Authorization` header, else `?pithyx-token=`. */
export function bearerToken(
  headers: IncomingHttpHeaders,
  search: URLSearchParams,
): string | undefined {
  const header = headers.authorization;
  const match = /^Bearer ([A-Za-z0-9_-]+)$/.exec(header ?? '');
  return match?.[1] ?? search.get(TOKEN_PARAMETER) ?? undefined;
}

/** Writes a problem (RFC 9457) as the box does. */
export function sendProblem(
  res: ServerResponse,
  status: number,
  code: string,
  detail: string,
  params: Record<string, string> = {},
): void {
  res.writeHead(status, {
    ...frontendHeaders(false),
    'content-type': 'application/problem+json',
    'cache-control': 'no-store',
  });
  res.end(
    JSON.stringify({ status, code, params, detail, requestId: randomBytes(8).toString('hex') }),
  );
}

export interface ProxyOptions {
  appId: string;
  tokens: TokenStore;
  /** The base URL of every endpoint the backend serves, by name. */
  endpoints: () => ReadonlyMap<string, URL>;
  /** Whether the app holds `network.internet`, which widens the answers' `connect-src` as on the box. */
  internet?: boolean;
}

function clientOf(req: IncomingMessage) {
  return {
    address: req.socket.remoteAddress ?? '127.0.0.1',
    host: req.headers.host ?? 'localhost',
    protocol: 'http' as const,
  };
}

function target(base: URL, match: EndpointMatch, search: string): URL {
  const url = new URL(base);
  url.pathname = `${base.pathname.replace(/\/$/, '')}${match.path}`;
  url.search = stripQuery(search);
  return url;
}

function requestTo(url: URL) {
  return url.protocol === 'https:' ? httpsRequest : httpRequest;
}

const PREFLIGHT_METHODS = 'GET, HEAD, POST, PUT, PATCH, DELETE';

/**
 * Proxies a request under `/apps/<id>/api*` to the app's backend as the box does: preflights from the sandboxed
 * window (`Origin: null`) are answered here, everything else needs a backend token (bearer header or
 * `?pithyx-token=`; a cookie never counts) and reaches the backend with the user's headers. False when the path is
 * not the backend's.
 */
export function proxyRequest(
  req: IncomingMessage,
  res: ServerResponse,
  options: ProxyOptions,
): boolean {
  const url = new URL(req.url ?? '/', 'http://localhost');
  const match = matchEndpoint(url.pathname, options.appId);
  if (match === null) {
    return false;
  }
  if (req.method === 'OPTIONS' && req.headers['access-control-request-method'] !== undefined) {
    if (req.headers.origin !== 'null') {
      res.writeHead(403).end();
      return true;
    }
    res.writeHead(204, {
      'access-control-allow-origin': '*',
      'access-control-allow-methods': PREFLIGHT_METHODS,
      'access-control-allow-headers': req.headers['access-control-request-headers'] ?? '',
      'access-control-max-age': '600',
    });
    res.end();
    return true;
  }
  const base = options.endpoints().get(match.endpoint);
  if (base === undefined) {
    sendProblem(res, 404, 'request.route.not_found', `The app has no endpoint ${match.endpoint}.`);
    return true;
  }
  const token = bearerToken(req.headers, url.searchParams);
  const holder = token === undefined ? undefined : options.tokens.checkToken(token);
  if (holder === undefined) {
    sendProblem(res, 401, 'app.token.invalid', 'A valid backend token is required.');
    return true;
  }
  const upstream = target(base, match, url.search);
  const outgoing = requestTo(upstream)(upstream, {
    method: req.method ?? 'GET',
    headers: backendHeaders(req.headers, holder, match, clientOf(req)),
  });
  outgoing.on('response', (answer) => {
    res.writeHead(
      answer.statusCode ?? 502,
      answerHeaders(answer.headers, options.internet === true),
    );
    answer.pipe(res);
  });
  outgoing.on('error', () => {
    if (!res.headersSent) {
      sendProblem(res, 503, 'app.backend.unavailable', 'The backend does not answer.');
    } else {
      res.destroy();
    }
  });
  req.pipe(outgoing);
  return true;
}

/**
 * Proxies a WebSocket upgrade under `/apps/<id>/api*`: it needs a one-time ticket for that endpoint
 * (`?pithyx-ticket=`); tokens and cookies do not count. False when the path is not the backend's.
 */
export function proxyUpgrade(
  req: IncomingMessage,
  socket: Duplex,
  head: Buffer,
  options: ProxyOptions,
): boolean {
  const url = new URL(req.url ?? '/', 'http://localhost');
  const match = matchEndpoint(url.pathname, options.appId);
  if (match === null) {
    return false;
  }
  const refuse = (status: number, reason: string) => {
    socket.end(
      `HTTP/1.1 ${String(status)} ${reason}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n`,
    );
  };
  const base = options.endpoints().get(match.endpoint);
  const ticket = url.searchParams.get(TICKET_PARAMETER);
  const holder = ticket === null ? undefined : options.tokens.takeTicket(ticket, match.endpoint);
  if (base === undefined) {
    refuse(404, 'Not Found');
    return true;
  }
  if (holder === undefined) {
    refuse(401, 'Unauthorized');
    return true;
  }
  const upstream = target(base, match, url.search);
  const outgoing = requestTo(upstream)(upstream, {
    method: 'GET',
    headers: backendHeaders(req.headers, holder, match, clientOf(req), { upgrade: true }),
  });
  outgoing.on('upgrade', (answer, backend, backendHead) => {
    const lines = [`HTTP/1.1 101 ${answer.statusMessage ?? 'Switching Protocols'}`];
    for (let index = 0; index < answer.rawHeaders.length; index += 2) {
      const name = answer.rawHeaders[index] ?? '';
      if (name.toLowerCase() !== 'set-cookie') {
        lines.push(`${name}: ${answer.rawHeaders[index + 1] ?? ''}`);
      }
    }
    socket.write(`${lines.join('\r\n')}\r\n\r\n`);
    if (backendHead.length > 0) {
      socket.write(backendHead);
    }
    if (head.length > 0) {
      backend.write(head);
    }
    backend.pipe(socket).pipe(backend);
    // Either side going away ends the other.
    backend.on('close', () => socket.destroy());
    socket.on('close', () => backend.destroy());
    backend.on('error', () => socket.destroy());
    socket.on('error', () => backend.destroy());
  });
  outgoing.on('response', (answer) => {
    refuse(answer.statusCode ?? 502, answer.statusMessage ?? 'Bad Gateway');
    answer.resume();
  });
  outgoing.on('error', () => {
    refuse(503, 'Service Unavailable');
  });
  outgoing.end();
  return true;
}
