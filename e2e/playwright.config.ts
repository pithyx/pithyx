import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { defineConfig, devices } from '@playwright/test';

// A fresh daemon in simulation mode with its own database, serving the built desktop (pnpm build first).
// Playwright loads this file in every process; the environment variable keeps them on one directory.
process.env.PITHYX_E2E_DATA ??= mkdtempSync(join(tmpdir(), 'pithyx-e2e-'));
const dataDirectory = process.env.PITHYX_E2E_DATA;

// Chromium resolves pithyx.test to this machine, so the daemon is reached at an origin that is not a secure context.
const insecureOrigin = {
  baseURL: 'http://pithyx.test:7484',
  launchOptions: { args: ['--host-resolver-rules=MAP pithyx.test 127.0.0.1'] },
};

export default defineConfig({
  testDir: 'tests',
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: 'http://localhost:7484',
    locale: 'en-US',
    // Motion collapses to 1 ms, so geometry is final when a test measures it; this also covers the reduced-motion path.
    reducedMotion: 'reduce',
    trace: 'retain-on-failure',
  },
  projects: [
    // The first-run flow needs the fresh database; every other spec signs in as the administrator it created. It runs
    // on a host name that is not localhost, like a box reached by its address over plain HTTP: no secure context.
    {
      name: 'first-run',
      testMatch: 'auth.spec.ts',
      use: { ...devices['Desktop Chrome'], ...insecureOrigin },
    },
    {
      name: 'insecure-origin',
      testMatch: 'insecure.spec.ts',
      dependencies: ['first-run'],
      use: { ...devices['Desktop Chrome'], ...insecureOrigin },
    },
    {
      name: 'chromium',
      testIgnore: ['auth.spec.ts', 'insecure.spec.ts'],
      dependencies: ['first-run'],
      use: { ...devices['Desktop Chrome'] },
    },
  ],
  webServer: {
    command: 'dotnet run --project ../daemon/src/Pithyx.Daemon --no-launch-profile',
    url: 'http://localhost:7484/api/health',
    timeout: 180_000,
    reuseExistingServer: false,
    env: {
      ASPNETCORE_ENVIRONMENT: 'Development',
      PITHYX_SIMULATE: '1',
      PITHYX_DB: join(dataDirectory, 'pithyx.db'),
      PITHYX_CACHE_DIR: join(dataDirectory, 'cache'),
      PITHYX_LOG_DIR: join(dataDirectory, 'logs'),
      PITHYX_DESKTOP_DIR: join(import.meta.dirname, '..', 'desktop', 'dist'),
    },
  },
});
