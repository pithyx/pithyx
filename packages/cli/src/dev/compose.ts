import { existsSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import type { AppManifest } from '@pithyx/sdk';
import { stringify } from 'yaml';
import { run } from '../docker';

/** The backend's source form with `build:` (D330); `pithyx dev` runs it instead of the pinned file when it exists. */
export const DEV_COMPOSE_FILE = 'backend/compose.dev.yaml';

/** The Compose project of an app under `pithyx dev`; the box names its own `pithyx-<id with _>`. */
export function devProject(appId: string): string {
  return `pithyx-dev-${appId.replace(/\./g, '_')}`;
}

/**
 * The override that makes each endpoint reachable from `pithyx dev`: its service's port published on 127.0.0.1 at a
 * port Docker picks. The box publishes nothing; its proxy reaches the containers on the app's internal network.
 */
export function endpointOverride(
  endpoints: NonNullable<AppManifest['backend']>['endpoints'],
): string {
  const services: Record<string, { ports: string[] }> = {};
  for (const { service, port } of Object.values(endpoints)) {
    const ports = (services[service] ??= { ports: [] }).ports;
    const published = `127.0.0.1::${String(port)}`;
    if (!ports.includes(published)) {
      ports.push(published);
    }
  }
  return stringify({ services });
}

/** The backend of an app running in the local Docker for `pithyx dev`. */
export interface DevBackend {
  endpoints: Map<string, URL>;
  stop(): Promise<void>;
}

/**
 * Starts the backend with `docker compose up --wait --build` (the source form `backend/compose.dev.yaml` when it
 * exists, else the app's pinned Compose file, plus the port override), and finds the published port of every endpoint. `stop` runs `docker compose down`
 * and keeps the volumes.
 */
export async function startBackend(root: string, manifest: AppManifest): Promise<DevBackend> {
  const backend = manifest.backend;
  if (backend === undefined) {
    throw new Error('the app has no backend');
  }
  const folder = mkdtempSync(join(tmpdir(), 'pithyx-dev-'));
  const override = join(folder, 'compose.ports.yaml');
  writeFileSync(override, endpointOverride(backend.endpoints));
  const files = [
    existsSync(join(root, DEV_COMPOSE_FILE))
      ? join(root, DEV_COMPOSE_FILE)
      : join(root, backend.compose),
    override,
  ];
  const compose = [
    'compose',
    '--project-name',
    devProject(manifest.id),
    ...files.flatMap((file) => ['--file', file]),
  ];
  console.log(`Starting the backend: docker ${compose.join(' ')} up --wait`);
  await run('docker', [...compose, 'up', '--detach', '--wait', '--build'], { inherit: true });
  const endpoints = new Map<string, URL>();
  for (const [name, { service, port }] of Object.entries(backend.endpoints)) {
    const published = (await run('docker', [...compose, 'port', service, String(port)])).trim();
    const hostPort = published.split('\n')[0]?.split(':').pop();
    if (hostPort === undefined || !/^\d+$/.test(hostPort)) {
      throw new Error(`docker compose port ${service} ${String(port)} gave no port: ${published}`);
    }
    endpoints.set(name, new URL(`http://127.0.0.1:${hostPort}/`));
  }
  return {
    endpoints,
    stop: async () => {
      try {
        await run('docker', [...compose, 'down'], { inherit: true });
      } finally {
        rmSync(folder, { recursive: true, force: true });
      }
    },
  };
}
