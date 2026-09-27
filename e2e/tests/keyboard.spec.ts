import type { Locator, Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { admin, signInAsAdmin } from './session';

/** The element's box once a restore animation (1 ms under reduced motion) has finished. */
async function settledBox(locator: Locator) {
  await locator.evaluate((element) =>
    Promise.all(element.getAnimations().map((animation) => animation.finished)),
  );
  return locator.boundingBox();
}

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
    await expect(page.getByRole('button', { name: 'Pithyx menu' })).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('searchbox')).toBeFocused();
    await page.keyboard.type('disks');
    await page.keyboard.press('Enter');
    await expect(page.getByRole('region', { name: 'Disks', exact: true })).toBeFocused();
  });

  await test.step('minimizing the last window moves focus to the start button', async () => {
    await page.keyboard.press('Alt+Shift+ArrowDown');
    await expect(page.getByRole('region', { name: 'Disks', exact: true })).toBeHidden();
    await expect(page.getByRole('button', { name: 'Pithyx menu' })).toBeFocused();
    await page.keyboard.press('Alt+Shift+PageDown');
    await expect(page.getByRole('region', { name: 'Disks', exact: true })).toBeFocused();
  });

  await test.step('escape puts the window back where it was before the keyboard move', async () => {
    const disks = page.getByRole('region', { name: 'Disks', exact: true });
    const before = await settledBox(disks);
    await page.keyboard.press('Alt+Shift+Space');
    await page.getByRole('menuitem', { name: 'Move' }).press('Enter');
    await page.keyboard.press('ArrowRight');
    await page.keyboard.press('ArrowDown');
    await expect.poll(async () => (await settledBox(disks))?.x).toBe((before?.x ?? 0) + 16);
    await page.keyboard.press('Escape');
    await expect.poll(async () => (await settledBox(disks))?.x).toBe(before?.x);
    expect((await settledBox(disks))?.y).toBe(before?.y);
  });

  await test.step('close the window from the window menu', async () => {
    await page.keyboard.press('Alt+Shift+Space');
    const menu = page.getByRole('menu', { name: 'Window menu for Disks' });
    await expect(menu).toBeVisible();
    await page.keyboard.press('End');
    await expect(page.getByRole('menuitem', { name: 'Close' })).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('region', { name: 'Disks', exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Pithyx menu' })).toBeFocused();
  });

  await test.step('sign out with the button of the Pithyx menu', async () => {
    await tabTo(page, 'Pithyx menu');
    await page.keyboard.press('Enter');
    await expect(page.getByRole('dialog', { name: 'Pithyx menu' })).toBeVisible();
    await tabTo(page, 'Sign out');
    await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { name: 'Welcome back' })).toBeVisible();
  });
  await context.close();
});
