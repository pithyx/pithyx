---
title: 'Calling the backend'
description: 'Reach the app backend with app.backend: fetch, token URLs, WebSockets with tickets, and the X-Pithyx-User headers on the backend side.'
status: ready
audience: developer
sidebar:
  order: 2
---

An app's window reaches its own backend only through the daemon's proxy, and only with `app.backend`: `fetch` adds a token for the signed-in user, `url` puts it into a URL for `<img>` and downloads, and `socket` opens a WebSocket with a one-time ticket. The backend never sees the token. It learns who is calling from the `X-Pithyx-*` headers the proxy sets, and those headers are the only identity it may trust.

## Routes

Each endpoint in the manifest's `backend.endpoints` has a name. The default endpoint `api` is served at `/apps/<id>/api/`, any other endpoint `<name>` at `/apps/<id>/api-<name>/`. SDK paths are relative to the endpoint: `app.backend.fetch('/messages')` reaches `/messages` in the container. Pass `{ endpoint: 'metrics' }` for another endpoint. Container ports never appear in a URL.

## Example: the window

<!-- example: sdk/backend-frontend.ts -->

```ts
// The window side of a chat: JSON over app.backend.fetch, an avatar URL with a token, and a WebSocket with a ticket.
// pithyx.json declares "backend": { "endpoints": { "api": { "service": "server", "port": 8080 } }, ... }
import { isProblem, PithyxError, type PithyxApp } from '@pithyx/sdk';

export interface Message {
  id: number;
  user: string;
  text: string;
  sentAt: string;
}

/** Turns an answer that is not 2xx into a PithyxError; the proxy's own failures are RFC 9457 problems. */
async function failure(response: Response): Promise<PithyxError> {
  const body: unknown = await response.json().catch(() => null);
  return new PithyxError(
    isProblem(body)
      ? body
      : { code: 'app.backend.unavailable', params: {}, detail: `HTTP ${String(response.status)}` },
  );
}

export async function loadMessages(app: PithyxApp): Promise<Message[]> {
  const response = await app.backend.fetch('/messages', {
    headers: { Accept: 'application/json' },
  });
  if (!response.ok) {
    throw await failure(response);
  }
  return (await response.json()) as Message[];
}

export async function sendMessage(app: PithyxApp, text: string): Promise<Message> {
  const response = await app.backend.fetch('/messages', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ text }),
  });
  if (!response.ok) {
    throw await failure(response);
  }
  return (await response.json()) as Message;
}

/** An <img> source with ?pithyx-token=; it works as long as its token, so set it again after a few minutes. */
export async function showAvatar(app: PithyxApp, image: HTMLImageElement, user: string) {
  image.src = await app.backend.url(`/avatars/${encodeURIComponent(user)}`);
}

/**
 * Live messages. Every socket() call fetches a new one-time ticket, so a reconnect is just another call. While the
 * backend is updating or unavailable it waits for backendStatus; a stopped onDemand backend wakes for the request.
 */
export function followMessages(app: PithyxApp, onMessage: (message: Message) => void): () => void {
  let socket: WebSocket | null = null;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let connecting = false;
  let stopped = false;
  let delay = 1000;

  async function open() {
    clearTimeout(timer);
    const status = app.context.backend?.status;
    if (
      stopped ||
      connecting ||
      socket !== null ||
      status === 'updating' ||
      status === 'unavailable'
    ) {
      return;
    }
    connecting = true;
    try {
      socket = await app.backend.socket('/live');
    } catch (error) {
      app.log.warn('No live connection', {
        code: error instanceof PithyxError ? error.code : 'unknown',
      });
      retry();
      return;
    } finally {
      connecting = false;
    }
    socket.addEventListener('open', () => {
      delay = 1000;
    });
    socket.addEventListener('message', (event: MessageEvent<string>) => {
      onMessage(JSON.parse(event.data) as Message);
    });
    socket.addEventListener('close', retry);
  }

  function retry() {
    socket = null;
    if (!stopped) {
      timer = setTimeout(() => void open(), delay);
      delay = Math.min(delay * 2, 30_000);
    }
  }

  const stopStatus = app.on('backendStatus', ({ status }) => {
    if (status === 'running') {
      void open();
    }
  });
  void open();
  return () => {
    stopped = true;
    clearTimeout(timer);
    stopStatus();
    socket?.close();
  };
}
```

## Example: the backend

The service `server` listens on port 8080 inside the app's network, as the manifest's endpoint says. It reads the caller from the headers:

<!-- example: sdk/backend-server.ts -->

```ts
// The backend side of the chat: a Node HTTP server in the container of the service "server", port 8080. It trusts
// only the X-Pithyx-* headers the proxy sets; the proxy removed every such header the browser sent.
import { createServer, type IncomingMessage, type ServerResponse } from 'node:http';

interface Caller {
  name: string;
  id: number;
  groups: string[];
  admin: boolean;
}

interface Message {
  id: number;
  user: string;
  text: string;
  sentAt: string;
}

const MAX_BODY = 64 * 1024;
const messages: Message[] = [];

function header(req: IncomingMessage, name: string): string | undefined {
  const value = req.headers[name];
  return Array.isArray(value) ? value[0] : value;
}

/** The signed-in user the proxy checked, or null for a request that did not come through it. */
function callerOf(req: IncomingMessage): Caller | null {
  const name = header(req, 'x-pithyx-user');
  const id = Number(header(req, 'x-pithyx-user-id'));
  if (name === undefined || !Number.isSafeInteger(id)) {
    return null;
  }
  const groups = header(req, 'x-pithyx-groups') ?? '';
  return {
    name,
    id,
    groups: groups === '' ? [] : groups.split(','),
    admin: header(req, 'x-pithyx-admin') === 'true',
  };
}

function send(res: ServerResponse, status: number, body: unknown): void {
  res.writeHead(status, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify(body));
}

function readBody(req: IncomingMessage): Promise<string> {
  return new Promise((resolve, reject) => {
    let body = '';
    req.setEncoding('utf8');
    req.on('data', (chunk: string) => {
      body += chunk;
      // The proxy sets no size limit; the backend decides.
      if (body.length > MAX_BODY) {
        reject(new Error('too large'));
        req.destroy();
      }
    });
    req.on('end', () => {
      resolve(body);
    });
    req.on('error', reject);
  });
}

async function handle(req: IncomingMessage, res: ServerResponse): Promise<void> {
  const caller = callerOf(req);
  if (caller === null) {
    send(res, 401, { error: 'not through the proxy' });
    return;
  }
  // The path arrives without /apps/<id>/api; X-Forwarded-Prefix names it for links back to the box.
  const url = new URL(req.url ?? '/', 'http://backend');
  console.log(
    JSON.stringify({
      method: req.method,
      path: url.pathname,
      user: caller.name,
      traceparent: header(req, 'traceparent'),
    }),
  );
  if (url.pathname === '/messages' && req.method === 'GET') {
    send(res, 200, messages.slice(-100));
  } else if (url.pathname === '/messages' && req.method === 'POST') {
    const { text } = JSON.parse(await readBody(req)) as { text?: unknown };
    if (typeof text !== 'string' || text.length === 0 || text.length > 2000) {
      send(res, 400, { error: 'text' });
      return;
    }
    const message = {
      id: messages.length + 1,
      user: caller.name,
      text,
      sentAt: new Date().toISOString(),
    };
    messages.push(message);
    send(res, 201, message);
  } else if (url.pathname === '/messages' && req.method === 'DELETE') {
    if (!caller.admin) {
      send(res, 403, { error: 'administrators only' });
      return;
    }
    messages.length = 0;
    res.writeHead(204).end();
  } else {
    send(res, 404, { error: 'not found' });
  }
}

createServer((req, res) => {
  handle(req, res).catch(() => {
    if (!res.headersSent) {
      send(res, 400, { error: 'bad request' });
    }
  });
}).listen(8080);
```

## What the window gets

- **`fetch(path, init)`** sends `Authorization: Bearer <token>` and no cookies. It resolves with any status, so check `response.ok`. A request that does not reach the box fails with [`sdk.backend.unreachable`](/docs/reference/errors/#sdk.backend.unreachable); an aborted one stays an `AbortError`.
- **`url(path)`** returns an absolute URL with `?pithyx-token=`. It works as long as its token: at least 30 seconds, at most 5 minutes. Ask again when you set it later.
- **`socket(path, { protocols })`** asks the desktop for a ticket and opens the socket with `?pithyx-ticket=` (`wss:` on HTTPS, `ws:` on plain HTTP). A ticket is valid 30 seconds, for one connection and one endpoint. Every call gets a new ticket, so reconnecting is calling `socket` again.
- **`token()`** returns `{ token, expiresAt }` for clients that build their own requests. A token is valid 5 minutes for every endpoint of the app. The SDK reuses it until 30 seconds before it expires and drops it on `permissionsChange`.

A path that would leave the endpoint (for example with `..`) fails with [`sdk.request.invalid`](/docs/reference/errors/#sdk.request.invalid), so a token never goes elsewhere. `pithyx-token` and `pithyx-ticket` in your own query are removed.

## What the backend gets

The proxy removes `Cookie`, `Authorization`, `Proxy-Authorization`, `Referer`, `Forwarded`, `traceparent`, `tracestate`, every incoming `X-Pithyx-*` and `X-Forwarded-*` header and every `pithyx-*` query parameter. Then it sets:

| Header                               | Value                                                                                                         |
| ------------------------------------ | ------------------------------------------------------------------------------------------------------------- |
| `X-Pithyx-User`                      | the user name, lower case                                                                                     |
| `X-Pithyx-User-Id`                   | the user id: the Linux uid, the same as `context.user.id` and `DirectoryUser.id`, never given to another user |
| `X-Pithyx-Groups`                    | the names of the user's groups, comma separated, empty for none                                               |
| `X-Pithyx-Admin`                     | `true` or `false`                                                                                             |
| `traceparent`                        | the trace of the request, for your logs                                                                       |
| `X-Forwarded-Prefix`                 | `/apps/<id>/api` or `/apps/<id>/api-<name>`                                                                   |
| `X-Forwarded-For`, `-Host`, `-Proto` | the client address, the host and `http` or `https`                                                            |

WebSocket upgrades get the same headers. Answers lose `Set-Cookie`, `Clear-Site-Data`, `Strict-Transport-Security` and other headers that act on the whole box, and get the sandbox headers of the frontend. Request bodies have no size limit at the proxy: the backend decides. See [Backends](/docs/developers/apps/backend/#who-is-calling) for the full list.

## Edge cases

- **Stopped backend.** A request to a `stopped` or `starting` backend wakes it and waits up to 30 seconds. While it is `updating` or `unavailable` the proxy answers 503 [`app.backend.unavailable`](/docs/reference/errors/#app.backend.unavailable). `app.context.backend.status` and the `backendStatus` event tell the window. Load your data again when the status becomes `running`: a call made while the backend started may have failed.
- **No backend.** `app.context.backend` is `null`; `fetch`, `url` and `socket` fail with [`sdk.backend.endpoint_unknown`](/docs/reference/errors/#sdk.backend.endpoint_unknown), and `token()` with [`app.backend.missing`](/docs/reference/errors/#app.backend.missing).
- **Unknown endpoint.** A name that is not in `context.backend.endpoints` fails with `sdk.backend.endpoint_unknown` before anything is sent. Endpoint names in a manifest have at most 32 characters.
- **401.** The proxy answers [`app.token.invalid`](/docs/reference/errors/#app.token.invalid) when the token or ticket is wrong, expired, used up or for another app, and when the session ended, the user may no longer sign in to the desktop or lost access to the app. Every use checks this again. A restart of the box ends all tokens. `fetch` handles this itself: when the proxy answers `app.token.invalid`, the SDK drops its token, gets a new one and repeats the request once (not for a body that is a stream). If the new token is refused too, the 401 is the answer, and retrying does not help; reopening the window gets new tokens. `url` cannot know: ask again for a URL when an `<img>` fails to load. A WebSocket ticket is fetched anew for every `socket` call.
- **Revocation.** Signing out, a password reset, deactivating the user or revoking the app closes open sockets.
- **Idle sockets** stay open for up to 24 hours without traffic. The proxy's connect timeout to the container is 5 seconds.
- **CORS.** The proxy answers preflights from the sandboxed window itself and replaces the backend's own CORS headers.
- **Mock desktop.** `pithyx dev` sets the same headers for the user you pick in its panel, but changing the backend status in the panel only sends the event; the proxy keeps forwarding.

## Error codes

[`app.backend.unavailable`](/docs/reference/errors/#app.backend.unavailable), [`app.backend.missing`](/docs/reference/errors/#app.backend.missing), [`app.backend.endpoint_unknown`](/docs/reference/errors/#app.backend.endpoint_unknown), [`app.token.invalid`](/docs/reference/errors/#app.token.invalid), [`request.route.not_found`](/docs/reference/errors/#request.route.not_found), [`sdk.backend.endpoint_unknown`](/docs/reference/errors/#sdk.backend.endpoint_unknown), [`sdk.backend.unreachable`](/docs/reference/errors/#sdk.backend.unreachable), [`sdk.request.invalid`](/docs/reference/errors/#sdk.request.invalid), [`sdk.request.timeout`](/docs/reference/errors/#sdk.request.timeout).

## Limits

A token lives 5 minutes, a ticket 30 seconds and once. One desktop session holds at most 512 tokens and tickets at a time; beyond that the box answers [`auth.rate.limited`](/docs/reference/errors/#auth.rate.limited) for about a minute. Waking a backend waits at most 30 seconds.
