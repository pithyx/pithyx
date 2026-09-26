import { defineConfig, devices } from '@playwright/test';

// Layout checks for the design gallery. Starts the Vite design server; no daemon needed.
const port = 5176;

export default defineConfig({
  testDir: '.',
  testMatch: '*.spec.ts',
  outputDir: 'test-results',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: 0,
  reporter: 'list',
  use: {
    ...devices['Desktop Chrome'],
    baseURL: `http://127.0.0.1:${String(port)}`,
  },
  webServer: {
    // Vite directly, not through pnpm: pnpm does not pass the stop signal on, and the orphaned server kept the
    // test run from exiting.
    command: `node node_modules/vite/bin/vite.js --host 127.0.0.1 --port ${String(port)} --strictPort`,
    cwd: '../../desktop',
    url: `http://127.0.0.1:${String(port)}/design.html`,
    timeout: 120_000,
    reuseExistingServer: false,
  },
});
