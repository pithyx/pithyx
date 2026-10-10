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
