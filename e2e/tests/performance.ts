/// <reference lib="dom" />
import type { BrowserContext, CDPSession, Page } from '@playwright/test';

// Measurements shared by the performance budgets (budgets.spec.ts) and the manual measuring tool
// (tools/perf/measure.ts). Times are taken inside the page with performance.now(), relative to the navigation or to
// the click on "Sign in", so Playwright's own round trips never count.

// Chrome DevTools "Fast 4G": 9 Mbit/s down, 1.5 Mbit/s up, 165 ms latency, scaled like DevTools does.
export const FAST_4G = {
  offline: false,
  downloadThroughput: ((9 * 1000 * 1000) / 8) * 0.9,
  uploadThroughput: ((1.5 * 1000 * 1000) / 8) * 0.9,
  latency: 60 * 2.75,
};
export const CPU_SLOWDOWN = 4;

export type Profile = 'unthrottled' | 'fast4g-cpu4x';

// The sign-in screen is usable once the user name field is there. The desktop is usable once the taskbar is there
// and no sign-in or welcome card covers it: those are a <main> without a label, and a leaving card (data-leaving)
// already lets input through. Feedback is the first visible reaction of the sign-in button to the click.
const MARKS = `
  (() => {
    const marks = (window.__marks = {});
    const check = () => {
      const now = performance.now();
      if (marks.login === undefined && document.querySelector('input[autocomplete="username"]')) marks.login = now;
      const covered = [...document.querySelectorAll('main:not([aria-label])')].some((main) => !main.closest('[data-leaving]'));
      if (marks.desktop === undefined && document.querySelector('nav[aria-label="Taskbar"]') && !covered) marks.desktop = now;
      const button = document.querySelector('button[type="submit"]');
      if (marks.clicked !== undefined && marks.feedback === undefined && button?.getAttribute('aria-busy') === 'true') marks.feedback = now;
    };
    new MutationObserver(check).observe(document, { subtree: true, childList: true, attributes: true });
    document.addEventListener('click', (event) => {
      if (event.target instanceof Element && event.target.closest('button[type="submit"]')) {
        marks.clicked = performance.now();
        marks.desktop = undefined;
        marks.feedback = undefined;
      }
    }, true);
  })();
`;

type Mark = 'login' | 'desktop' | 'clicked' | 'feedback';

async function mark(page: Page, name: Mark): Promise<number> {
  const handle = await page.waitForFunction(
    (key) => (window as unknown as { __marks: Partial<Record<string, number>> }).__marks[key],
    name,
    { timeout: 30_000 },
  );
  return Number(await handle.jsonValue());
}

async function transferred(page: Page): Promise<number> {
  return page.evaluate(() =>
    performance
      .getEntriesByType('resource')
      .concat(performance.getEntriesByType('navigation'))
      .reduce((sum, entry) => sum + (entry as PerformanceResourceTiming).transferSize, 0),
  );
}

export interface Round {
  /** From navigation start to a usable sign-in screen, with an empty cache. */
  coldToLogin: number;
  coldBytes: number;
  clickToFeedback: number;
  /** From the click on "Sign in" to a usable desktop, including the password check. */
  loginToDesktop: number;
  /** The daemon's Argon2 time for the sign-in (Server-Timing), part of loginToDesktop. */
  argon2: number;
  /**
   * The first reload after the first visit: the HTTP cache is warm, but Chrome still writes V8's code cache for every
   * script on the main thread. This happens once per script version, so after an update.
   */
  firstReload: number;
  warmBytes: number;
  /** Any later reload: HTTP cache and code cache are warm. What a returning user sees. */
  reload: number;
  /** A reload that bypasses the HTTP cache. */
  hardReload?: number;
}

/**
 * One sign-in in a fresh context (empty cache): cold start, sign-in, reloads, sign-out. The user must exist.
 * `extended` adds a hard reload, which the budgets do not track.
 */
export async function measureRound(
  context: BrowserContext,
  options: {
    url: string;
    username: string;
    password: string;
    profile: Profile;
    extended?: boolean;
  },
): Promise<Round> {
  const page = await context.newPage();
  await page.addInitScript(MARKS);
  const cdp: CDPSession = await context.newCDPSession(page);
  await cdp.send('Network.enable');
  if (options.profile === 'fast4g-cpu4x') {
    await cdp.send('Network.emulateNetworkConditions', FAST_4G);
    await cdp.send('Emulation.setCPUThrottlingRate', { rate: CPU_SLOWDOWN });
  }

  await page.goto(options.url);
  const coldToLogin = await mark(page, 'login');
  const coldBytes = await transferred(page);

  await page.getByLabel('User name').fill(options.username);
  await page.getByLabel('Password', { exact: true }).fill(options.password);
  const loginResponse = page.waitForResponse((response) =>
    response.url().endsWith('/api/auth/login'),
  );
  await page.getByRole('button', { name: 'Sign in' }).click();
  const response = await loginResponse;
  const clicked = await mark(page, 'clicked');
  const feedback = await mark(page, 'feedback');
  const desktop = await mark(page, 'desktop');
  const serverTiming = response.headers()['server-timing'] ?? '';
  const argon2 = Number(/argon2;dur=([\d.]+)/.exec(serverTiming)?.[1] ?? NaN);

  await page.reload();
  const firstReload = await mark(page, 'desktop');
  const warmBytes = await transferred(page);
  await page.reload();
  const round: Round = {
    coldToLogin,
    coldBytes,
    clickToFeedback: feedback - clicked,
    loginToDesktop: desktop - clicked,
    argon2,
    firstReload,
    warmBytes,
    reload: await mark(page, 'desktop'),
  };

  if (options.extended === true) {
    await cdp.send('Network.setCacheDisabled', { cacheDisabled: true });
    await page.reload();
    round.hardReload = await mark(page, 'desktop');
    await cdp.send('Network.setCacheDisabled', { cacheDisabled: false });
  }

  // Signing out keeps the daemon's session table as it was.
  const signedOut = await page.evaluate(async () => {
    const response = await fetch('/api/auth/logout', {
      method: 'POST',
      headers: { 'X-Vyxis-Request': '1' },
    });
    return response.ok;
  });
  if (!signedOut) {
    throw new Error('Signing out failed');
  }
  await page.close();
  return round;
}

/** A budget or a reported number is the median of at least this many runs. */
export const MIN_RUNS = 3;

/** The median of the valid values, or NaN with fewer than MIN_RUNS of them, so a budget never rests on one run. */
export function median(values: readonly number[]): number {
  const sorted = values.filter((value) => !Number.isNaN(value)).sort((a, b) => a - b);
  return sorted.length < MIN_RUNS ? NaN : (sorted[Math.floor(sorted.length / 2)] ?? NaN);
}
