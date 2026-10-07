import { createReadStream, existsSync, lstatSync } from 'node:fs';
import type { IncomingMessage, ServerResponse } from 'node:http';
import { dirname, extname, join } from 'node:path';
import type { Duplex } from 'node:stream';
import { fileURLToPath } from 'node:url';
import type { AppManifest } from '@pithyx/sdk';
import { TEMPLATE_ICON_PATH } from '../project';
import type { DevConfig } from './config';
import { frontendHeaders, serveFrontend } from './frontend';
import { proxyRequest, proxyUpgrade, sendProblem } from './proxy';
import { TokenStore } from './tokens';
import { directoryGroups, directoryUsers, MOCK_GROUPS, MOCK_USERS } from './users';

/** Where the mock desktop's own files and API live, next to the app under `/apps/<id>/`. */
export const SHELL_PREFIX = '/__pithyx/';

/** The built mock desktop, shipped in `dist/shell`. */
export const SHELL_FOLDER = join(dirname(fileURLToPath(import.meta.url)), 'shell');

/** How the app's frontend is served: a built folder like the box, or by the Vite dev server behind these headers. */
export type FrontendSource =
  { kind: 'folder'; folder: string } | { kind: 'vite'; scriptNonce: string };

export interface DevServerOptions {
  manifest: AppManifest;
  frontend: FrontendSource;
  /** The backend's endpoints by name; empty for an app without a backend or before it runs. */
  endpoints: () => ReadonlyMap<string, URL>;
  /** The app's `icon.svg`; the template icon while there is none. */
  icon?: string | undefined;
  tokens?: TokenStore;
  shellFolder?: string;
}

export interface DevServer {
  /** Handles a request; calls `next` for one that is not its own (only in front of the Vite dev server). */
  handle(req: IncomingMessage, res: ServerResponse, next?: () => void): void;
  /** Handles a WebSocket upgrade to the backend; false for one that is not its own. */
  upgrade(req: IncomingMessage, socket: Duplex, head: Buffer): boolean;
  tokens: TokenStore;
}

const SHELL_TYPES: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.woff2': 'font/woff2',
};

const MAX_BODY = 16 * 1024;

function readJson(req: IncomingMessage): Promise<unknown> {
  return new Promise((resolve, reject) => {
    let body = '';
    req.setEncoding('utf8');
    req.on('data', (chunk: string) => {
      body += chunk;
      if (body.length > MAX_BODY) {
        reject(new Error('too large'));
        req.destroy();
      }
    });
    req.on('end', () => {
      try {
        resolve(JSON.parse(body));
      } catch (failure) {
        reject(failure instanceof Error ? failure : new Error(String(failure)));
      }
    });
    req.on('error', reject);
  });
}

function sendJson(res: ServerResponse, value: unknown): void {
  res.writeHead(200, { 'content-type': 'application/json', 'cache-control': 'no-store' });
  res.end(JSON.stringify(value));
}

/** The URL of the app's entry: `frontend/index.html` is `/apps/<id>/index.html`. */
export function entryUrl(manifest: Pick<AppManifest, 'id' | 'entry'>): string {
  return `/apps/${manifest.id}/${manifest.entry.slice('frontend/'.length)}`;
}

/**
 * The server behind `pithyx dev`: the mock desktop at `/`, its API under `/__pithyx/`, the app's frontend under
 * `/apps/<id>/` with the box's headers, and the proxy to the backend under `/apps/<id>/api*`. Tokens and tickets come
 * only from the mock desktop's API, which answers same-origin requests with the header `X-Pithyx-Request` (like the
 * box's CSRF header), so neither the sandboxed app nor another site can mint them.
 */
export function createDevServer(options: DevServerOptions): DevServer {
  const { manifest } = options;
  const tokens = options.tokens ?? new TokenStore();
  const shellFolder = options.shellFolder ?? SHELL_FOLDER;
  const internet = manifest.permissions.includes('network.internet');
  const appPrefix = `/apps/${manifest.id}/`;
  const proxy = { appId: manifest.id, tokens, endpoints: options.endpoints };

  function serveShell(res: ServerResponse, path: string) {
    const file = join(shellFolder, ...(path === '' ? ['index.html'] : path.split('/')));
    const stat =
      path.split('/').every((part) => part !== '..' && part !== '.') && existsSync(file)
        ? lstatSync(file)
        : undefined;
    if (stat?.isFile() !== true) {
      res.writeHead(404, { 'content-type': 'text/plain' });
      res.end(
        existsSync(shellFolder) ? 'Not found' : 'The mock desktop is not built: run pnpm build.',
      );
      return;
    }
    res.writeHead(200, {
      'content-type': SHELL_TYPES[extname(file)] ?? 'application/octet-stream',
      'cache-control': 'no-cache',
      'x-content-type-options': 'nosniff',
      'x-frame-options': 'DENY',
    });
    createReadStream(file).pipe(res);
  }

  function serveIcon(res: ServerResponse) {
    const file =
      options.icon !== undefined && existsSync(options.icon) ? options.icon : TEMPLATE_ICON_PATH;
    // As the box serves icons: an SVG that can run nothing.
    res.writeHead(200, {
      'content-type': 'image/svg+xml',
      'content-security-policy': "default-src 'none'; sandbox",
      'x-content-type-options': 'nosniff',
      'cache-control': 'no-cache',
    });
    createReadStream(file).pipe(res);
  }

  async function shellApi(req: IncomingMessage, res: ServerResponse, name: string) {
    if (name === 'config' && req.method === 'GET') {
      const config: DevConfig = {
        manifest,
        entry: entryUrl(manifest),
        users: MOCK_USERS,
        groups: MOCK_GROUPS,
        directory: { users: directoryUsers(), groups: directoryGroups() },
      };
      sendJson(res, config);
      return;
    }
    const sameOrigin =
      req.headers.origin === undefined || req.headers.origin === `http://${req.headers.host ?? ''}`;
    if (req.method !== 'POST' || req.headers['x-pithyx-request'] !== '1' || !sameOrigin) {
      sendProblem(res, 403, 'auth.csrf.header_missing', 'Only the mock desktop may ask for this.');
      return;
    }
    let body: { user?: unknown; instance?: unknown; endpoint?: unknown };
    try {
      body = (await readJson(req)) as typeof body;
    } catch {
      sendProblem(res, 400, 'request.body.invalid', 'The body is not valid JSON.');
      return;
    }
    const user = MOCK_USERS.find((candidate) => candidate.id === body.user);
    if (user === undefined || typeof body.instance !== 'string') {
      sendProblem(res, 400, 'request.body.invalid', 'Name a user and a window instance.');
      return;
    }
    const holder = { user, instance: body.instance };
    if (name === 'tokens') {
      sendJson(res, tokens.issueToken(holder));
    } else if (name === 'tickets' && typeof body.endpoint === 'string') {
      if (!options.endpoints().has(body.endpoint)) {
        sendProblem(
          res,
          404,
          'request.route.not_found',
          `The app has no endpoint ${body.endpoint}.`,
        );
        return;
      }
      sendJson(res, tokens.issueTicket(holder, body.endpoint));
    } else if (name === 'signout') {
      tokens.revoke(user.id);
      sendJson(res, {});
    } else {
      sendProblem(res, 404, 'request.route.not_found', 'No such call.');
    }
  }

  return {
    tokens,
    handle(req, res, next) {
      const url = new URL(req.url ?? '/', 'http://localhost');
      if (url.pathname === '/' || url.pathname === '/index.html') {
        serveShell(res, '');
      } else if (url.pathname === `${SHELL_PREFIX}icon.svg`) {
        serveIcon(res);
      } else if (url.pathname.startsWith(`${SHELL_PREFIX}api/`)) {
        void shellApi(req, res, url.pathname.slice(`${SHELL_PREFIX}api/`.length));
      } else if (url.pathname.startsWith(SHELL_PREFIX)) {
        serveShell(res, url.pathname.slice(SHELL_PREFIX.length));
      } else if (proxyRequest(req, res, proxy)) {
        return;
      } else if (url.pathname.startsWith(appPrefix)) {
        if (options.frontend.kind === 'folder') {
          serveFrontend(
            req,
            res,
            url.pathname.slice(appPrefix.length),
            options.frontend.folder,
            internet,
          );
        } else {
          for (const [header, value] of Object.entries(
            frontendHeaders(internet, options.frontend.scriptNonce),
          )) {
            res.setHeader(header, value);
          }
          next?.();
        }
      } else if (next !== undefined) {
        next();
      } else {
        res.writeHead(404, { 'content-type': 'text/plain' }).end('Not found');
      }
    },
    upgrade: (req, socket, head) => proxyUpgrade(req, socket, head, proxy),
  };
}
