import { type Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { appWindow, openFromDesktop, signInAsAdmin } from './session';

/** Resolves with the next layout the desktop saves over the WebSocket. */
function layoutSaved(page: Page): Promise<string> {
  return new Promise((resolve) => {
    page.on('websocket', (socket) => {
      socket.on('framesent', (frame) => {
        const payload =
          typeof frame.payload === 'string' ? frame.payload : frame.payload.toString();
        if (payload.includes('"type":"layout"')) {
          resolve(payload);
        }
      });
    });
  });
}

test('the layout survives a reload', async ({ page }) => {
  const saved = layoutSaved(page);
  await signInAsAdmin(page);
  await openFromDesktop(page, 'Disks');
  await page.getByRole('button', { name: 'Start' }).click();
  await page
    .getByRole('dialog', { name: 'Start menu' })
    .getByRole('row', { name: 'About Vyxis' })
    .click();
  const about = appWindow(page, 'About Vyxis');
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
      .getByRole('button', { name: 'About Vyxis, active' }),
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
    .getByRole('row', { name: 'About Vyxis' })
    .click();
  await saved;

  const phone = await browser.newContext();
  const second = await phone.newPage();
  await signInAsAdmin(second);
  await expect(appWindow(second, 'Disks')).toHaveCount(0);
  await second.getByRole('button', { name: 'Account menu for Ada Admin' }).click();
  await second.getByRole('menuitem', { name: 'Get desktop from' }).click();
  await second
    .getByRole('menuitem', { name: /^Chrome on / })
    .first()
    .click();
  await expect(appWindow(second, 'Disks')).toBeVisible();
  await expect(appWindow(second, 'About Vyxis')).toBeVisible();
  await expect(second.getByText('Desktop loaded')).toBeVisible();

  await laptop.close();
  await phone.close();
});
