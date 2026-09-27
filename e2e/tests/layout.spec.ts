import { type Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { appWindow, openAccountMenu, openFromDesktop, signInAsAdmin } from './session';

/** Resolves with the next layout the desktop saved. */
async function layoutSaved(page: Page): Promise<string> {
  const response = await page.waitForResponse(
    (candidate) =>
      candidate.request().method() === 'PUT' &&
      candidate.url().endsWith('/api/desktop/layout') &&
      candidate.status() === 204,
  );
  return response.request().postData() ?? '';
}

test('the layout survives a reload', async ({ page }) => {
  const saved = layoutSaved(page);
  await signInAsAdmin(page);
  await openFromDesktop(page, 'Disks');
  await page.getByRole('button', { name: 'Start' }).click();
  await page
    .getByRole('dialog', { name: 'Start menu' })
    .getByRole('row', { name: 'About Pithyx' })
    .click();
  const about = appWindow(page, 'About Pithyx');
  await expect(about).toBeFocused();
  await page.keyboard.press('Alt+Shift+ArrowLeft');
  const payload = await saved;
  expect(payload).toContain('"zone":"left"');

  await page.reload();
  await expect(about).toBeVisible();
  await expect(appWindow(page, 'Disks')).toBeVisible();
  const workspace = await page.getByRole('main', { name: 'Desktop' }).boundingBox();
  await expect
    .poll(async () => (await about.boundingBox())?.width)
    .toBe(Math.round((workspace?.width ?? 0) / 2));
  await expect(
    page
      .getByRole('navigation', { name: 'Taskbar' })
      .getByRole('button', { name: 'About Pithyx, active' }),
  ).toBeVisible();
});

test('another device gets this desktop', async ({ browser }) => {
  const laptop = await browser.newContext();
  const first = await laptop.newPage();
  const saved = layoutSaved(first);
  await signInAsAdmin(first);
  await openFromDesktop(first, 'Disks');
  await first.getByRole('button', { name: 'Start' }).click();
  await first
    .getByRole('dialog', { name: 'Start menu' })
    .getByRole('row', { name: 'About Pithyx' })
    .click();
  await saved;

  const phone = await browser.newContext();
  const second = await phone.newPage();
  await signInAsAdmin(second);
  await expect(appWindow(second, 'Disks')).toHaveCount(0);
  await openAccountMenu(second);
  await second.getByRole('menuitem', { name: 'Get desktop from' }).click();
  await second
    .getByRole('menuitem', { name: /^Chrome on / })
    .first()
    .click();
  await expect(appWindow(second, 'Disks')).toBeVisible();
  await expect(appWindow(second, 'About Pithyx')).toBeVisible();
  await expect(second.getByText('Desktop loaded')).toBeVisible();

  await laptop.close();
  await phone.close();
});

test('two tabs of one browser do not overwrite each other unnoticed', async ({ browser }) => {
  const context = await browser.newContext();
  const first = await context.newPage();
  await signInAsAdmin(first);
  const second = await context.newPage();
  await second.goto('/');
  await expect(second.getByRole('navigation', { name: 'Taskbar' })).toBeVisible();

  // Both tabs start from the same saved version; the first one saves a newer one.
  const firstSaved = layoutSaved(first);
  await openFromDesktop(first, 'Disks');
  await firstSaved;

  // The second tab still names the old version, gets 412 and saves its own layout on top, since it is in use.
  const statuses: number[] = [];
  second.on('response', (response) => {
    if (response.request().method() === 'PUT' && response.url().endsWith('/api/desktop/layout')) {
      statuses.push(response.status());
    }
  });
  const secondSaved = layoutSaved(second);
  await openFromDesktop(second, 'About Pithyx');
  expect(await secondSaved).toContain('"app":"about"');
  expect(statuses).toEqual([412, 204]);

  await first.reload();
  await expect(appWindow(first, 'About Pithyx')).toBeVisible();
  await expect(appWindow(first, 'Disks')).toHaveCount(0);
  await context.close();
});
