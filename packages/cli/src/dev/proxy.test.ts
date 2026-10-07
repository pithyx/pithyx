import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createServer, request, type IncomingHttpHeaders, type Server } from 'node:http';
import type { AddressInfo } from 'node:net';
import type { Duplex } from 'node:stream';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import type { AppManifest } from '@pithyx/sdk';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { contentSecurityPolicy } from './frontend';
import { matchEndpoint, stripQuery } from './proxy';
import { createDevServer } from './server';
import { TOKEN_LIFETIME, TokenStore } from './tokens';
import { MOCK_USERS } from './users';

const appId = 'com.example.chat';
const manifest = {
  manifestVersion: 1,
  id: appId,
  version: '1.2.0',
  name: { en: 'Chat' },
  author: { name: 'Example' },
  entry: 'frontend/index.html',
  sdk: '^1.0.0',
  permissions: [],
  window: {
    instances: 'single',
    defaultSize: { width: 900, height: 640 },
    minSize: { width: 320, height: 400 },
  },
  backend: {
    compose: 'backend/compose.yaml',
    endpoints: {
      api: { service: 'server', port: 8080 },
      metrics: { service: 'server', port: 9090 },
    },
    lifecycle: 'onDemand',
  },
} as unknown as AppManifest;

const [alice, bob] = MOCK_USERS as [(typeof MOCK_USERS)[number], (typeof MOCK_USERS)[number]];

function listen(server: Server): Promise<string> {
  return new Promise((resolve) => {
    server.listen(0, '127.0.0.1', () => {
      resolve(`http://127.0.0.1:${String((server.address() as AddressInfo).port)}`);
    });
  });
}

let now = 1_000_000;
const tokens = new TokenStore(() => now);
let backend: Server;
let dev: Server;
let base = '';
let frontend = '';
let closedPort = '';
const upgraded: Duplex[] = [];

beforeAll(async () => {
  // A backend that answers with what it got.
  backend = createServer((req, res) => {
    res.writeHead(200, {
      'content-type': 'application/json',
      'set-cookie': 'session=evil; Path=/',
    });
    res.end(JSON.stringify({ method: req.method, url: req.url, headers: req.headers }));
  });
  backend.on('upgrade', (req, socket) => {
    socket.write(
      `HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n` +
        `X-Seen-User: ${String(req.headers['x-pithyx-user'])}\r\nX-Seen-Url: ${String(req.url)}\r\n\r\n`,
    );
    socket.on('data', (data: Buffer) => socket.write(data));
    socket.on('end', () => socket.end());
    upgraded.push(socket);
  });
  const backendUrl = await listen(backend);
  const unused = createServer();
  closedPort = await listen(unused);
  await new Promise((resolve) => unused.close(resolve));

  frontend = mkdtempSync(join(tmpdir(), 'pithyx-dev-test-'));
  mkdirSync(join(frontend, 'api'));
  writeFileSync(join(frontend, 'index.html'), '<!doctype html>');
  writeFileSync(join(frontend, 'api', 'secret.txt'), 'not served');

  const server = createDevServer({
    manifest,
    frontend: { kind: 'folder', folder: frontend },
    endpoints: () =>
      new Map([
        ['api', new URL(`${backendUrl}/`)],
        ['metrics', new URL(`${closedPort}/`)],
      ]),
    tokens,
  });
  dev = createServer((req, res) => {
    server.handle(req, res);
  });
  dev.on('upgrade', (req, socket, head: Buffer) => {
    if (!server.upgrade(req, socket, head)) {
      socket.destroy();
    }
  });
  base = await listen(dev);
});

afterAll(async () => {
  for (const socket of upgraded) {
    socket.destroy();
  }
  await Promise.all(
    [dev, backend].map((server) => {
      server.closeAllConnections();
      return new Promise((resolve) => server.close(resolve));
    }),
  );
  rmSync(frontend, { recursive: true, force: true });
});

async function echo(path: string, init: RequestInit = {}) {
  const response = await fetch(`${base}${path}`, init);
  return {
    response,
    body: (await response.json()) as { url: string; headers: IncomingHttpHeaders; code?: string },
  };
}

const token = (user = alice) => tokens.issueToken({ user, instance: 'w1' }).token;

describe('the routes of the backend', () => {
  it.each([
    [
      '/apps/com.example.chat/api/messages',
      { endpoint: 'api', prefix: '/apps/com.example.chat/api', path: '/messages' },
    ],
    [
      '/apps/com.example.chat/api',
      { endpoint: 'api', prefix: '/apps/com.example.chat/api', path: '/' },
    ],
    [
      '/apps/com.example.chat/api-metrics/a/b',
      { endpoint: 'metrics', prefix: '/apps/com.example.chat/api-metrics', path: '/a/b' },
    ],
    ['/apps/com.example.chat/index.html', null],
    ['/apps/com.example.chat/apis/x', null],
    ['/apps/com.example.chat/api-/x', null],
    ['/apps/com.example.other/api/x', null],
  ])('%s', (path, expected) => {
    expect(matchEndpoint(path, appId)).toEqual(expected);
  });

  it('drops the pithyx-* parameters and keeps the rest as written', () => {
    expect(stripQuery('?a=1&pithyx-token=x&b=%20&PITHYX-ticket=y&pithyx%2Dtoken=z')).toBe(
      '?a=1&b=%20',
    );
    expect(stripQuery('?pithyx-token=x')).toBe('');
  });
});

describe('the proxy of pithyx dev', () => {
  it('refuses a request without a token, also with a cookie', async () => {
    const { response, body } = await echo('/apps/com.example.chat/api/messages', {
      headers: { Cookie: 'pithyx_session=whatever' },
    });
    expect(response.status).toBe(401);
    expect(body.code).toBe('app.token.invalid');
  });

  it('hands the backend the user of the token and nothing the browser claims', async () => {
    const { response, body } = await echo(
      '/apps/com.example.chat/api/messages?since=3&pithyx-token=other',
      {
        headers: {
          Authorization: `Bearer ${token(bob)}`,
          Cookie: 'pithyx_session=secret',
          'X-Pithyx-User': 'alice',
          'X-Pithyx-Admin': 'true',
          'X-Forwarded-Prefix': '/elsewhere',
          Accept: 'application/json',
        },
      },
    );
    expect(response.status).toBe(200);
    expect(body.url).toBe('/messages?since=3');
    expect(body.headers).toMatchObject({
      'x-pithyx-user': 'bob',
      'x-pithyx-user-id': '2',
      'x-pithyx-groups': 'family,office',
      'x-pithyx-admin': 'false',
      'x-forwarded-prefix': '/apps/com.example.chat/api',
      accept: 'application/json',
    });
    expect(body.headers.cookie).toBeUndefined();
    expect(body.headers.authorization).toBeUndefined();
    expect(body.headers.traceparent).toMatch(/^00-[0-9a-f]{32}-[0-9a-f]{16}-01$/);
    // The backend's cookie never reaches the box's origin; any origin may read the answer.
    expect(response.headers.get('set-cookie')).toBeNull();
    expect(response.headers.get('access-control-allow-origin')).toBe('*');
  });

  it('takes the token from ?pithyx-token= for URLs without headers', async () => {
    const { response, body } = await echo(
      `/apps/com.example.chat/api/avatar/1?pithyx-token=${token()}`,
    );
    expect(response.status).toBe(200);
    expect(body.url).toBe('/avatar/1');
    expect(body.headers['x-pithyx-admin']).toBe('true');
  });

  it('refuses a token after 5 minutes', async () => {
    const old = token();
    now += TOKEN_LIFETIME;
    const { response } = await echo('/apps/com.example.chat/api/x', {
      headers: { Authorization: `Bearer ${old}` },
    });
    expect(response.status).toBe(401);
  });

  it('answers 503 app.backend.unavailable when the backend does not run', async () => {
    const { response, body } = await echo('/apps/com.example.chat/api-metrics/stats', {
      headers: { Authorization: `Bearer ${token()}` },
    });
    expect(response.status).toBe(503);
    expect(body.code).toBe('app.backend.unavailable');
  });

  it('answers preflights of the sandboxed window itself and no others', async () => {
    const preflight = (origin: string) =>
      fetch(`${base}/apps/com.example.chat/api/messages`, {
        method: 'OPTIONS',
        headers: {
          Origin: origin,
          'Access-Control-Request-Method': 'POST',
          'Access-Control-Request-Headers': 'authorization, content-type',
        },
      });
    const sandboxed = await preflight('null');
    expect(sandboxed.status).toBe(204);
    expect(sandboxed.headers.get('access-control-allow-headers')).toBe(
      'authorization, content-type',
    );
    expect((await preflight('https://evil.example')).status).toBe(403);
  });

  it('gives tokens only to the mock desktop', async () => {
    const ask = (headers: Record<string, string>) =>
      fetch(`${base}/__pithyx/api/tokens`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...headers },
        body: JSON.stringify({ user: 1, instance: 'w1' }),
      });
    expect((await ask({})).status).toBe(403);
    expect((await ask({ 'X-Pithyx-Request': '1', Origin: 'null' })).status).toBe(403);
    const granted = await ask({ 'X-Pithyx-Request': '1' });
    expect(granted.status).toBe(200);
    const { token: issued } = (await granted.json()) as { token: string };
    expect(Buffer.from(issued, 'base64url')).toHaveLength(32);
  });
});

function upgrade(
  path: string,
): Promise<{ status: number; headers: IncomingHttpHeaders; echo?: string }> {
  return new Promise((resolve, reject) => {
    const req = request(`${base}${path}`, {
      headers: {
        Connection: 'Upgrade',
        Upgrade: 'websocket',
        'Sec-WebSocket-Version': '13',
        Cookie: 'a=b',
      },
    });
    req.on('upgrade', (res, socket) => {
      socket.once('data', (data: Buffer) => {
        socket.destroy();
        resolve({ status: res.statusCode ?? 0, headers: res.headers, echo: data.toString() });
      });
      socket.write('ping');
    });
    req.on('response', (res) => {
      res.resume();
      resolve({ status: res.statusCode ?? 0, headers: res.headers });
    });
    req.on('error', reject);
    req.end();
  });
}

describe('WebSockets through the proxy', () => {
  const ticket = (endpoint = 'api') =>
    tokens.issueTicket({ user: alice, instance: 'w1' }, endpoint).ticket;

  it('need a ticket, which works once', async () => {
    const once = ticket();
    const first = await upgrade(`/apps/com.example.chat/api/live?pithyx-ticket=${once}&room=1`);
    expect(first.status).toBe(101);
    expect(first.echo).toBe('ping');
    expect(first.headers['x-seen-user']).toBe('alice');
    expect(first.headers['x-seen-url']).toBe('/live?room=1');
    expect((await upgrade(`/apps/com.example.chat/api/live?pithyx-ticket=${once}`)).status).toBe(
      401,
    );
  });

  it('refuse a token, a cookie and a ticket of another endpoint', async () => {
    expect((await upgrade(`/apps/com.example.chat/api/live?pithyx-token=${token()}`)).status).toBe(
      401,
    );
    expect(
      (await upgrade(`/apps/com.example.chat/api/live?pithyx-ticket=${ticket('metrics')}`)).status,
    ).toBe(401);
  });
});

describe('the frontend under pithyx dev', () => {
  it('has the policy of the box, word for word', () => {
    const source = readFileSync(
      join(
        dirname(fileURLToPath(import.meta.url)),
        '../../../../daemon/src/Pithyx.Daemon/Apps/AppFrontendHosting.cs',
      ),
      'utf8',
    );
    const declaration = /private const string Policy =([\s\S]*?");/.exec(source)?.[1] ?? '';
    const policy = [...declaration.matchAll(/"((?:[^"\\]|\\.)*)"/g)]
      .map((match) => match[1])
      .join('');
    expect(policy).not.toBe('');
    expect(contentSecurityPolicy(false)).toBe(policy);
    expect(contentSecurityPolicy(true)).toBe(`${policy} https:`);
  });

  it('serves files with the box headers, 404s included, and never the backend paths', async () => {
    const file = await fetch(`${base}/apps/com.example.chat/index.html`);
    expect(file.status).toBe(200);
    expect(file.headers.get('content-security-policy')).toBe(contentSecurityPolicy(false));
    expect(file.headers.get('x-content-type-options')).toBe('nosniff');
    expect(file.headers.get('access-control-allow-origin')).toBe('*');
    const missing = await fetch(`${base}/apps/com.example.chat/missing.js`);
    expect(missing.status).toBe(404);
    expect(missing.headers.get('content-security-policy')).toBe(contentSecurityPolicy(false));
    expect((await fetch(`${base}/apps/com.example.chat/%2e%2e/x`)).status).toBe(404);
    expect(
      (await fetch(`${base}/apps/com.example.chat/index.html`, { method: 'POST' })).status,
    ).toBe(405);
  });
});
