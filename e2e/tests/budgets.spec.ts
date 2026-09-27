import { expect, test } from './fixtures';
import { MIN_RUNS, measureRound, median, type Round } from './performance';
import { admin } from './session';

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

// CI runs the gate on pull requests; the run on dev after a merge skips it (VYXIS_SKIP_BUDGETS=1).
test.skip(process.env.VYXIS_SKIP_BUDGETS === '1', 'Performance budgets gate pull requests only');

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
