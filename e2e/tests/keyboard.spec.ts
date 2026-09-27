import type { Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { admin, signInAsAdmin } from './session';

/** Presses Tab until the element is focused, like a keyboard user would. */
async function tabTo(page: Page, name: string, role: 'button' | 'searchbox' = 'button') {
  const target = page.getByRole(role, { name, exact: true });
  for (let presses = 0; presses < 40; presses++) {
    if (await target.evaluate((element) => element === document.activeElement).catch(() => false)) {
      return;
    }
    await page.keyboard.press('Tab');
  }
  throw new Error(`Tab never reached ${name}`);
}

test('sign in, work with windows and sign out with the keyboard only', async ({ browser }) => {
  // The administrator exists after the first-run project; a fresh context has no session.
  const context = await browser.newContext();
  const page = await context.newPage();
  await signInAsAdmin(page);
  await context.clearCookies();
  await page.reload();

  await test.step('sign in', async () => {
    await expect(page.getByLabel('User name')).toBeFocused();
    await page.keyboard.type(admin.username);
    await page.keyboard.press('Tab');
    await expect(page.getByLabel('Password', { exact: true })).toBeFocused();
    await page.keyboard.type(admin.password);
    await page.keyboard.press('Enter');
    await expect(page.getByRole('navigation', { name: 'Taskbar' })).toBeVisible();
  });

  await test.step('open an app from the start menu', async () => {
    await expect(page.getByRole('button', { name: 'Start' })).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('searchbox')).toBeFocused();
    await page.keyboard.type('disks');
    await page.keyboard.press('Enter');
    await expect(page.getByRole('region', { name: 'Disks', exact: true })).toBeFocused();
  });

  await test.step('close the window from the window menu', async () => {
    await page.keyboard.press('Alt+Shift+Space');
    const menu = page.getByRole('menu', { name: 'Window menu for Disks' });
    await expect(menu).toBeVisible();
    await page.keyboard.press('End');
    await expect(page.getByRole('menuitem', { name: 'Close' })).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('region', { name: 'Disks', exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Start' })).toBeFocused();
  });

  await test.step('sign out from the account menu', async () => {
    await tabTo(page, 'Account menu for Ada Admin');
    await page.keyboard.press('Enter');
    await expect(page.getByRole('menu', { name: 'Account menu for Ada Admin' })).toBeVisible();
    await page.keyboard.press('End');
    await expect(page.getByRole('menuitem', { name: 'Sign out' })).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { name: 'Welcome back' })).toBeVisible();
  });
  await context.close();
});
