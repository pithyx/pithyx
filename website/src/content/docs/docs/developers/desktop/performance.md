---
title: 'Performance and budgets'
description: 'Bundle and timing budgets, the perf gate and how to stay inside them.'
status: ready
audience: developer
sidebar:
  order: 4
---

The desktop is held to two kinds of budgets: gzip size limits for the code that loads before and after sign-in, and absolute timing budgets measured by an end-to-end test on a throttled browser. The size check runs on every frontend change; the timing check runs when a pull request carries the `e2e` label.

## Budgets

**Bundle size** (gzip, `pnpm build` then `pnpm check:bundle`, script `tools/checks/bundle-budget.ts`):

- **Sign-in bundle:** everything `index.html` loads before the first render plus the largest `i18n-<lang>` chunk. It may not exceed 200 KB and may not exceed the recorded baseline (`tools/checks/bundle-budget.json`, 147,995 bytes) by more than 5 percent.
- **Desktop bundle:** what signing in adds (the desktop chunk with its static imports and styles, plus the largest desktop translation chunk). Fixed limit of 150 KB.
- The baseline changes only with `node tools/checks/bundle-budget.ts --update`.
- Fonts are separate woff2 files and not part of these numbers.

**Timing** (`e2e/tests/budgets.spec.ts`, Playwright against the production build served by the daemon). The browser uses the Chrome "Fast 4G" profile (9 Mbit/s down, 1.5 Mbit/s up, 165 ms latency) with a 4x CPU slowdown, a 1440x900 window and a cold cache for each round. Each budget is the median of 5 rounds after one warm-up round.

| Measure                                        | Budget  |
| ---------------------------------------------- | ------- |
| Cold start to a usable sign-in                 | 1800 ms |
| Click to visible feedback                      | 100 ms  |
| Sign-in to a usable desktop                    | 1000 ms |
| Reload                                         | 650 ms  |
| Files: open a visited folder (1000 files)      | 16 ms   |
| Files: first page of a new folder (1000 files) | 100 ms  |

The daemon has its own timing test: the first page of a folder with 100,000 entries in under 200 ms (category `TimingBudget`).

The timing budgets are absolute numbers, not a tolerance against earlier runs. Only the sign-in bundle is also compared with a baseline.

Rules of thumb: show no spinner before 300 ms, open apps with dynamic `import()`, use narrow zustand selectors for fast-changing state, rely on the React Compiler instead of `useMemo`, and virtualize long lists.

## The perf gate

- The job `e2e` in `.github/workflows/ci.yaml` builds the daemon and the desktop, runs `pnpm e2e` (which includes the budgets), `pnpm design:test` and the daemon `TimingBudget` tests.
- It runs only on request: with the pull request label `e2e`, by starting the workflow manually, or for any pull request or push to `beta` and `stable`. Add the label for changes to user flows, the shell, the window manager, sign-in, setup, SDK transport, the API contract, the loading path or the bundle.
- It starts after the daemon tests finish, because parallel load skews the timings. On pushes to `dev` the budgets are skipped (`PITHYX_SKIP_BUDGETS=1`).
- Timings vary with machine load. If a budget fails but nothing slow is in your change, check `uptime` and run once more.
- `pnpm design:test` opens every screen of the design gallery at 390x844, 820x1180 and 1440x900 in English and German and fails on overflow or a covered title bar. Accessibility is checked with axe in `e2e/tests/a11y.spec.ts`, and token contrast in `packages/ui/src/contrast.test.ts`.

## Measuring

```sh
node tools/perf/measure.ts --url https://<box-address>:7484 --user <name> --password '<password>' --runs 5
```

The script needs a Chromium installed through the end-to-end dependencies and an existing user. It runs an unthrottled profile and a Fast 4G profile with a 4x CPU slowdown, with a cold context for each run, and prints the median of cold start, click feedback, sign-in to desktop, first reload, warm reload, and the time the password hash takes. Put the table in your pull request.

To check the bundle, run `pnpm build` and `pnpm check:bundle`; it prints every file's gzip size and the totals. In the Files app, `performance.getEntriesByName('pithyx:files:shown', 'measure')` shows the time to show a folder.
