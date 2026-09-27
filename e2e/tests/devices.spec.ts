import { type Locator } from '@playwright/test';
import { expect, test } from './fixtures';
import { appWindow, signInAsAdmin } from './session';

async function box(locator: Locator) {
  await locator.evaluate((element) =>
    Promise.all(element.getAnimations().map((animation) => animation.finished)),
  );
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
      await page.getByRole('option', { name: 'Disks, shortcut' }).tap();
      await expect(disks).toBeVisible();
      const workspace = await box(page.getByRole('main', { name: 'Desktop' }));
      expect((await box(disks)).width).toBe(workspace.width);
      expect((await box(disks)).height).toBe(workspace.height);
    });

    await test.step('the back gesture returns to the home screen', async () => {
      await page.goBack();
      await expect(disks).toBeHidden();
      await expect(page.getByRole('option', { name: 'Disks, shortcut' })).toBeVisible();
    });

    await test.step('the app switcher shows open apps and switches between them', async () => {
      await page.getByRole('option', { name: 'About Vyxis, shortcut' }).tap();
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

    await test.step('with two apps open, back still returns to the home screen', async () => {
      await page.goBack();
      await expect(about).toBeHidden();
      await expect(disks).toBeHidden();
      await expect(page.getByRole('option', { name: 'Disks, shortcut' })).toBeVisible();
      await page.getByRole('option', { name: 'Disks, shortcut' }).tap();
      await expect(disks).toBeVisible();
      await page.goBack();
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

    await page.getByRole('option', { name: 'Disks, shortcut' }).tap();
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

test.describe('ultrawide', () => {
  test.use({ viewport: { width: 3440, height: 1440 } });

  test('snaps to thirds by dragging and by keyboard', async ({ page }) => {
    await signInAsAdmin(page);
    await page.getByRole('option', { name: 'About Vyxis, shortcut' }).dblclick();
    const about = appWindow(page, 'About Vyxis');
    const workspace = await box(page.getByRole('main', { name: 'Desktop' }));
    const third = Math.round(workspace.width / 3);

    const title = await box(about.getByRole('heading', { name: 'About Vyxis', exact: true }));
    await page.mouse.move(title.x + 20, title.y + 10);
    await page.mouse.down();
    await page.mouse.move(workspace.x + 1, title.y + 200, { steps: 10 });
    await page.mouse.up();
    await expect.poll(async () => (await box(about)).width).toBe(third);

    await page.keyboard.press('Alt+Shift+ArrowLeft');
    await expect
      .poll(async () => (await box(about)).width)
      .toBe(Math.round((workspace.width * 2) / 3));
    await page.keyboard.press('Alt+Shift+ArrowLeft');
    await expect.poll(async () => (await box(about)).width).toBe(Math.round(workspace.width / 2));

    const grip = await box(about.getByRole('heading', { name: 'About Vyxis', exact: true }));
    await page.mouse.move(grip.x + 20, grip.y + 10);
    await page.mouse.down();
    await page.mouse.move(workspace.x + workspace.width / 2, workspace.y + 1, { steps: 10 });
    await page.mouse.up();
    await expect.poll(async () => (await box(about)).x).toBe(workspace.x + third);
    await about.getByRole('button', { name: 'Close' }).click();
  });
});

test.describe('touch screen laptop', () => {
  test.use({ viewport: { width: 1440, height: 900 }, hasTouch: true });

  test('drags a window with a finger', async ({ page }) => {
    await signInAsAdmin(page);
    await page.getByRole('option', { name: 'About Vyxis, shortcut' }).tap();
    const about = appWindow(page, 'About Vyxis');
    const start = await box(about);
    const title = await box(about.getByRole('heading', { name: 'About Vyxis', exact: true }));
    const x = title.x + 20;
    const y = title.y + 10;

    // Playwright has no touch move, so the gesture goes through the DevTools protocol.
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x, y }] });
    for (let step = 1; step <= 10; step++) {
      await cdp.send('Input.dispatchTouchEvent', {
        type: 'touchMove',
        touchPoints: [{ x: x + step * 15, y: y + step * 8 }],
      });
    }
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });

    await expect.poll(async () => (await box(about)).x).toBeCloseTo(start.x + 150, 0);
    expect((await box(about)).y).toBeCloseTo(start.y + 80, 0);
    await about.getByRole('button', { name: 'Close' }).tap();
  });
});
