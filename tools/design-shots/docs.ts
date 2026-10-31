/// <reference lib="dom" />
// Renders the website's screenshots from the design gallery: the real apps on invented sample data, so no picture shows
// a real box, user or network (website/CLAUDE.md). Each entry names the picture and the gallery screen it comes from;
// every picture is taken in English and German at 1440 x 900 with device scale factor 2 and written as WebP to
// website/src/assets/screenshots/<name>.webp and <name>.de.webp.
//
//   node tools/design-shots/docs.ts [--names store,files] [--base-url http://localhost:5174]
//
// Without --base-url the script starts its own design server and stops it afterwards, and the browser reaches it as
// http://pithyx:<port>, so addresses the apps build from the page's host (file services) name an invented box.
// Pictures of the installer, the sign-in and pithyx dev are not in the gallery and are taken from a test VM as before.
import { chromium } from '@playwright/test';
import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { openScreen, type Language, type Theme } from './matrix.ts';

const repoRoot = resolve(import.meta.dirname, '..', '..');
const outDirectory = join(repoRoot, 'website', 'src', 'assets', 'screenshots');

// sharp comes with the website's image pipeline.
const sharp = createRequire(join(repoRoot, 'website', 'package.json'))('sharp') as (
  input: Buffer,
) => { webp: (options: { quality: number }) => { toFile: (path: string) => Promise<unknown> } };

interface Shot {
  /** The picture's file name without language and extension. */
  name: string;
  /** The gallery screen. */
  screen: string;
  theme?: Theme;
}

export const shots: Shot[] = [
  { name: 'desktop', screen: 'desktop-windows' },
  { name: 'desktop-dark', screen: 'desktop-windows', theme: 'dark' },
  { name: 'desktop-menu', screen: 'start-menu' },
  { name: 'desktop-account-menu', screen: 'user-menu', theme: 'dark' },
  { name: 'theming-appearance', screen: 'user-menu-appearance', theme: 'dark' },
  { name: 'desktop-window-menu', screen: 'desktop-window-menu' },
  { name: 'files', screen: 'files-folder' },
  { name: 'files-recycle-bin', screen: 'files-trash' },
  { name: 'files-conflict', screen: 'files-conflict' },
  { name: 'settings', screen: 'settings-users' },
  { name: 'file-services', screen: 'settings-file-services' },
  { name: 'shared-folders-edit', screen: 'settings-share-edit' },
  { name: 'storage', screen: 'storage-pools' },
  { name: 'storage-disks', screen: 'storage-disks' },
  { name: 'storage-layout', screen: 'storage-wizard-layout' },
  { name: 'first-setup-encryption', screen: 'storage-wizard-encryption' },
  { name: 'personal-settings', screen: 'preferences-account' },
  { name: 'account-two-factor-qr', screen: 'preferences-two-factor' },
  { name: 'account-recovery-codes', screen: 'preferences-recovery-codes' },
  { name: 'i18n-language', screen: 'preferences-language' },
  { name: 'backup-checked', screen: 'settings-backup-checked' },
  { name: 'network-confirm', screen: 'settings-network-confirm' },
  { name: 'notifications-matrix', screen: 'settings-notification-matrix' },
  { name: 'logs-center', screen: 'logs-center', theme: 'dark' },
  { name: 'monitor-performance', screen: 'monitor-overview' },
  { name: 'store', screen: 'store-discover' },
  { name: 'store-update-settings', screen: 'store-updates' },
];

const { values: options } = parseArgs({
  options: {
    'base-url': { type: 'string' },
    names: { type: 'string' },
    port: { type: 'string', default: '5177' },
  },
});

async function waitForServer(url: string): Promise<void> {
  const deadline = Date.now() + 60_000;
  while (Date.now() < deadline) {
    try {
      if ((await fetch(url)).ok) {
        return;
      }
    } catch {
      // Not listening yet.
    }
    await new Promise((done) => setTimeout(done, 250));
  }
  throw new Error(`Design server did not answer at ${url}`);
}

const wanted = options.names?.split(',');
const unknown = (wanted ?? []).filter((name) => !shots.some((shot) => shot.name === name));
if (unknown.length > 0) {
  throw new Error(`Unknown pictures: ${unknown.join(', ')}`);
}
// The box's name in the pictures.
const HOST = 'pithyx';
let baseUrl = options['base-url'];
// Vite directly, in its own process group, so stopping it stops everything below it.
const server =
  baseUrl === undefined
    ? spawn(
        'node',
        [
          'node_modules/vite/bin/vite.js',
          '--host',
          '127.0.0.1',
          '--port',
          options.port,
          '--strictPort',
        ],
        {
          cwd: join(repoRoot, 'desktop'),
          detached: true,
          stdio: 'ignore',
          env: { ...process.env, __VITE_ADDITIONAL_SERVER_ALLOWED_HOSTS: HOST },
        },
      )
    : undefined;
baseUrl ??= `http://${HOST}:${options.port}`;

try {
  if (server !== undefined) {
    await waitForServer(`http://127.0.0.1:${options.port}/design.html`);
  }
  const browser = await chromium.launch({
    args: [`--host-resolver-rules=MAP ${HOST} 127.0.0.1`],
  });
  try {
    for (const shot of shots.filter((entry) => wanted?.includes(entry.name) ?? true)) {
      const theme = shot.theme ?? 'light';
      const context = await browser.newContext({
        viewport: { width: 1440, height: 900 },
        deviceScaleFactor: 2,
        colorScheme: theme,
        reducedMotion: 'reduce',
      });
      for (const lang of ['en', 'de'] as Language[]) {
        const page = await context.newPage();
        await openScreen(page, baseUrl, shot.screen, theme, lang);
        // No pointer over the page, so nothing shows a hover state.
        await page.mouse.move(0, 0);
        const file = join(outDirectory, `${shot.name}${lang === 'en' ? '' : '.de'}.webp`);
        await sharp(await page.screenshot())
          .webp({ quality: 82 })
          .toFile(file);
        console.log(`${shot.name} (${lang}): ${shot.screen}`);
        await page.close();
      }
      await context.close();
    }
  } finally {
    await browser.close();
  }
} finally {
  if (server?.pid !== undefined) {
    process.kill(-server.pid, 'SIGTERM');
  }
}
