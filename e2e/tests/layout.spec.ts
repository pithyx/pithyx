import { type Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { appWindow, openFromDesktop, openPersonalSettings, signInAsAdmin } from './session';

/** Resolves with the next layout the desktop saved, or the next one that contains `containing`. */
async function layoutSaved(page: Page, containing = ''): Promise<string> {
  const response = await page.waitForResponse(
    (candidate) =>
      candidate.request().method() === 'PUT' &&
      candidate.url().endsWith('/api/desktop/layout') &&
      candidate.status() === 204 &&
      (candidate.request().postData() ?? '').includes(containing),
  );
  return response.request().postData() ?? '';
}

test('the layout survives a reload', async ({ page }) => {
  // Opening Settings may save a layout of its own first, depending on how fast its chunk loads.
  const saved = layoutSaved(page, '"zone":"left"');
  await signInAsAdmin(page);
  await openFromDesktop(page, 'Settings');
  await page.getByRole('button', { name: 'Pithyx menu' }).click();
  await page
    .getByRole('dialog', { name: 'Pithyx menu' })
    .getByRole('row', { name: 'About Pithyx' })
    .click();
  const about = appWindow(page, 'About Pithyx');
  await expect(about).toBeFocused();
  await page.keyboard.press('Alt+Shift+ArrowLeft');
  const payload = await saved;
  expect(payload).toContain('"zone":"left"');

  await page.reload();
  await expect(about).toBeVisible();
  await expect(appWindow(page, 'Settings')).toBeVisible();
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
  await openFromDesktop(first, 'Settings');
  await first.getByRole('button', { name: 'Pithyx menu' }).click();
  await first
    .getByRole('dialog', { name: 'Pithyx menu' })
    .getByRole('row', { name: 'About Pithyx' })
    .click();
  await saved;

  const phone = await browser.newContext();
  const second = await phone.newPage();
  await signInAsAdmin(second);
  await expect(appWindow(second, 'Settings')).toHaveCount(0);
  const settings = await openPersonalSettings(second, 'Appearance');
  await settings.getByRole('button', { name: 'Get desktop from', exact: true }).click();
  await second
    .getByRole('menuitem', { name: /^Chrome on / })
    .first()
    .click();
  await expect(appWindow(second, 'Settings')).toBeVisible();
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
  await openFromDesktop(first, 'Settings');
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
  expect(await secondSaved).toContain('"app":"org.pithyx.about"');
  expect(statuses).toEqual([412, 204]);

  await first.reload();
  await expect(appWindow(first, 'About Pithyx')).toBeVisible();
  await expect(appWindow(first, 'Settings')).toHaveCount(0);
  await context.close();
});

test('apps chosen in Personal settings open after signing in, once', async ({ page }) => {
  await signInAsAdmin(page);
  try {
    const settings = await openPersonalSettings(page, 'Apps');
    await settings.getByRole('button', { name: 'Open at sign-in', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Open at sign-in' });
    const saved = page.waitForResponse(
      (response) =>
        response.url().endsWith('/api/desktop/startup-apps') && response.status() === 204,
    );
    // React Aria hides the input itself; the label takes the click.
    await dialog.getByText('About Pithyx', { exact: true }).click();
    await saved;
    await dialog.getByRole('button', { name: 'Close' }).click();

    await page.getByRole('button', { name: 'Pithyx menu' }).click();
    await page
      .getByRole('dialog', { name: 'Pithyx menu' })
      .getByRole('button', { name: 'Sign out' })
      .click();
    await expect(page.getByRole('heading', { name: 'Welcome back' })).toBeVisible();
    const opened = layoutSaved(page, 'org.pithyx.about');
    await signInAsAdmin(page);
    await expect(appWindow(page, 'About Pithyx')).toBeVisible();

    // A reload is no sign-in: the restored window stays the only one.
    await opened;
    await page.reload();
    await expect(appWindow(page, 'About Pithyx')).toHaveCount(1);
  } finally {
    await page.request.put('/api/desktop/startup-apps', {
      data: { apps: [] },
      headers: { 'X-Pithyx-Request': '1' },
    });
  }
});
