import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect, request } from '@playwright/test';
import { admin } from './session';

/** The E2E probe app (e2e/apps/sdk-probe), a third-party app installed from its package. */
export const probeId = 'com.example.sdk-probe';

const probeDirectory = join(import.meta.dirname, '..', 'apps', 'sdk-probe');
const packagePath = join(probeDirectory, 'dist', 'sdk-probe.pithyxapp');

/**
 * Installs the probe on the test daemon like an administrator installs any app from a file: upload, check, install
 * job. Builds the package first when it is missing. Does nothing when the probe is installed already.
 */
export async function installProbe(baseURL: string) {
  if (!existsSync(packagePath)) {
    execFileSync('pnpm', ['--filter', '@pithyx/e2e-sdk-probe', 'build'], { stdio: 'inherit' });
  }
  const api = await request.newContext({ baseURL, extraHTTPHeaders: { 'X-Pithyx-Request': '1' } });
  try {
    const login = await api.post('/api/auth/login', {
      data: { username: admin.username, password: admin.password },
    });
    expect(login.ok()).toBe(true);
    const installed = (await (await api.get('/api/apps')).json()) as { apps: { id: string }[] };
    if (installed.apps.some((app) => app.id === probeId)) {
      return;
    }
    const upload = await api.put('/api/apps/packages', {
      data: readFileSync(packagePath),
      headers: { 'Content-Type': 'application/gzip' },
    });
    expect(upload.status(), await upload.text()).toBe(200);
    const { packageId } = (await upload.json()) as { packageId: string };
    const install = await api.post(`/api/apps/packages/${packageId}/install`, {
      data: { pool: 'pithyxtestm', access: 'everybody' },
    });
    expect(install.status(), await install.text()).toBe(202);
    const { id } = (await install.json()) as { id: number };
    await expect
      .poll(
        async () =>
          ((await (await api.get(`/api/jobs/${String(id)}`)).json()) as { state: string }).state,
      )
      .toBe('succeeded');
  } finally {
    await api.dispose();
  }
}
