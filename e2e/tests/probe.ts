import { execFileSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, request } from '@playwright/test';
import { admin } from './session';

/** The E2E probe app (e2e/apps/sdk-probe), a third-party app installed from its package. */
export const probeId = 'com.example.sdk-probe';

const probeDirectory = join(import.meta.dirname, '..', 'apps', 'sdk-probe');
const packagePath = join(probeDirectory, 'dist', 'sdk-probe.pithyxapp');

/** The probe's package file, built first when it is missing. */
export function probePackage(): string {
  if (!existsSync(packagePath)) {
    execFileSync('pnpm', ['--filter', '@pithyx/e2e-sdk-probe...', 'build'], { stdio: 'inherit' });
  }
  return packagePath;
}

/**
 * The package of the root probe (e2e/apps/root-probe), an app whose backend asks for the unfiltered container service,
 * root on the box (D388). It has a static frontend, so `pithyx pack` builds it straight from its folder.
 */
export function rootProbePackage(): string {
  const project = join(import.meta.dirname, '..', 'apps', 'root-probe');
  const target = join(mkdtempSync(join(tmpdir(), 'pithyx-root-probe-')), 'root-probe.pithyxapp');
  execFileSync(
    join(probeDirectory, 'node_modules', '.bin', 'pithyx'),
    ['pack', project, '--frontend', 'frontend', '--out', target],
    { stdio: 'inherit' },
  );
  return target;
}

/** An API client signed in as the administrator, with the CSRF header; dispose it when done. */
async function adminApi(baseURL: string) {
  const api = await request.newContext({ baseURL, extraHTTPHeaders: { 'X-Pithyx-Request': '1' } });
  const login = await api.post('/api/auth/login', {
    data: { username: admin.username, password: admin.password },
  });
  expect(login.ok()).toBe(true);
  return api;
}

async function finished(api: Awaited<ReturnType<typeof adminApi>>, id: number) {
  await expect
    .poll(
      async () =>
        ((await (await api.get(`/api/jobs/${String(id)}`)).json()) as { state: string }).state,
    )
    .toBe('succeeded');
}

/** Removes the probe with its data, so a spec can install it through the Store. Does nothing when it is not installed. */
export async function uninstallProbe(baseURL: string) {
  const api = await adminApi(baseURL);
  try {
    const installed = (await (await api.get('/api/apps')).json()) as { apps: { id: string }[] };
    if (!installed.apps.some((app) => app.id === probeId)) {
      return;
    }
    const remove = await api.delete(`/api/apps/${probeId}?data=delete`);
    expect(remove.status(), await remove.text()).toBe(202);
    await finished(api, ((await remove.json()) as { id: number }).id);
  } finally {
    await api.dispose();
  }
}

/**
 * Installs the probe on the test daemon like an administrator installs any app from a file: upload, check, install
 * job. Builds the package first when it is missing. Does nothing when the probe is installed already.
 */
export async function installProbe(baseURL: string) {
  const file = probePackage();
  const api = await adminApi(baseURL);
  try {
    const installed = (await (await api.get('/api/apps')).json()) as { apps: { id: string }[] };
    if (installed.apps.some((app) => app.id === probeId)) {
      return;
    }
    const upload = await api.put('/api/apps/packages', {
      data: readFileSync(file),
      headers: { 'Content-Type': 'application/gzip' },
    });
    expect(upload.status(), await upload.text()).toBe(200);
    const { packageId } = (await upload.json()) as { packageId: string };
    const install = await api.post(`/api/apps/packages/${packageId}/install`, {
      data: { pool: 'pithyxtestm', access: 'everybody' },
    });
    expect(install.status(), await install.text()).toBe(202);
    await finished(api, ((await install.json()) as { id: number }).id);
  } finally {
    await api.dispose();
  }
}
