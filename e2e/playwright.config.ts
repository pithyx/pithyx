import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { defineConfig, devices } from '@playwright/test';

// A fresh daemon in simulation mode with its own database, serving the built desktop (pnpm build first).
// Playwright loads this file in every process; the environment variable keeps them on one directory.
process.env.VYXIS_E2E_DATA ??= mkdtempSync(join(tmpdir(), 'vyxis-e2e-'));
const dataDirectory = process.env.VYXIS_E2E_DATA;

export default defineConfig({
  testDir: 'tests',
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: 'http://localhost:8994',
    locale: 'en-US',
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: 'dotnet run --project ../daemon/src/Vyxis.Daemon --no-launch-profile',
    url: 'http://localhost:8994/api/health',
    timeout: 180_000,
    reuseExistingServer: false,
    env: {
      ASPNETCORE_ENVIRONMENT: 'Development',
      VYXIS_SIMULATE: '1',
      VYXIS_DB: join(dataDirectory, 'vyxis.db'),
      VYXIS_LOG_DIR: join(dataDirectory, 'logs'),
      VYXIS_DESKTOP_DIR: join(import.meta.dirname, '..', 'desktop', 'dist'),
    },
  },
});
