import { expect, test, type Locator } from '@playwright/test';
import { appWindow, signInAsAdmin } from './session';

async function box(locator: Locator) {
  const result = await locator.boundingBox();
  if (result === null) {
    throw new Error('element is not visible');
  }
  return result;
}

test.describe('phone', () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });

  test('apps run full screen with an app switcher, swipe and back gesture', async ({ page }) => {
    await signInAsAdmin(page);
    const taskbar = page.getByRole('navigation', { name: 'Taskbar' });
    const disks = appWindow(page, 'Disks');
    const about = appWindow(page, 'About Vyxis');

    await test.step('a tap opens an app full screen', async () => {
      await page.getByRole('button', { name: 'Disks, shortcut' }).tap();
      await expect(disks).toBeVisible();
      const workspace = await box(page.getByRole('main', { name: 'Desktop' }));
      expect((await box(disks)).width).toBe(workspace.width);
      expect((await box(disks)).height).toBe(workspace.height);
    });

    await test.step('the back gesture returns to the home screen', async () => {
      await page.goBack();
      await expect(disks).toBeHidden();
      await expect(page.getByRole('button', { name: 'Disks, shortcut' })).toBeVisible();
    });

    await test.step('the app switcher shows open apps and switches between them', async () => {
      await page.getByRole('button', { name: 'About Vyxis, shortcut' }).tap();
      await expect(about).toBeVisible();
      await taskbar.getByRole('button', { name: /Switch apps, 2 open/ }).tap();
      const switcher = page.getByRole('dialog', { name: 'Open apps' });
      await expect(switcher.getByRole('listitem')).toHaveCount(2);
      await switcher.getByRole('button', { name: 'Switch to Disks' }).tap();
      await expect(switcher).toBeHidden();
      await expect(disks).toBeVisible();
      await expect(about).toBeHidden();
    });

    await test.step('a swipe along the taskbar switches to the next app', async () => {
      const bar = await box(taskbar);
      const y = bar.y + bar.height / 2;
      await page.mouse.move(bar.x + bar.width * 0.75, y);
      await page.mouse.down();
      await page.mouse.move(bar.x + bar.width * 0.25, y, { steps: 6 });
      await page.mouse.up();
      await expect(about).toBeVisible();
      await expect(disks).toBeHidden();
    });

    await test.step('close all from the switcher', async () => {
      await taskbar.getByRole('button', { name: /Switch apps/ }).tap();
      const switcher = page.getByRole('dialog', { name: 'Open apps' });
      await switcher.getByRole('button', { name: 'Close all' }).tap();
      await expect(switcher).toBeHidden();
      await expect(disks).toHaveCount(0);
      await expect(about).toHaveCount(0);
    });
  });
});

test.describe('tablet', () => {
  test.use({ viewport: { width: 820, height: 1180 }, hasTouch: true });

  test('windows open maximized and split side by side', async ({ page }) => {
    await signInAsAdmin(page);
    const disks = appWindow(page, 'Disks');
    const about = appWindow(page, 'About Vyxis');
    const workspace = await box(page.getByRole('main', { name: 'Desktop' }));

    await page.getByRole('button', { name: 'Disks, shortcut' }).tap();
    await expect.poll(async () => (await box(disks)).width).toBe(workspace.width);

    await disks.getByRole('button', { name: 'Window menu for Disks' }).tap();
    await page.getByRole('menuitem', { name: 'Snap left' }).tap();
    await page.getByRole('button', { name: 'Start' }).tap();
    await page
      .getByRole('dialog', { name: 'Start menu' })
      .getByRole('row', { name: 'About Vyxis' })
      .tap();
    await about.getByRole('button', { name: 'Window menu for About Vyxis' }).tap();
    await page.getByRole('menuitem', { name: 'Snap right' }).tap();

    await expect
      .poll(async () => (await box(about)).x)
      .toBeGreaterThan(workspace.x + workspace.width / 2);
    const left = await box(disks);
    const right = await box(about);
    expect(left.x).toBe(workspace.x);
    expect(left.width + right.width).toBeLessThanOrEqual(workspace.width);
    expect(right.x + right.width).toBe(workspace.x + workspace.width);
    await expect(disks).toBeVisible();
  });
});
