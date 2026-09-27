import { expect, test } from './fixtures';
import { admin, signInAsAdmin } from './session';

// TODO.md 5.4: the desktop is usable within 1 s after signing in on a repeat visit. Usable means the taskbar is
// shown and the welcome card has left, which happens once the live connection is open. The median of three sign-ins
// keeps a single slow run on a busy CI machine from failing the budget.
const BUDGET_MS = 1000;

test('the desktop is usable within one second after signing in again', async ({ page }) => {
  await signInAsAdmin(page);
  const durations: number[] = [];
  for (let round = 0; round < 3; round++) {
    await page.getByRole('button', { name: 'Account menu for Ada Admin' }).click();
    await page.getByRole('menuitem', { name: 'Sign out' }).click();
    await page.getByLabel('User name').fill(admin.username);
    await page.getByLabel('Password', { exact: true }).fill(admin.password);
    const start = Date.now();
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.getByRole('navigation', { name: 'Taskbar' })).toBeVisible();
    await expect(page.getByRole('status').filter({ hasText: 'Welcome' })).toHaveCount(0);
    durations.push(Date.now() - start);
  }
  const median = [...durations].sort((a, b) => a - b)[1] ?? Infinity;
  test
    .info()
    .annotations.push({ type: 'sign-in to desktop (ms)', description: durations.join(', ') });
  expect(median).toBeLessThan(BUDGET_MS);
});
