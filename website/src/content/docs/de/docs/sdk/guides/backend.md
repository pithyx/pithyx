---
title: 'Das Backend aufrufen'
description: 'Das App-Backend mit app.backend erreichen: fetch, Token-URLs, WebSockets mit Tickets und die X-Pithyx-User-Header auf der Backend-Seite.'
status: ready
audience: developer
sidebar:
  order: 2
---

Das Fenster einer App erreicht sein eigenes Backend nur über den Proxy des Daemons und nur mit `app.backend`: `fetch` fügt ein Token für den angemeldeten Benutzer hinzu, `url` steckt es in eine URL für `<img>` und Downloads, und `socket` öffnet einen WebSocket mit einem Einmal-Ticket. Das Backend sieht das Token nie. Wer aufruft, erfährt es aus den `X-Pithyx-*`-Headern, die der Proxy setzt, und nur diesen Headern darf es trauen.

## Routen

Jeder Endpunkt in `backend.endpoints` des Manifests hat einen Namen. Der Standard-Endpunkt `api` liegt unter `/apps/<id>/api/`, jeder andere Endpunkt `<name>` unter `/apps/<id>/api-<name>/`. Pfade im SDK sind relativ zum Endpunkt: `app.backend.fetch('/messages')` erreicht `/messages` im Container. Gib `{ endpoint: 'metrics' }` für einen anderen Endpunkt mit. Container-Ports stehen nie in einer URL.

## Beispiel: das Fenster

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

## Beispiel: das Backend

Der Dienst `server` lauscht im Netzwerk der App auf Port 8080, wie es der Endpunkt im Manifest sagt. Er liest den Aufrufer aus den Headern:

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

## Was das Fenster bekommt

- **`fetch(path, init)`** sendet `Authorization: Bearer <token>` und keine Cookies. Es erfüllt sich mit jedem Status, prüfe also `response.ok`. Eine Anfrage, die die Box nicht erreicht, scheitert mit [`sdk.backend.unreachable`](/de/docs/reference/errors/#sdk.backend.unreachable); eine abgebrochene bleibt ein `AbortError`.
- **`url(path)`** liefert eine absolute URL mit `?pithyx-token=`. Sie funktioniert so lange wie ihr Token: mindestens 30 Sekunden, höchstens 5 Minuten. Frag neu, wenn du sie später setzt.
- **`socket(path, { protocols })`** holt beim Desktop ein Ticket und öffnet den Socket mit `?pithyx-ticket=` (`wss:` bei HTTPS, `ws:` bei reinem HTTP). Ein Ticket gilt 30 Sekunden, für eine Verbindung und einen Endpunkt. Jeder Aufruf holt ein neues Ticket, neu verbinden heißt also einfach erneut `socket` aufrufen.
- **`token()`** liefert `{ token, expiresAt }` für Clients, die ihre Anfragen selbst bauen. Ein Token gilt 5 Minuten für jeden Endpunkt der App. Das SDK nutzt es bis 30 Sekunden vor dem Ablauf und verwirft es bei `permissionsChange`.

Ein Pfad, der den Endpunkt verlassen würde (etwa mit `..`), scheitert mit [`sdk.request.invalid`](/de/docs/reference/errors/#sdk.request.invalid), damit ein Token nie woanders hingeht. `pithyx-token` und `pithyx-ticket` in deiner eigenen Query werden entfernt.

## Was das Backend bekommt

Der Proxy entfernt `Cookie`, `Authorization`, `Proxy-Authorization`, `Referer`, `Forwarded`, `traceparent`, `tracestate`, alle eingehenden `X-Pithyx-*`- und `X-Forwarded-*`-Header und alle `pithyx-*`-Query-Parameter. Dann setzt er:

| Header                               | Wert                                                                         |
| ------------------------------------ | ---------------------------------------------------------------------------- |
| `X-Pithyx-User`                      | der Benutzername, kleingeschrieben                                           |
| `X-Pithyx-User-Id`                   | die Benutzer-ID                                                              |
| `X-Pithyx-Groups`                    | die Namen der Gruppen des Benutzers, durch Kommas getrennt, leer ohne Gruppe |
| `X-Pithyx-Admin`                     | `true` oder `false`                                                          |
| `traceparent`                        | der Trace der Anfrage, für deine Logs                                        |
| `X-Forwarded-Prefix`                 | `/apps/<id>/api` oder `/apps/<id>/api-<name>`                                |
| `X-Forwarded-For`, `-Host`, `-Proto` | die Client-Adresse, der Host und `http` oder `https`                         |

WebSocket-Upgrades bekommen dieselben Header. Antworten verlieren `Set-Cookie`, `Clear-Site-Data`, `Strict-Transport-Security` und andere Header, die auf die ganze Box wirken, und bekommen die Sandbox-Header des Frontends. Request-Bodys haben am Proxy keine Größengrenze: Das Backend entscheidet. Die vollständige Liste steht unter [Backends](/de/docs/developers/apps/backend/#wer-ruft-auf).

## Sonderfälle

- **Gestopptes Backend.** Eine Anfrage an ein Backend im Zustand `stopped` oder `starting` weckt es und wartet bis zu 30 Sekunden. Solange es `updating` oder `unavailable` ist, antwortet der Proxy mit 503 [`app.backend.unavailable`](/de/docs/reference/errors/#app.backend.unavailable). `app.context.backend.status` und das Ereignis `backendStatus` sagen es dem Fenster.
- **Kein Backend.** `app.context.backend` ist `null`; `fetch`, `url` und `socket` scheitern mit [`sdk.backend.endpoint_unknown`](/de/docs/reference/errors/#sdk.backend.endpoint_unknown), `token()` mit [`app.backend.missing`](/de/docs/reference/errors/#app.backend.missing).
- **Unbekannter Endpunkt.** Ein Name, der nicht in `context.backend.endpoints` steht, scheitert mit `sdk.backend.endpoint_unknown`, bevor etwas gesendet wird. Endpunktnamen im Manifest haben höchstens 32 Zeichen.
- **401.** Der Proxy antwortet mit [`app.token.invalid`](/de/docs/reference/errors/#app.token.invalid), wenn Token oder Ticket falsch, abgelaufen, verbraucht oder für eine andere App sind, und wenn die Sitzung endete, der Benutzer sich nicht mehr am Desktop anmelden darf oder den Zugriff auf die App verlor. Jede Nutzung prüft das erneut. Ein Neustart der Box beendet alle Tokens. `fetch` fängt das selbst ab: Antwortet der Proxy mit `app.token.invalid`, verwirft das SDK sein Token, holt ein neues und wiederholt die Anfrage einmal (nicht bei einem Body, der ein Stream ist). Wird auch das neue Token abgelehnt, ist die 401 die Antwort, und erneut versuchen hilft nicht; das Fenster neu zu öffnen holt neue Tokens. `url` kann das nicht wissen: Frage eine neue URL an, wenn ein `<img>` nicht lädt. Ein WebSocket-Ticket wird bei jedem `socket`-Aufruf neu geholt.
- **Entzug.** Abmelden, ein Zurücksetzen des Passworts, das Deaktivieren des Benutzers oder das Entziehen der App schließt offene Sockets.
- **Ruhende Sockets** bleiben bis zu 24 Stunden ohne Verkehr offen. Der Proxy wartet höchstens 5 Sekunden auf die Verbindung zum Container.
- **CORS.** Der Proxy beantwortet Preflights aus dem Sandbox-Fenster selbst und ersetzt die eigenen CORS-Header des Backends.
- **Mock-Desktop.** `pithyx dev` setzt dieselben Header für den Benutzer, den du im Panel wählst; den Backend-Zustand im Panel zu ändern sendet aber nur das Ereignis, der Proxy leitet weiter.

## Fehlercodes

[`app.backend.unavailable`](/de/docs/reference/errors/#app.backend.unavailable), [`app.backend.missing`](/de/docs/reference/errors/#app.backend.missing), [`app.backend.endpoint_unknown`](/de/docs/reference/errors/#app.backend.endpoint_unknown), [`app.token.invalid`](/de/docs/reference/errors/#app.token.invalid), [`request.route.not_found`](/de/docs/reference/errors/#request.route.not_found), [`sdk.backend.endpoint_unknown`](/de/docs/reference/errors/#sdk.backend.endpoint_unknown), [`sdk.backend.unreachable`](/de/docs/reference/errors/#sdk.backend.unreachable), [`sdk.request.invalid`](/de/docs/reference/errors/#sdk.request.invalid), [`sdk.request.timeout`](/de/docs/reference/errors/#sdk.request.timeout).

## Grenzen

Ein Token lebt 5 Minuten, ein Ticket 30 Sekunden und einmal. Eine Desktop-Sitzung hält höchstens 512 Tokens und Tickets gleichzeitig; darüber antwortet die Box etwa eine Minute lang mit [`auth.rate.limited`](/de/docs/reference/errors/#auth.rate.limited). Ein Backend zu wecken wartet höchstens 30 Sekunden.
