/// <reference lib="dom" />
// Renders every design gallery screen at full size as PNG: <screen>_<viewport>_<theme>_<lang>.png.
// Basis for visual regression tests later. Screenshots use reduced motion, so entrance animations are finished and
// every run captures the same frame.
//
//   pnpm design:shots [--base-url http://localhost:5174] [--screens login,tokens] [--out tools/design-shots/out]
//
// Without --base-url the script starts its own design server and stops it afterwards.
import { chromium, type Browser, type BrowserContext } from '@playwright/test';
import { spawn, type ChildProcess } from 'node:child_process';
import { mkdirSync, rmSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';
import { parseArgs } from 'node:util';

const repoRoot = resolve(import.meta.dirname, '..', '..');

// Sizes and input types match the Playwright devices in desktop/CLAUDE.md.
const viewports = {
  phone: { width: 390, height: 844, touch: true },
  tablet: { width: 820, height: 1180, touch: true },
  laptop: { width: 1440, height: 900, touch: false },
} as const;
const themes = ['light', 'dark'] as const;
const languages = ['en', 'de'] as const;

type Viewport = keyof typeof viewports;

interface Job {
  screen: string;
  viewport: Viewport;
  theme: (typeof themes)[number];
  lang: (typeof languages)[number];
}

const { values: options } = parseArgs({
  options: {
    'base-url': { type: 'string' },
    screens: { type: 'string' },
    out: { type: 'string', default: join('tools', 'design-shots', 'out') },
    port: { type: 'string', default: '5175' },
    workers: { type: 'string', default: '4' },
  },
});

async function waitForServer(url: string, timeoutMs: number): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url);
      if (response.ok) {
        return;
      }
    } catch {
      // Not listening yet.
    }
    await new Promise((done) => setTimeout(done, 250));
  }
  throw new Error(`Design server did not answer at ${url}`);
}

function startServer(port: string): ChildProcess {
  // Own process group, so stopping it also stops vite below pnpm.
  return spawn(
    'pnpm',
    ['--filter', '@pithyx/desktop', 'exec', 'vite', '--port', port, '--strictPort'],
    { cwd: repoRoot, detached: true, stdio: 'ignore' },
  );
}

function stopServer(server: ChildProcess) {
  if (server.pid !== undefined) {
    try {
      process.kill(-server.pid, 'SIGTERM');
    } catch {
      // Already gone.
    }
  }
}

/** Reads the screen ids from the gallery itself, so the list never drifts from the app. */
async function screenIds(browser: Browser, baseUrl: string): Promise<string[]> {
  const page = await browser.newPage();
  await page.goto(`${baseUrl}/design.html`);
  const ids = await page.evaluate(async () => {
    const path = '/src/design/screens.tsx';
    const module = (await import(path)) as { screens: { id: string }[] };
    return module.screens.map((screen) => screen.id);
  });
  await page.close();
  return ids;
}

async function render(
  context: BrowserContext,
  baseUrl: string,
  job: Job,
  outDirectory: string,
): Promise<string[]> {
  const problems: string[] = [];
  const page = await context.newPage();
  page.on('console', (message) => {
    if (message.type() === 'error' || message.type() === 'warning') {
      problems.push(message.text());
    }
  });
  page.on('pageerror', (error) => problems.push(error.message));
  const params = new URLSearchParams({ screen: job.screen, theme: job.theme, lang: job.lang });
  await page.goto(`${baseUrl}/design.html?${params.toString()}`);
  await page.evaluate(() => document.fonts.ready.then(() => undefined));
  // Screens of real apps run a short script (open a dialog, fill it) and mark the page busy until it is done.
  await page.waitForFunction(
    () => document.querySelector('[data-design-busy]') === null,
    undefined,
    {
      timeout: 10_000,
    },
  );
  // The loading hint on the welcome screen appears after 300 ms; the simulated loading ends after 1.4 s.
  await page.waitForTimeout(800);
  const name = `${job.screen}_${job.viewport}_${job.theme}_${job.lang}.png`;
  await page.screenshot({ path: join(outDirectory, name), fullPage: true });
  await page.close();
  return problems.map((problem) => `${name}: ${problem}`);
}

async function main() {
  const outDirectory = resolve(repoRoot, options.out);
  const port = options.port;
  let baseUrl = options['base-url'];
  let server: ChildProcess | undefined;
  if (baseUrl === undefined) {
    baseUrl = `http://localhost:${port}`;
    server = startServer(port);
  }

  try {
    await waitForServer(`${baseUrl}/design.html`, 60_000);
    const browser = await chromium.launch();
    try {
      const available = await screenIds(browser, baseUrl);
      const requested = options.screens?.split(',') ?? available;
      const unknown = requested.filter((id) => !available.includes(id));
      if (unknown.length > 0) {
        throw new Error(`Unknown screens: ${unknown.join(', ')}`);
      }

      rmSync(outDirectory, { recursive: true, force: true });
      mkdirSync(outDirectory, { recursive: true });

      const contexts = new Map<string, BrowserContext>();
      for (const [viewport, size] of Object.entries(viewports)) {
        for (const theme of themes) {
          contexts.set(
            `${viewport}-${theme}`,
            await browser.newContext({
              viewport: { width: size.width, height: size.height },
              hasTouch: size.touch,
              isMobile: viewport === 'phone',
              colorScheme: theme,
              reducedMotion: 'reduce',
              deviceScaleFactor: 1,
            }),
          );
        }
      }

      const jobs: Job[] = requested.flatMap((screen) =>
        (Object.keys(viewports) as Viewport[]).flatMap((viewport) =>
          themes.flatMap((theme) => languages.map((lang) => ({ screen, viewport, theme, lang }))),
        ),
      );
      const problems: string[] = [];
      let next = 0;
      const worker = async () => {
        for (let job = jobs[next++]; job !== undefined; job = jobs[next++]) {
          const context = contexts.get(`${job.viewport}-${job.theme}`);
          if (context !== undefined) {
            problems.push(...(await render(context, baseUrl, job, outDirectory)));
          }
        }
      };
      await Promise.all(Array.from({ length: Number(options.workers) }, worker));

      console.log(`${String(jobs.length)} screenshots in ${relative(repoRoot, outDirectory)}`);
      for (const problem of problems) {
        console.error(problem);
      }
      if (problems.length > 0) {
        process.exitCode = 1;
      }
    } finally {
      await browser.close();
    }
  } finally {
    if (server !== undefined) {
      stopServer(server);
    }
  }
}

await main();
