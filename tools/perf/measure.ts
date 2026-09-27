// Measures the production desktop as the daemon serves it, for the performance table in a pull request: cold start
// to a usable sign-in screen, click to visible feedback, sign-in to a usable desktop and the reloads. The CI budgets
// (e2e/tests/budgets.spec.ts) use the same measurements.
//
//   node tools/perf/measure.ts --url http://192.168.168.40:8994 [--user admin --password '...'] [--runs 5]
import { chromium } from '@playwright/test';
import { parseArgs } from 'node:util';
import { measureRound, median, type Round } from '../../e2e/tests/performance.ts';

const { values: options } = parseArgs({
  options: {
    url: { type: 'string', default: 'http://127.0.0.1:8994' },
    user: { type: 'string', default: 'admin' },
    password: { type: 'string', default: 'correct horse battery' },
    runs: { type: 'string', default: '5' },
  },
});

const ms = (value: number) => (Number.isNaN(value) ? 'n/a' : `${String(Math.round(value))} ms`);
const kb = (value: number) => `${(value / 1024).toFixed(0)} KB`;

const browser = await chromium.launch();
try {
  for (const profile of ['unthrottled', 'fast4g-cpu4x'] as const) {
    const runs: Round[] = [];
    for (let run = 0; run < Number(options.runs); run++) {
      // A new context has an empty cache: every run starts cold.
      const context = await browser.newContext({
        viewport: { width: 1440, height: 900 },
        locale: 'en-US',
      });
      runs.push(
        await measureRound(context, {
          url: options.url,
          username: options.user,
          password: options.password,
          profile,
          extended: true,
        }),
      );
      await context.close();
    }
    const row = (key: keyof Round) => median(runs.map((run) => run[key] ?? NaN));
    console.log(`\n${options.url} ${profile} (median of ${String(runs.length)})`);
    console.log(
      `  cold start to sign-in usable  ${ms(row('coldToLogin'))}  (${kb(row('coldBytes'))} transferred)`,
    );
    console.log(`  click to visible feedback     ${ms(row('clickToFeedback'))}`);
    console.log(
      `  sign-in to desktop usable     ${ms(row('loginToDesktop'))}  (argon2 ${ms(row('argon2'))})`,
    );
    console.log(
      `  first reload (code cache cold) ${ms(row('firstReload'))}  (${kb(row('warmBytes'))} transferred)`,
    );
    console.log(`  reload, warm cache            ${ms(row('reload'))}`);
    console.log(`  hard reload                   ${ms(row('hardReload'))}`);
  }
} finally {
  await browser.close();
}
