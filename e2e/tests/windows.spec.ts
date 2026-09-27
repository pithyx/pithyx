import { expect, test, type Locator, type Page } from '@playwright/test';
import { appWindow, openFromDesktop, signInAsAdmin } from './session';

async function zIndex(window: Locator): Promise<number> {
  return Number(await window.evaluate((element) => getComputedStyle(element).zIndex));
}

function taskbarEntry(page: Page, name: RegExp) {
  return page.getByRole('navigation', { name: 'Taskbar' }).getByRole('button', { name });
}

test.beforeEach(async ({ page }) => {
  await signInAsAdmin(page);
});

test('opens, focuses, minimizes and closes windows', async ({ page }) => {
  const disks = appWindow(page, 'Disks');
  const about = appWindow(page, 'About Vyxis');

  await test.step('open two apps from the desktop', async () => {
    await openFromDesktop(page, 'Disks');
    await openFromDesktop(page, 'About Vyxis');
    await expect(disks).toBeVisible();
    await expect(about).toBeVisible();
    await expect(taskbarEntry(page, /^About Vyxis, active$/)).toBeVisible();
    await expect(taskbarEntry(page, /^Disks$/)).toBeVisible();
    expect(await zIndex(about)).toBeGreaterThan(await zIndex(disks));
  });

  await test.step('a click raises a window', async () => {
    await disks.getByRole('heading', { name: 'Disks' }).click();
    expect(await zIndex(disks)).toBeGreaterThan(await zIndex(about));
    await expect(taskbarEntry(page, /^Disks, active$/)).toBeVisible();
  });

  await test.step('a single-instance app focuses its window instead of opening another', async () => {
    await page.getByRole('button', { name: 'Start' }).click();
    await page
      .getByRole('dialog', { name: 'Start menu' })
      .getByRole('row', { name: 'About Vyxis' })
      .click();
    await expect(page.getByRole('region', { name: 'About Vyxis' })).toHaveCount(1);
    expect(await zIndex(about)).toBeGreaterThan(await zIndex(disks));
  });

  await test.step('the taskbar minimizes the active window and restores it', async () => {
    await taskbarEntry(page, /^About Vyxis, active$/).click();
    await expect(about).toBeHidden();
    await expect(taskbarEntry(page, /^About Vyxis, minimized$/)).toBeVisible();
    await expect(taskbarEntry(page, /^Disks, active$/)).toBeVisible();
    await taskbarEntry(page, /^About Vyxis, minimized$/).click();
    await expect(about).toBeVisible();
    await expect(about).toBeFocused();
  });

  await test.step('maximize and restore', async () => {
    const before = await about.boundingBox();
    await about.getByRole('button', { name: 'Maximize' }).click();
    const workspace = await page.getByRole('main', { name: 'Desktop' }).boundingBox();
    await expect.poll(async () => (await about.boundingBox())?.width).toBe(workspace?.width);
    await about.getByRole('button', { name: 'Restore' }).click();
    await expect.poll(async () => (await about.boundingBox())?.width).toBe(before?.width);
  });

  await test.step('close', async () => {
    await about.getByRole('button', { name: 'Close' }).click();
    await expect(about).toHaveCount(0);
    await expect(taskbarEntry(page, /About Vyxis/)).toHaveCount(0);
    await expect(taskbarEntry(page, /^Disks, active$/)).toBeVisible();
  });
});

test('the start menu searches and opens apps', async ({ page }) => {
  const start = page.getByRole('button', { name: 'Start' });
  await start.click();
  const menu = page.getByRole('dialog', { name: 'Start menu' });
  await expect(menu.getByRole('searchbox')).toBeFocused();

  await page.keyboard.press('Escape');
  await expect(menu).toBeHidden();
  await expect(start).toBeFocused();

  await start.click();
  await page.keyboard.type('abo');
  await expect(menu.getByRole('option', { name: /About Vyxis/ })).toBeVisible();
  await page.keyboard.press('Enter');
  await expect(menu).toBeHidden();
  await expect(appWindow(page, 'About Vyxis')).toBeVisible();

  await start.click();
  await menu.getByRole('row', { name: 'Disks' }).click();
  await expect(appWindow(page, 'Disks')).toBeVisible();

  await start.click();
  await page.getByRole('main', { name: 'Desktop' }).click({ position: { x: 700, y: 400 } });
  await expect(menu).toBeHidden();
});
