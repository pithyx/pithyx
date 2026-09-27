import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import type { Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { MIN_RUNS, measureRound, median, type Round } from './performance';
import { admin, openFromStart, signInAsAdmin } from './session';

// Performance budgets against the production build the daemon serves, on Chrome's "Fast 4G" with the CPU slowed
// down four times. Tracing is off: its snapshots would slow the page down. Every budget is the median of five
// sign-ins (each in a fresh context, so the first load is cold), at least MIN_RUNS valid ones, so a single slow run
// on a busy CI machine cannot fail a budget; one unmeasured round first lets the daemon warm up.
const ROUNDS = 5;
const BUDGETS = {
  coldToLogin: 1500,
  clickToFeedback: 100,
  loginToDesktopWithoutArgon2: 1000,
  reload: 500,
} as const;

// The real motion: the welcome card must bridge the wait without adding to it.
test.use({ reducedMotion: 'no-preference', trace: 'off' });

// CI runs the gate on pull requests; the run on dev after a merge skips it (PITHYX_SKIP_BUDGETS=1).
test.skip(process.env.PITHYX_SKIP_BUDGETS === '1', 'Performance budgets gate pull requests only');

test('sign-in and reload stay within their budgets on a slow connection', async ({
  browser,
  baseURL,
}) => {
  test.setTimeout(240_000);
  const rounds: Round[] = [];
  for (let round = 0; round <= ROUNDS; round++) {
    const context = await browser.newContext({
      viewport: { width: 1440, height: 900 },
      locale: 'en-US',
    });
    const result = await measureRound(context, {
      url: baseURL ?? '/',
      username: admin.username,
      password: admin.password,
      profile: 'fast4g-cpu4x',
    });
    await context.close();
    if (round > 0) {
      rounds.push(result);
    }
  }

  expect(rounds.length).toBeGreaterThanOrEqual(MIN_RUNS);
  const measured = {
    coldToLogin: rounds.map((round) => round.coldToLogin),
    clickToFeedback: rounds.map((round) => round.clickToFeedback),
    loginToDesktopWithoutArgon2: rounds.map((round) => round.loginToDesktop - round.argon2),
    reload: rounds.map((round) => round.reload),
  };
  // Printed as well, so the CI log shows how close each budget is.
  // The first reload after an update is reported, not budgeted: Chrome then writes V8's code cache on the main thread.
  const reported = {
    ...measured,
    firstReload: rounds.map((round) => round.firstReload),
    argon2: rounds.map((round) => round.argon2),
  };
  const rows = Object.entries(reported).map(([metric, values]) => ({
    type: `${metric} (ms)`,
    description: `median ${String(Math.round(median(values)))}: ${values.map((value) => String(Math.round(value))).join(', ')}`,
  }));
  test.info().annotations.push(...rows);
  console.log(rows.map((row) => `${row.type} ${row.description}`).join('\n'));

  expect
    .soft(median(measured.coldToLogin), 'cold start to sign-in usable')
    .toBeLessThan(BUDGETS.coldToLogin);
  expect
    .soft(median(measured.clickToFeedback), 'click to visible feedback')
    .toBeLessThan(BUDGETS.clickToFeedback);
  expect
    .soft(median(measured.loginToDesktopWithoutArgon2), 'sign-in to desktop without Argon2')
    .toBeLessThan(BUDGETS.loginToDesktopWithoutArgon2);
  expect.soft(median(measured.reload), 'reload with a warm cache').toBeLessThan(BUDGETS.reload);
});

// Files on the LAN with a warm daemon: a folder visited before shows from the cache within one frame, a new folder's
// first page within 100 ms. Both are measured by the app itself, from the navigation to the rows on screen.
const FILES_BUDGETS = { visitedFolder: 16, firstPage: 100 } as const;

async function lastShown(page: Page): Promise<number> {
  return page.evaluate(() => {
    const entries = performance.getEntriesByName('pithyx:files:shown', 'measure');
    return entries.at(-1)?.duration ?? Number.NaN;
  });
}

test('Files shows a visited folder within a frame and a new one quickly', async ({ page }) => {
  test.setTimeout(120_000);
  const shared = join(process.env.PITHYX_E2E_DATA ?? '', 'shares', 'shared');
  for (let round = 0; round <= ROUNDS; round++) {
    const folder = join(shared, `budget-${String(round)}`);
    mkdirSync(folder, { recursive: true });
    for (let file = 0; file < 1000; file++) {
      writeFileSync(join(folder, `photo ${String(file)}.jpg`), '');
    }
  }
  await page.setViewportSize({ width: 1440, height: 900 });
  await signInAsAdmin(page);
  await openFromStart(page, 'Files');
  const window = page.getByRole('region', { name: /Files$/ });
  const grid = window.getByRole('grid', { name: 'Folder contents' });

  const measured = { firstPage: [] as number[], visitedFolder: [] as number[] };
  for (let round = 0; round <= ROUNDS; round++) {
    const name = `budget-${String(round)}`;
    const open = async () => {
      await grid.getByRole('row', { name: new RegExp(`^${name}`) }).dblclick();
      await expect(grid.getByRole('row', { name: /^photo 0\.jpg/ })).toBeVisible();
      return lastShown(page);
    };
    const first = await open();
    await page.keyboard.press('Backspace');
    await expect(grid.getByRole('row', { name: new RegExp(`^${name}`) })).toBeVisible();
    const visited = await open();
    await page.keyboard.press('Backspace');
    if (round > 0) {
      measured.firstPage.push(first);
      measured.visitedFolder.push(visited);
    }
  }

  const rows = Object.entries(measured).map(([metric, values]) => ({
    type: `files ${metric} (ms)`,
    description: `median ${median(values).toFixed(1)}: ${values.map((value) => value.toFixed(1)).join(', ')}`,
  }));
  test.info().annotations.push(...rows);
  console.log(rows.map((row) => `${row.type} ${row.description}`).join('\n'));
  expect
    .soft(median(measured.visitedFolder), 'visited folder from the cache')
    .toBeLessThan(FILES_BUDGETS.visitedFolder);
  expect
    .soft(median(measured.firstPage), 'first page of a new folder')
    .toBeLessThan(FILES_BUDGETS.firstPage);
});
