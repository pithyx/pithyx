import { createReadStream, lstatSync } from 'node:fs';
import type { IncomingMessage, ServerResponse } from 'node:http';
import { extname, join } from 'node:path';
import { isReservedFrontendPath, isSafePath } from '../layout';

/**
 * The policy of an app's frontend, word for word the box's (`AppFrontendHosting.cs`, D303): the sandbox keeps a page
 * opened directly in a tab in an opaque origin; `connect-src https:` only for an app holding `network.internet`.
 */
const POLICY =
  "sandbox allow-scripts allow-forms; default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
  "img-src 'self' data: blob:; font-src 'self' data:; media-src 'self' data: blob:; worker-src 'self' blob:; " +
  "object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'self'; connect-src 'self'";

/**
 * The `Content-Security-Policy` of an app's frontend. `scriptNonce` only for the Vite dev server, whose injected
 * scripts carry it; the built frontend never needs one.
 */
export function contentSecurityPolicy(internet: boolean, scriptNonce?: string): string {
  const policy = internet ? `${POLICY} https:` : POLICY;
  return scriptNonce === undefined
    ? policy
    : policy.replace("script-src 'self'", `script-src 'self' 'nonce-${scriptNonce}'`);
}

/** The headers of every answer under `/apps/<id>/`, as the box sends them. */
export function frontendHeaders(internet: boolean, scriptNonce?: string): Record<string, string> {
  return {
    'content-security-policy': contentSecurityPolicy(internet, scriptNonce),
    'x-content-type-options': 'nosniff',
    'access-control-allow-origin': '*',
    'cache-control': 'no-cache',
    'referrer-policy': 'no-referrer',
  };
}

const TYPES: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json',
  '.map': 'application/json',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.gif': 'image/gif',
  '.webp': 'image/webp',
  '.avif': 'image/avif',
  '.ico': 'image/x-icon',
  '.woff': 'font/woff',
  '.woff2': 'font/woff2',
  '.ttf': 'font/ttf',
  '.otf': 'font/otf',
  '.wasm': 'application/wasm',
  '.txt': 'text/plain',
  '.mp3': 'audio/mpeg',
  '.mp4': 'video/mp4',
  '.webm': 'video/webm',
};

/**
 * Serves a built frontend at `/apps/<id>/<path>` like the box: the headers on every answer, 404s included; GET and
 * HEAD only; paths that a package could not contain, `api` and `api-*`, folders and links are 404. Files are read on
 * every request, so a new build shows on the next reload.
 */
export function serveFrontend(
  req: IncomingMessage,
  res: ServerResponse,
  path: string,
  folder: string,
  internet: boolean,
): void {
  for (const [name, value] of Object.entries(frontendHeaders(internet))) {
    res.setHeader(name, value);
  }
  if (req.method !== 'GET' && req.method !== 'HEAD') {
    res.writeHead(405, { allow: 'GET, HEAD' }).end();
    return;
  }
  let decoded: string;
  try {
    decoded = decodeURIComponent(path);
  } catch {
    decoded = '';
  }
  const file = join(folder, ...decoded.split('/'));
  const stat =
    isSafePath(decoded) && !isReservedFrontendPath(decoded)
      ? lstatSync(file, { throwIfNoEntry: false })
      : undefined;
  if (stat?.isFile() !== true) {
    res.writeHead(404).end();
    return;
  }
  res.writeHead(200, {
    'content-type': TYPES[extname(file).toLowerCase()] ?? 'application/octet-stream',
    'content-length': String(stat.size),
  });
  if (req.method === 'HEAD') {
    res.end();
  } else {
    createReadStream(file).pipe(res);
  }
}
