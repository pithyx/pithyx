import { createServer } from 'node:http';
import { join } from 'node:path';
import type { AppManifest } from '@pithyx/sdk';
import { readProject, type ProjectOptions } from '../project';
import { startBackend } from './compose';
import { createDevServer } from './server';

export interface BackendOptions {
  /** Endpoints to reach directly instead of through Docker, by name, for example `{ api: 'http://localhost:8080' }`. */
  endpoints?: Record<string, string> | undefined;
  /** False to leave Docker alone; endpoints not given then answer 503. */
  docker?: boolean | undefined;
}

/** The backend's endpoints for `pithyx dev`, and how to stop what was started for them. */
export async function devBackend(
  root: string,
  manifest: AppManifest,
  options: BackendOptions,
): Promise<{ endpoints: Map<string, URL>; stop: () => Promise<void> }> {
  const endpoints = new Map<string, URL>();
  const declared = Object.keys(manifest.backend?.endpoints ?? {});
  for (const [name, url] of Object.entries(options.endpoints ?? {})) {
    if (!declared.includes(name)) {
      throw new Error(
        `the manifest declares no endpoint ${name} (it has: ${declared.join(', ') || 'none'})`,
      );
    }
    endpoints.set(name, new URL(url));
  }
  const missing = declared.filter((name) => !endpoints.has(name));
  if (manifest.backend === undefined || missing.length === 0 || options.docker === false) {
    return { endpoints, stop: () => Promise.resolve() };
  }
  const started = await startBackend(root, manifest);
  for (const name of missing) {
    const url = started.endpoints.get(name);
    if (url !== undefined) {
      endpoints.set(name, url);
    }
  }
  return { endpoints, stop: () => started.stop() };
}

/** The port of `pithyx dev` without Vite. */
export const DEFAULT_PORT = 7400;

export interface DevOptions extends ProjectOptions, BackendOptions {
  port?: number | undefined;
  host?: string | undefined;
}

/**
 * `pithyx dev` without Vite: the mock desktop and the proxy on a local port, serving the built frontend as the box
 * would (rebuild and reload to see a change). Stops the backend on Ctrl+C.
 */
export async function runDev(root: string, options: DevOptions): Promise<void> {
  const project = readProject(root, options);
  const manifest = project.manifest;
  if (manifest === undefined) {
    throw new Error('pithyx.json is missing or not valid; run pithyx validate');
  }
  const backend = await devBackend(project.root, manifest, options);
  const dev = createDevServer({
    manifest,
    frontend: { kind: 'folder', folder: project.frontend },
    endpoints: () => backend.endpoints,
    icon: join(project.root, 'icon.svg'),
  });
  const server = createServer((req, res) => {
    dev.handle(req, res);
  });
  server.on('upgrade', (req, socket, head: Buffer) => {
    if (!dev.upgrade(req, socket, head)) {
      socket.destroy();
    }
  });
  const host = options.host ?? '127.0.0.1';
  const port = options.port ?? DEFAULT_PORT;
  await new Promise<void>((resolve, reject) => {
    server.once('error', reject);
    server.listen(port, host, resolve);
  });
  console.log(
    `Pithyx mock desktop: http://${host.includes(':') ? `[${host}]` : host}:${String(port)}/`,
  );
  console.log(
    `Serving ${project.frontend} as /apps/${manifest.id}/; rebuild and reload to see changes.`,
  );
  const stop = () => {
    server.close();
    void backend.stop().finally(() => process.exit(0));
  };
  process.once('SIGINT', stop);
  process.once('SIGTERM', stop);
}
