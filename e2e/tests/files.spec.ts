import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import type { Locator, Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { openFromStart, signInAsAdmin } from './session';

// The simulated share "Shared" lives in the daemon's data directory (playwright.config.ts).
const shared = join(process.env.PITHYX_E2E_DATA ?? '', 'shares', 'shared');
const headers = { 'X-Pithyx-Request': '1' };

/** A fresh folder at the share root for one test, with the given files in it. */
function prepare(name: string, files: Record<string, string> = {}): string {
  const folder = join(shared, name);
  mkdirSync(folder, { recursive: true });
  for (const [file, content] of Object.entries(files)) {
    writeFileSync(join(folder, file), content);
  }
  return folder;
}

function filesWindow(page: Page): Locator {
  return page.getByRole('region', { name: /Files$/ });
}

function grid(page: Page): Locator {
  return filesWindow(page).getByRole('grid', { name: 'Folder contents' });
}

function row(page: Page, name: string): Locator {
  return grid(page).getByRole('row', { name: new RegExp(`^${name.replace(/[.()]/g, '\\$&')}`) });
}

/** Goes into a folder of the list and waits until Files shows it, so keys go to the new folder. */
async function enter(page: Page, name: string) {
  await row(page, name).dblclick();
  await expect(
    filesWindow(page)
      .getByRole('navigation', { name: 'Path' })
      .getByRole('button', { name, exact: true }),
  ).toHaveAttribute('aria-current', 'page');
}

/** Opens Files and goes into a folder at the share root. */
async function openFolder(page: Page, folder: string) {
  await signInAsAdmin(page);
  await openFromStart(page, 'Files');
  await expect(filesWindow(page)).toBeVisible();
  await row(page, folder).dblclick();
  await expect(filesWindow(page).getByRole('navigation', { name: 'Path' })).toContainText(folder);
}

test('lists a folder, creates, renames and uploads, and downloads with a double click', async ({
  page,
}) => {
  prepare('e2e-basics', { 'notes.txt': 'hello', 'backup.zip': 'PK' });
  await openFolder(page, 'e2e-basics');
  await expect(row(page, 'notes.txt')).toBeVisible();

  await filesWindow(page).getByRole('button', { name: 'New folder' }).click();
  const input = filesWindow(page).getByRole('textbox', { name: 'Rename New folder' });
  await expect(input).toBeFocused();
  await input.fill('Photos');
  await input.press('Enter');
  await expect(row(page, 'Photos')).toBeVisible();

  await row(page, 'notes.txt').click();
  await page.keyboard.press('F2');
  await filesWindow(page).getByRole('textbox', { name: 'Rename notes.txt' }).fill('diary.txt');
  await page.keyboard.press('Enter');
  await expect(row(page, 'diary.txt')).toBeVisible();
  await expect(row(page, 'notes.txt')).toHaveCount(0);

  const chooser = page.waitForEvent('filechooser');
  await filesWindow(page).getByRole('button', { name: 'Upload' }).click();
  await (
    await chooser
  ).setFiles({ name: 'report.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.7') });
  await expect(row(page, 'report.pdf')).toBeVisible();
  expect(readFileSync(join(shared, 'e2e-basics', 'report.pdf'), 'utf8')).toBe('%PDF-1.7');

  // Files the viewer does not show are downloaded.
  const download = page.waitForEvent('download');
  await row(page, 'backup.zip').dblclick();
  expect((await download).suggestedFilename()).toBe('backup.zip');
});

test('copies and moves with the clipboard and asks about a name that exists', async ({ page }) => {
  prepare('e2e-clipboard/target', { 'b.txt': 'old' });
  prepare('e2e-clipboard', { 'a.txt': 'a', 'b.txt': 'new' });
  await openFolder(page, 'e2e-clipboard');

  await row(page, 'a.txt').click();
  await page.keyboard.press('Control+x');
  await enter(page, 'target');
  await page.keyboard.press('Control+v');
  await expect(row(page, 'a.txt')).toBeVisible();

  await page.keyboard.press('Backspace');
  await expect(row(page, 'target')).toBeVisible();
  await expect(row(page, 'a.txt')).toHaveCount(0);
  await row(page, 'b.txt').click();
  await page.keyboard.press('Control+c');
  await enter(page, 'target');
  await page.keyboard.press('Control+v');
  const dialog = page.getByRole('dialog', { name: 'Names already exist' });
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: 'Keep both' }).click();
  await expect(row(page, 'b (2).txt')).toBeVisible();
  expect(readFileSync(join(shared, 'e2e-clipboard', 'target', 'b.txt'), 'utf8')).toBe('old');
});

test('moves entries by dragging them onto a folder', async ({ page }) => {
  prepare('e2e-drag/box');
  prepare('e2e-drag', { 'photo.jpg': 'p' });
  await openFolder(page, 'e2e-drag');
  await row(page, 'photo.jpg').dragTo(row(page, 'box'));
  await expect(row(page, 'photo.jpg')).toHaveCount(0);
  await row(page, 'box').dblclick();
  await expect(row(page, 'photo.jpg')).toBeVisible();
});

test('deletes into the recycle bin, restores, and deletes for good only after asking', async ({
  page,
}) => {
  prepare('e2e-trash', { 'keep.txt': 'k', 'gone.txt': 'g' });
  await openFolder(page, 'e2e-trash');

  await row(page, 'keep.txt').click();
  await page.keyboard.press('Delete');
  await expect(row(page, 'keep.txt')).toHaveCount(0);

  await filesWindow(page).getByRole('button', { name: 'Recycle bin of Shared' }).click();
  const bin = filesWindow(page).getByRole('listbox', { name: 'Recycle bin of Shared' });
  await bin.getByRole('option', { name: 'keep.txt' }).click();
  await filesWindow(page).getByRole('button', { name: 'Restore' }).click();
  await expect(bin.getByRole('option', { name: 'keep.txt' })).toHaveCount(0);

  await filesWindow(page).getByRole('button', { name: 'Back' }).click();
  await expect(row(page, 'keep.txt')).toBeVisible();

  await row(page, 'gone.txt').click();
  await page.keyboard.press('Shift+Delete');
  const confirm = page.getByRole('alertdialog', { name: 'Delete permanently?' });
  await confirm.getByRole('button', { name: 'Delete permanently' }).click();
  await expect(row(page, 'gone.txt')).toHaveCount(0);
  await filesWindow(page).getByRole('button', { name: 'Recycle bin of Shared' }).click();
  await expect(bin.getByRole('option', { name: 'gone.txt' })).toHaveCount(0);
});

test('shows changes made elsewhere while the folder is open', async ({ page, browser }) => {
  const folder = prepare('e2e-live');
  await openFolder(page, 'e2e-live');
  await expect(filesWindow(page).getByText('This folder is empty.')).toBeVisible();

  // A second session of the same user changes the folder through the API, and the file system changes directly.
  const other = await browser.newContext({ storageState: await page.context().storageState() });
  const shares = (await (await other.request.get('/api/shares')).json()) as { id: number }[];
  const response = await other.request.post(`/api/files/${String(shares[0]?.id)}/folders`, {
    headers,
    data: { path: 'e2e-live/From elsewhere' },
  });
  expect(response.ok()).toBe(true);
  await expect(row(page, 'From elsewhere')).toBeVisible();
  writeFileSync(join(folder, 'written.txt'), 'x');
  await expect(row(page, 'written.txt')).toBeVisible();
  await other.close();
});

test('selecting everything in a large folder sends no list of names', async ({ page }) => {
  const files: Record<string, string> = {};
  for (let i = 0; i < 3000; i++) {
    files[`file ${String(i)}.txt`] = '';
  }
  prepare('e2e-large', files);
  await openFolder(page, 'e2e-large');
  await expect(filesWindow(page).getByText('3,000 items')).toBeVisible();

  await row(page, 'file 0.txt').click();
  await page.keyboard.press('Control+a');
  await expect(filesWindow(page).getByText('3,000 selected')).toBeVisible();
  await page.keyboard.press('Control+Space');
  await expect(filesWindow(page).getByText('2,999 selected')).toBeVisible();

  const request = page.waitForRequest('**/api/files/operations');
  await page.keyboard.press('Delete');
  const body = (await request).postDataJSON() as {
    items?: unknown;
    allIn?: { folder: { path: string }; except: string[] };
  };
  expect(body.items).toBeUndefined();
  expect(body.allIn).toEqual({
    folder: expect.objectContaining({ path: 'e2e-large' }),
    except: ['file 0.txt'],
  });
  await expect(filesWindow(page).getByText('1 item', { exact: true })).toBeVisible();
});

test('works with the keyboard alone', async ({ page }) => {
  prepare('e2e-keys/inner', { 'deep.txt': 'd' });
  await openFolder(page, 'e2e-keys');
  // The keys act on the rows, so the listing must be there before the first one.
  await expect(row(page, 'inner')).toBeVisible();
  await grid(page).focus();
  await page.keyboard.press('Home');
  await page.keyboard.press('Enter');
  await expect(row(page, 'deep.txt')).toBeVisible();
  await page.keyboard.press('Backspace');
  await expect(row(page, 'inner')).toBeVisible();
  await page.keyboard.press('Shift+F10');
  await expect(page.getByRole('menu', { name: 'Actions for 1 item' })).toBeVisible();
  await page.keyboard.press('Escape');
});

test('a right-click while the menu is open moves the menu to the new row', async ({ page }) => {
  prepare('e2e-menus', { 'a.txt': 'a', 'b.txt': 'b' });
  await openFolder(page, 'e2e-menus');
  await expect(row(page, 'b.txt')).toBeVisible();
  await page.evaluate(() => {
    const prevented: boolean[] = [];
    Object.assign(window, { rightClicks: prevented });
    window.addEventListener('contextmenu', (event) => {
      if (event.isTrusted) {
        prevented.push(event.defaultPrevented);
      }
    });
  });
  const menu = page.getByRole('menu', { name: 'Actions for 1 item' });
  const at = async (name: string) => {
    const rect = await row(page, name).boundingBox();
    if (rect === null) {
      throw new Error('row has no box');
    }
    return { x: rect.x + 40, y: rect.y + rect.height / 2 };
  };
  const openedAt = async (point: { x: number; y: number }) => {
    const rect = await menu.boundingBox();
    return (
      rect !== null && Math.abs(rect.x - point.x) < 8 && rect.y >= point.y && rect.y - point.y < 12
    );
  };

  // The menu opens below the pointer, so the second row is the one above it.
  const first = await at('b.txt');
  await page.mouse.click(first.x, first.y, { button: 'right' });
  await expect.poll(() => openedAt(first)).toBe(true);
  const second = await at('a.txt');
  await page.mouse.click(second.x, second.y, { button: 'right' });
  await expect.poll(() => openedAt(second)).toBe(true);
  await expect(menu).toHaveCount(1);
  await expect(row(page, 'a.txt')).toHaveAttribute('aria-selected', 'true');
  await expect(row(page, 'b.txt')).not.toHaveAttribute('aria-selected', 'true');
  // The second right-click landed on the open menu, which kept the browser's menu away.
  expect(
    await page.evaluate(() => (window as unknown as { rightClicks: boolean[] }).rightClicks),
  ).toEqual([true, true]);
  await page.keyboard.press('Escape');
});
