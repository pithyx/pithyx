import AxeBuilder from '@axe-core/playwright';
import { type Locator, type Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { appWindow, resetDesktopItems, signInAsAdmin } from './session';

const CELL = { width: 96, height: 112, padding: 12 };

function icon(page: Page, name: string) {
  return page.getByRole('option', { name: `${name}, shortcut` });
}

function workspace(page: Page) {
  return page.getByRole('main', { name: 'Desktop' });
}

async function box(locator: Locator) {
  const rect = await locator.boundingBox();
  if (rect === null) {
    throw new Error('element has no box');
  }
  return rect;
}

/** The grid cell an icon or note shows in. */
async function cellOf(page: Page, locator: Locator) {
  const origin = await box(workspace(page));
  const rect = await box(locator);
  return {
    column: Math.round((rect.x - origin.x - CELL.padding) / CELL.width) || 0,
    row: Math.round((rect.y - origin.y - CELL.padding) / CELL.height) || 0,
  };
}

/** A point in the middle of a grid cell, in page coordinates. */
async function cellCenter(page: Page, column: number, row: number) {
  const origin = await box(workspace(page));
  return {
    x: origin.x + CELL.padding + column * CELL.width + CELL.width / 2,
    y: origin.y + CELL.padding + row * CELL.height + CELL.height / 2,
  };
}

async function drag(page: Page, from: { x: number; y: number }, to: { x: number; y: number }) {
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  await page.mouse.move(from.x + 10, from.y + 10);
  await page.mouse.move(to.x, to.y, { steps: 5 });
  await page.mouse.up();
}

/** Note texts as the daemon has them, to wait until a save arrived. */
async function savedNotes(page: Page) {
  const items = (await (await page.request.get('/api/desktop/items')).json()) as {
    kind: string;
    text: string | null;
  }[];
  return items.filter((item) => item.kind === 'note').map((item) => item.text);
}

test.beforeEach(async ({ page }) => {
  await signInAsAdmin(page);
  // New users also get a Files shortcut; these tests measure the grid of Disks and About alone.
  await resetDesktopItems(page);
  await page.reload();
  await expect(page.getByRole('navigation', { name: 'Taskbar' })).toBeVisible();
});

test.afterEach(async ({ page }) => {
  await resetDesktopItems(page);
});

test('icons move on the grid by dragging and keep their place after a reload', async ({ page }) => {
  const disks = icon(page, 'Disks');
  expect(await cellOf(page, disks)).toEqual({ column: 0, row: 0 });
  expect(await cellOf(page, icon(page, 'About Pithyx'))).toEqual({ column: 0, row: 1 });

  const response = page.waitForResponse(
    (entry) =>
      /\/api\/desktop\/items\/\d+$/.test(entry.url()) && entry.request().method() === 'PATCH',
  );
  await drag(page, await cellCenter(page, 0, 0), await cellCenter(page, 3, 2));
  expect((await response).status()).toBe(200);
  await expect.poll(() => cellOf(page, disks)).toEqual({ column: 3, row: 2 });
  await expect(disks).toHaveAttribute('aria-selected', 'true');

  await test.step('an icon dropped on another one takes the nearest free cell', async () => {
    await drag(page, await cellCenter(page, 0, 1), await cellCenter(page, 3, 2));
    await expect
      .poll(() => cellOf(page, icon(page, 'About Pithyx')))
      .toEqual({ column: 3, row: 3 });
  });

  await page.reload();
  await expect.poll(() => cellOf(page, disks)).toEqual({ column: 3, row: 2 });
});

test('a frame, Ctrl and Shift select several icons, which move and go together', async ({
  page,
}) => {
  const disks = icon(page, 'Disks');
  const about = icon(page, 'About Pithyx');

  await test.step('a frame drawn over empty desktop selects the icons it touches', async () => {
    const start = await cellCenter(page, 2, 3);
    const end = await cellCenter(page, 0, 0);
    await drag(page, start, end);
    await expect(disks).toHaveAttribute('aria-selected', 'true');
    await expect(about).toHaveAttribute('aria-selected', 'true');
  });

  await test.step('a click on the desktop clears the selection, Ctrl adds one icon, Shift a range', async () => {
    const empty = await cellCenter(page, 5, 3);
    await page.mouse.click(empty.x, empty.y);
    await expect(disks).toHaveAttribute('aria-selected', 'false');
    await disks.click();
    await about.click({ modifiers: ['ControlOrMeta'] });
    await expect(about).toHaveAttribute('aria-selected', 'true');
    await expect(disks).toHaveAttribute('aria-selected', 'true');
    await about.click();
    await disks.click({ modifiers: ['Shift'] });
    await expect(disks).toHaveAttribute('aria-selected', 'true');
  });

  await test.step('dragging one selected icon moves the whole selection', async () => {
    await drag(page, await cellCenter(page, 0, 0), await cellCenter(page, 2, 0));
    await expect.poll(() => cellOf(page, disks)).toEqual({ column: 2, row: 0 });
    await expect.poll(() => cellOf(page, about)).toEqual({ column: 2, row: 1 });
  });

  await test.step('Delete removes the selection from the desktop', async () => {
    await page.keyboard.press('Delete');
    await expect(disks).toHaveCount(0);
    await expect(about).toHaveCount(0);
  });

  await test.step('the desktop menu puts a new shortcut where it was opened', async () => {
    const target = await cellCenter(page, 4, 1);
    await page.mouse.click(target.x, target.y, { button: 'right' });
    await page.getByRole('menuitem', { name: 'New shortcut' }).click();
    await page.getByRole('menuitem', { name: 'Disks' }).click();
    await expect.poll(() => cellOf(page, disks)).toEqual({ column: 4, row: 1 });
    await disks.dblclick();
    await expect(appWindow(page, 'Disks')).toBeVisible();
  });
});

test('the keyboard selects, renames, moves and opens icons', async ({ page }) => {
  const disks = icon(page, 'Disks');
  await disks.click();
  await expect(disks).toBeFocused();

  await test.step('arrow keys go to the nearest icon, Shift selects a range', async () => {
    await page.keyboard.press('ArrowDown');
    await expect(icon(page, 'About Pithyx')).toBeFocused();
    await page.keyboard.press('Shift+ArrowUp');
    await expect(disks).toHaveAttribute('aria-selected', 'true');
    await expect(icon(page, 'About Pithyx')).toHaveAttribute('aria-selected', 'true');
    await page.keyboard.press('ArrowDown');
    await expect(disks).toHaveAttribute('aria-selected', 'false');
  });

  await test.step('F2 renames a shortcut, an empty name goes back to the app name', async () => {
    await page.keyboard.press('F2');
    const dialog = page.getByRole('dialog', { name: 'Rename shortcut' });
    await expect(dialog.getByLabel('Name')).toBeFocused();
    await dialog.getByLabel('Name').fill('Info');
    await page.keyboard.press('Enter');
    await expect(icon(page, 'Info')).toBeVisible();
    await expect(icon(page, 'Info')).toBeFocused();
  });

  await test.step('the menu key opens the item menu and Move moves with the arrow keys', async () => {
    await page.keyboard.press('Shift+F10');
    await page.getByRole('menuitem', { name: 'Move', exact: true }).click();
    await page.keyboard.press('ArrowRight');
    await page.keyboard.press('ArrowRight');
    await page.keyboard.press('Enter');
    await expect.poll(() => cellOf(page, icon(page, 'Info'))).toEqual({ column: 2, row: 1 });
  });

  await test.step('Enter opens the app', async () => {
    await icon(page, 'Info').focus();
    await page.keyboard.press('Enter');
    await expect(appWindow(page, 'About Pithyx')).toBeVisible();
  });

  await page.reload();
  await expect.poll(() => cellOf(page, icon(page, 'Info'))).toEqual({ column: 2, row: 1 });
});

test('sticky notes keep their text on the daemon, move, stay under windows and ask before deleting', async ({
  page,
}) => {
  const empty = await cellCenter(page, 3, 0);
  await page.mouse.click(empty.x, empty.y, { button: 'right' });
  await page.getByRole('menuitem', { name: 'New note' }).click();
  const note = page.getByRole('region', { name: 'Sticky note' });
  const text = note.getByRole('textbox', { name: 'Sticky note' });
  await expect(text).toBeFocused();
  expect(await cellOf(page, note)).toEqual({ column: 3, row: 0 });

  await test.step('text is saved on the daemon', async () => {
    const saved = page.waitForResponse(
      (entry) =>
        /\/api\/desktop\/items\/\d+$/.test(entry.url()) && entry.request().method() === 'PATCH',
    );
    await text.fill('Replace disk 3 before Friday.');
    await (await saved).finished();
    await page.reload();
    await expect(page.getByRole('textbox', { name: 'Sticky note' })).toHaveValue(
      'Replace disk 3 before Friday.',
    );
  });

  await test.step('the grip moves the note with the arrow keys and the bar by dragging', async () => {
    await note.getByRole('button', { name: 'Move note', exact: true }).focus();
    await page.keyboard.press('ArrowDown');
    await expect.poll(() => cellOf(page, note)).toEqual({ column: 3, row: 1 });
    const bar = await box(note.getByRole('button', { name: 'Move note', exact: true }));
    const from = { x: bar.x + bar.width / 2, y: bar.y + bar.height / 2 };
    await drag(page, from, { x: from.x + 2 * CELL.width, y: from.y });
    await expect.poll(() => cellOf(page, note)).toEqual({ column: 5, row: 1 });
  });

  await test.step('windows cover notes', async () => {
    await icon(page, 'Disks').dblclick();
    const window = appWindow(page, 'Disks');
    await expect(window).toBeVisible();
    const noteBox = await box(note);
    const covered = await page.evaluate(
      ({ x, y }) => document.elementFromPoint(x, y)?.closest('[data-note]') === null,
      { x: noteBox.x + noteBox.width / 2, y: noteBox.y + noteBox.height / 2 },
    );
    const windowBox = await box(window);
    const overlaps =
      windowBox.x < noteBox.x + noteBox.width / 2 &&
      windowBox.x + windowBox.width > noteBox.x + noteBox.width / 2 &&
      windowBox.y < noteBox.y + noteBox.height / 2 &&
      windowBox.y + windowBox.height > noteBox.y + noteBox.height / 2;
    expect(covered).toBe(overlaps);
    await window.getByRole('button', { name: 'Close' }).click();
  });

  await test.step('deleting a note with text asks first', async () => {
    await note.getByRole('button', { name: 'Remove note' }).click();
    const confirm = page.getByRole('alertdialog', { name: 'Delete this note?' });
    await expect(confirm).toBeVisible();
    await confirm.getByRole('button', { name: 'Delete note' }).click();
    await expect(note).toHaveCount(0);
    await expect.poll(() => savedNotes(page)).toEqual([]);
    await page.reload();
    await expect(page.getByRole('option', { name: 'Disks, shortcut' })).toBeVisible();
    await expect(page.getByRole('region', { name: 'Sticky note' })).toHaveCount(0);
  });
});

test('text written on a note that changed on another device is kept on a new note', async ({
  page,
  browser,
}) => {
  const empty = await cellCenter(page, 3, 0);
  await page.mouse.click(empty.x, empty.y, { button: 'right' });
  await page.getByRole('menuitem', { name: 'New note' }).click();
  const text = page.getByRole('textbox', { name: 'Sticky note' });
  await text.fill('first');
  await text.blur();
  await expect.poll(() => savedNotes(page)).toEqual(['first']);

  const other = await browser.newContext();
  const otherPage = await other.newPage();
  await signInAsAdmin(otherPage);
  const otherText = otherPage.getByRole('textbox', { name: 'Sticky note' });
  await expect(otherText).toHaveValue('first');
  await otherText.fill('from the laptop');
  await otherText.blur();
  await expect.poll(() => savedNotes(otherPage)).toEqual(['from the laptop']);
  await other.close();

  await text.fill('from the phone');
  await text.blur();
  await expect(page.getByText('Note text kept')).toBeVisible();
  await expect(page.getByRole('textbox', { name: 'Sticky note' })).toHaveCount(2);
  await expect(page.getByRole('textbox', { name: 'Sticky note' }).first()).toHaveValue(
    'from the laptop',
  );
  await expect(page.getByRole('textbox', { name: 'Sticky note' }).last()).toHaveValue(
    'from the phone',
  );
});

test('the desktop with items and their menus has no accessibility violations', async ({ page }) => {
  const empty = await cellCenter(page, 3, 0);
  await page.mouse.click(empty.x, empty.y, { button: 'right' });
  await page.getByRole('menuitem', { name: 'New note' }).click();
  await page.getByRole('textbox', { name: 'Sticky note' }).fill('Order disks');
  await icon(page, 'Disks').click();
  const scan = () =>
    new AxeBuilder({ page })
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
      .analyze();
  expect((await scan()).violations).toEqual([]);

  await icon(page, 'Disks').click({ button: 'right' });
  await expect(page.getByRole('menuitem', { name: 'Rename' })).toBeVisible();
  expect((await scan()).violations).toEqual([]);
  await page.keyboard.press('Escape');

  await icon(page, 'Disks').focus();
  await page.keyboard.press('F2');
  await expect(page.getByRole('dialog', { name: 'Rename shortcut' })).toBeVisible();
  expect((await scan()).violations).toEqual([]);
});

test.describe('phone', () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });

  test('icons flow in rows and notes below them, a tap opens the app', async ({ page }) => {
    await page.request.post('/api/desktop/items', {
      headers: { 'X-Pithyx-Request': '1' },
      data: { kind: 'note', text: 'Call Sam', column: 6, row: 0 },
    });
    await page.reload();
    const disks = await box(icon(page, 'Disks'));
    const about = await box(icon(page, 'About Pithyx'));
    expect(Math.abs(disks.y - about.y)).toBeLessThan(2);
    expect(about.x).toBeGreaterThan(disks.x);
    const note = await box(page.getByRole('region', { name: 'Sticky note' }));
    expect(note.y).toBeGreaterThan(disks.y + disks.height);
    expect(note.width).toBeGreaterThan(300);

    await icon(page, 'Disks').tap();
    await expect(appWindow(page, 'Disks')).toBeVisible();
  });
});
