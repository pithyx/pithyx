import { type Locator, type Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { appWindow, choosePersonalSetting, openFromDesktop, signInAsAdmin } from './session';

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
  const disks = appWindow(page, 'Storage');
  const about = appWindow(page, 'About Pithyx');

  await test.step('open two apps from the desktop', async () => {
    await openFromDesktop(page, 'Storage');
    await openFromDesktop(page, 'About Pithyx');
    await expect(disks).toBeVisible();
    await expect(about).toBeVisible();
    await expect(taskbarEntry(page, /^About Pithyx, active$/)).toBeVisible();
    await expect(taskbarEntry(page, /^Storage$/)).toBeVisible();
    expect(await zIndex(about)).toBeGreaterThan(await zIndex(disks));
  });

  await test.step('a click raises a window', async () => {
    await disks.getByRole('heading', { name: 'Storage' }).click();
    expect(await zIndex(disks)).toBeGreaterThan(await zIndex(about));
    await expect(taskbarEntry(page, /^Storage, active$/)).toBeVisible();
  });

  await test.step('a single-instance app focuses its window instead of opening another', async () => {
    await page.getByRole('button', { name: 'Pithyx menu' }).click();
    await page
      .getByRole('dialog', { name: 'Pithyx menu' })
      .getByRole('row', { name: 'About Pithyx' })
      .click();
    await expect(page.getByRole('region', { name: 'About Pithyx' })).toHaveCount(1);
    expect(await zIndex(about)).toBeGreaterThan(await zIndex(disks));
  });

  await test.step('the taskbar minimizes the active window and restores it', async () => {
    await taskbarEntry(page, /^About Pithyx, active$/).click();
    await expect(about).toBeHidden();
    await expect(taskbarEntry(page, /^About Pithyx, minimized$/)).toBeVisible();
    await expect(taskbarEntry(page, /^Storage, active$/)).toBeVisible();
    await taskbarEntry(page, /^About Pithyx, minimized$/).click();
    await expect(about).toBeVisible();
    await expect(about).toBeFocused();
  });

  await test.step('maximize and restore', async () => {
    const before = await box(about);
    await about.getByRole('button', { name: 'Maximize' }).click();
    const workspace = await page.getByRole('main', { name: 'Desktop' }).boundingBox();
    await expect.poll(async () => (await box(about)).width).toBe(workspace?.width);
    await about.getByRole('button', { name: 'Restore' }).click();
    await expect.poll(async () => (await box(about)).width).toBe(before.width);
  });

  await test.step('close', async () => {
    await about.getByRole('button', { name: 'Close' }).click();
    await expect(about).toHaveCount(0);
    await expect(taskbarEntry(page, /About Pithyx/)).toHaveCount(0);
    await expect(taskbarEntry(page, /^Storage, active$/)).toBeVisible();
  });
});

test('the start menu searches and opens apps', async ({ page }) => {
  const start = page.getByRole('button', { name: 'Pithyx menu' });
  await start.click();
  const menu = page.getByRole('dialog', { name: 'Pithyx menu' });
  await expect(menu.getByRole('searchbox')).toBeFocused();

  await page.keyboard.press('Escape');
  await expect(menu).toBeHidden();
  await expect(start).toBeFocused();

  await start.click();
  await page.keyboard.type('abo');
  await expect(menu.getByRole('option', { name: /About Pithyx/ })).toBeVisible();
  await page.keyboard.press('Enter');
  await expect(menu).toBeHidden();
  await expect(appWindow(page, 'About Pithyx')).toBeVisible();

  await start.click();
  await menu.getByRole('row', { name: 'Storage' }).click();
  await expect(appWindow(page, 'Storage')).toBeVisible();

  await start.click();
  // Right of the menu, which is attached to the taskbar at the left and 760 px wide.
  await page.getByRole('main', { name: 'Desktop' }).click({ position: { x: 1000, y: 300 } });
  await expect(menu).toBeHidden();
});

interface Box {
  x: number;
  y: number;
  width: number;
  height: number;
}

async function box(locator: Locator): Promise<Box> {
  // A window that was just restored plays its entrance, even if only for a millisecond under reduced motion.
  await locator.evaluate((element) =>
    Promise.all(element.getAnimations().map((animation) => animation.finished)),
  );
  const result = await locator.boundingBox();
  if (result === null) {
    throw new Error('element is not visible');
  }
  return result;
}

async function drag(page: Page, from: { x: number; y: number }, to: { x: number; y: number }) {
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  await page.mouse.move(to.x, to.y, { steps: 10 });
  await page.mouse.up();
}

function center(rect: Box) {
  return { x: rect.x + rect.width / 2, y: rect.y + rect.height / 2 };
}

test('drag, resize and snap with the mouse', async ({ page }) => {
  await openFromDesktop(page, 'About Pithyx');
  const about = appWindow(page, 'About Pithyx');
  const title = about.getByRole('heading', { name: 'About Pithyx', exact: true });
  const workspace = await box(page.getByRole('main', { name: 'Desktop' }));

  await test.step('drag by the title bar', async () => {
    const start = await box(about);
    const grip = center(await box(title));
    await drag(page, grip, { x: grip.x + 120, y: grip.y + 60 });
    await expect.poll(async () => (await box(about)).x).toBeCloseTo(start.x + 120, 0);
    expect((await box(about)).y).toBeCloseTo(start.y + 60, 0);
  });

  await test.step('resize from the top left corner, not below the minimum size', async () => {
    const start = await box(about);
    const corner = { x: start.x + 4, y: start.y + 4 };
    await drag(page, corner, { x: corner.x - 80, y: corner.y - 40 });
    await expect.poll(async () => (await box(about)).width).toBeCloseTo(start.width + 80, 0);
    const grown = await box(about);
    expect(grown.height).toBeCloseTo(start.height + 40, 0);
    expect(grown.x + grown.width).toBeCloseTo(start.x + start.width, 0);
    const again = { x: grown.x + 4, y: grown.y + 4 };
    await drag(page, again, { x: again.x + 2000, y: again.y });
    await expect.poll(async () => (await box(about)).width).toBe(320);
  });

  await test.step('snap to the left half by dragging to the edge', async () => {
    const grip = center(await box(title));
    await drag(page, grip, { x: workspace.x + 2, y: grip.y + 100 });
    await expect.poll(async () => (await box(about)).x).toBe(workspace.x);
    expect((await box(about)).width).toBe(Math.round(workspace.width / 2));
    expect((await box(about)).height).toBe(workspace.height);
  });

  await test.step('pulling a snapped window away restores its size', async () => {
    const grip = center(await box(title));
    await drag(page, grip, { x: grip.x + 300, y: grip.y + 150 });
    await expect.poll(async () => (await box(about)).width).toBe(320);
  });

  await test.step('maximize by dragging to the top edge, restore by double click', async () => {
    const grip = center(await box(title));
    await drag(page, grip, { x: grip.x, y: workspace.y + 1 });
    await expect.poll(async () => (await box(about)).width).toBe(workspace.width);
    await title.dblclick();
    await expect.poll(async () => (await box(about)).width).toBe(320);
  });
});

test('keyboard shortcuts and the window menu', async ({ page }) => {
  await openFromDesktop(page, 'About Pithyx');
  const about = appWindow(page, 'About Pithyx');
  await expect(about).toBeFocused();
  const workspace = await box(page.getByRole('main', { name: 'Desktop' }));
  const free = await box(about);

  await page.keyboard.press('Alt+Shift+ArrowRight');
  await expect
    .poll(async () => (await box(about)).x)
    .toBe(workspace.x + Math.round(workspace.width / 2));
  await page.keyboard.press('Alt+Shift+ArrowUp');
  await expect.poll(async () => (await box(about)).width).toBe(workspace.width);
  await page.keyboard.press('Alt+Shift+ArrowDown');
  await expect.poll(async () => (await box(about)).width).toBe(free.width);
  await page.keyboard.press('Alt+Shift+ArrowDown');
  await expect(about).toBeHidden();
  await page.keyboard.press('Alt+Shift+PageDown');
  await expect(about).toBeVisible();

  await about.getByRole('button', { name: 'Window menu for About Pithyx' }).click();
  await page.getByRole('menuitem', { name: 'Move' }).click();
  const before = await box(about);
  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('Shift+ArrowDown');
  await page.keyboard.press('Enter');
  await expect.poll(async () => (await box(about)).x).toBeCloseTo(before.x + 32, 0);
  expect((await box(about)).y).toBeCloseTo(before.y + 1, 0);

  await page.keyboard.press('Alt+Shift+Space');
  await page.getByRole('menuitem', { name: 'Size' }).click();
  const sized = await box(about);
  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('Escape');
  await expect.poll(async () => (await box(about)).width).toBe(sized.width);

  await about.getByRole('button', { name: 'Window menu for About Pithyx' }).click();
  await page.getByRole('menuitem', { name: 'Close' }).click();
  await expect(about).toHaveCount(0);
});

test('the taskbar moves to the left and back', async ({ page }) => {
  const taskbar = page.getByRole('navigation', { name: 'Taskbar' });
  const moveTaskbar = async (position: string) => {
    await choosePersonalSetting(page, 'Taskbar position', position);
  };
  await moveTaskbar('Left');
  const viewport = page.viewportSize();
  await expect.poll(async () => (await box(taskbar)).height).toBe(viewport?.height);
  expect((await box(taskbar)).x).toBe(0);
  const workspace = await box(page.getByRole('main', { name: 'Desktop' }));
  expect(workspace.x).toBe((await box(taskbar)).width);
  await moveTaskbar('Bottom');
  await expect.poll(async () => (await box(taskbar)).width).toBe(viewport?.width);
});

test('signing out closes the windows, so the next sign-in starts with an empty desktop', async ({
  page,
}) => {
  await openFromDesktop(page, 'Storage');
  await expect(page.getByRole('region', { name: 'Storage', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Pithyx menu' }).click();
  const menu = page.getByRole('dialog', { name: 'Pithyx menu' });
  // Administrators may restart and shut down; nothing is pressed here.
  await expect(menu.getByRole('button', { name: 'Power options' })).toBeVisible();
  await menu.getByRole('button', { name: 'Sign out' }).click();
  await expect(page.getByRole('heading', { name: 'Welcome back' })).toBeVisible();

  await signInAsAdmin(page);
  await expect(page.getByRole('region', { name: 'Storage', exact: true })).toHaveCount(0);
});
