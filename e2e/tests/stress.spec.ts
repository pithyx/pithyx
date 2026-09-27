import { type Locator, type Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { appWindow, openFromDesktop, signInAsAdmin } from './session';

interface Box {
  x: number;
  y: number;
  width: number;
  height: number;
}

async function box(locator: Locator): Promise<Box> {
  await locator.evaluate((element) =>
    Promise.all(element.getAnimations().map((animation) => animation.finished)),
  );
  const result = await locator.boundingBox();
  if (result === null) {
    throw new Error('element is not visible');
  }
  return result;
}

/** Collects uncaught errors and console errors, so a test can require a clean run. */
function watchErrors(page: Page): string[] {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  page.on('console', (message) => {
    if (message.type() === 'error') {
      errors.push(message.text());
    }
  });
  return errors;
}

/** Saves a layout for this browser, the way the desktop does (without a version, so unconditionally). */
async function seedLayout(page: Page, layout: unknown) {
  const status = await page.evaluate(async (data) => {
    const response = await fetch('/api/desktop/layout', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json', 'X-Vyxis-Request': '1' },
      body: JSON.stringify(data),
    });
    return response.status;
  }, layout);
  expect(status).toBe(204);
}

/** At least 48 px of free title bar (the title, between the window menu and the buttons) stay inside. */
async function expectGrabbable(page: Page, window: Locator) {
  const workspace = await box(page.getByRole('main', { name: 'Desktop' }));
  const free = await box(window.getByRole('heading', { level: 2 }).first());
  const inside =
    Math.min(free.x + free.width, workspace.x + workspace.width) - Math.max(free.x, workspace.x);
  expect(inside).toBeGreaterThanOrEqual(48 - 0.5);
}

test('windows stay reachable after the screen gets smaller', async ({ page }) => {
  await page.setViewportSize({ width: 1600, height: 1000 });
  await signInAsAdmin(page);
  // Before sign-in the session probe answers 401, which the browser logs; watch from here on.
  const errors = watchErrors(page);
  await openFromDesktop(page, 'About Vyxis');
  const about = appWindow(page, 'About Vyxis');
  const title = await box(about.getByRole('heading', { name: 'About Vyxis', exact: true }));
  await page.mouse.move(title.x + 20, title.y + 10);
  await page.mouse.down();
  await page.mouse.move(1560, 960, { steps: 10 });
  await page.mouse.up();

  const checkReachable = async () => {
    const workspace = await box(page.getByRole('main', { name: 'Desktop' }));
    const rect = await box(about);
    await expectGrabbable(page, about);
    expect(rect.y).toBeGreaterThanOrEqual(workspace.y - 0.5);
    expect(rect.y + 40).toBeLessThanOrEqual(workspace.y + workspace.height + 0.5);
    expect(rect.width).toBeLessThanOrEqual(workspace.width);
  };
  await checkReachable();

  await page.setViewportSize({ width: 1100, height: 700 });
  await expect(checkReachable).toPass();

  // Pull it back into view with the keyboard, then check a restored layout is clamped the same way.
  await page.keyboard.press('Alt+Shift+ArrowUp');
  await page.keyboard.press('Alt+Shift+ArrowDown');
  await page.setViewportSize({ width: 1600, height: 1000 });
  await page.reload();
  await expect(about).toBeVisible();
  await checkReachable();
  expect(errors).toEqual([]);
});

test('25 windows restore, stack and close', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await signInAsAdmin(page);
  const errors = watchErrors(page);
  const windows = Array.from({ length: 25 }, (_, index) => ({
    id: `w${String(index + 1)}`,
    app: index % 2 === 0 ? 'disks' : 'about',
    screen: 1,
    title: null,
    rect: { x: index * 20, y: index * 20, width: 520, height: 360 },
    placement: { kind: 'free' },
    minimized: false,
    args: null,
  }));
  await seedLayout(page, {
    version: 1,
    taskbar: 'bottom',
    windows,
    order: windows.map((window) => window.id),
    focused: 'w25',
  });
  await page.reload();

  const frames = page.locator('section[data-medium]');
  await expect(frames).toHaveCount(25);
  const layers = await frames.evaluateAll((elements) =>
    elements.map((element) => Number(getComputedStyle(element).zIndex)),
  );
  expect(new Set(layers).size).toBe(25);
  expect(
    await page.getByRole('navigation', { name: 'Taskbar' }).getByRole('listitem').count(),
  ).toBe(25);

  // The bottom window shows only a 20 px strip; a click on it brings it to the top.
  await page.mouse.click(300, 10);
  const first = frames.first();
  const top = Math.max(...layers);
  await expect
    .poll(async () => Number(await first.evaluate((element) => getComputedStyle(element).zIndex)))
    .toBe(top);

  // Close every window as fast as the buttons allow.
  for (let remaining = 25; remaining > 0; remaining--) {
    await page
      .locator('section[data-medium]:focus-within, section[data-medium]:focus')
      .getByRole('button', { name: 'Close' })
      .click();
  }
  await expect(frames).toHaveCount(0);
  await expect(page.getByRole('navigation', { name: 'Taskbar' }).getByRole('listitem')).toHaveCount(
    0,
  );
  expect(errors).toEqual([]);
});

test('rapid opening, closing and shortcuts leave a consistent desktop', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await signInAsAdmin(page);
  const errors = watchErrors(page);
  const disks = appWindow(page, 'Disks');
  const shortcut = page.getByRole('button', { name: 'Disks, shortcut' });

  for (let round = 0; round < 15; round++) {
    await shortcut.dblclick();
    await disks.getByRole('button', { name: 'Close' }).click();
  }
  await expect(disks).toHaveCount(0);
  await expect(page.getByRole('navigation', { name: 'Taskbar' }).getByRole('listitem')).toHaveCount(
    0,
  );

  await openFromDesktop(page, 'About Vyxis');
  const about = appWindow(page, 'About Vyxis');
  await expect(about).toBeFocused();
  const keys = ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowLeft', 'ArrowDown', 'ArrowRight'];
  for (let round = 0; round < 4; round++) {
    for (const key of keys) {
      await page.keyboard.press(`Alt+Shift+${key}`);
    }
  }
  // Snapped right after the last round; one more Down restores it, another minimizes it.
  await page.keyboard.press('Alt+Shift+ArrowDown');
  await expect(about).toBeVisible();
  await page.keyboard.press('Alt+Shift+ArrowDown');
  await expect(about).toBeHidden();
  await page.keyboard.press('Alt+Shift+PageDown');
  await expect(about).toBeVisible();
  const workspace = await box(page.getByRole('main', { name: 'Desktop' }));
  const rect = await box(about);
  await expectGrabbable(page, about);
  await expect(about.getByRole('button', { name: 'Close' })).toBeInViewport();
  expect(rect.y).toBeGreaterThanOrEqual(workspace.y);
  expect(errors).toEqual([]);
});

test('dragging a window causes no long tasks', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await signInAsAdmin(page);
  await openFromDesktop(page, 'Disks');
  await page.getByRole('button', { name: 'Start' }).click();
  await page
    .getByRole('dialog', { name: 'Start menu' })
    .getByRole('row', { name: 'About Vyxis' })
    .click();
  const about = appWindow(page, 'About Vyxis');
  await expect(about).toBeFocused();
  await page.evaluate(() => {
    const target = window as unknown as { longTasks: number[] };
    target.longTasks = [];
    new PerformanceObserver((list) => {
      for (const entry of list.getEntries()) {
        target.longTasks.push(entry.duration);
      }
    }).observe({ type: 'longtask' });
  });

  const title = await box(about.getByRole('heading', { name: 'About Vyxis', exact: true }));
  await page.mouse.move(title.x + 20, title.y + 10);
  await page.mouse.down();
  for (let step = 1; step <= 60; step++) {
    await page.mouse.move(title.x + 20 + step * 8, title.y + 10 + (step % 20) * 4);
  }
  await page.mouse.up();

  const longTasks = await page.evaluate(
    () => (window as unknown as { longTasks: number[] }).longTasks,
  );
  expect(longTasks).toEqual([]);
});
