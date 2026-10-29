import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import type { Locator, Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { openFilesInShared, openFromStart, resetDesktopItems, signInAsAdmin } from './session';

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
  await openFilesInShared(page);
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

test('uploads and downloads show their progress in the taskbar and can be cancelled', async ({
  page,
}) => {
  prepare('e2e-transfers', { 'large.bin': 'x'.repeat(3_000_000) });
  await openFolder(page, 'e2e-transfers');
  // Slow links, so the transfers are still running when the test looks at them.
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Network.emulateNetworkConditions', {
    offline: false,
    latency: 0,
    downloadThroughput: 500_000,
    uploadThroughput: 500_000,
  });

  const chooser = page.waitForEvent('filechooser');
  await filesWindow(page).getByRole('button', { name: 'Upload' }).click();
  await (
    await chooser
  ).setFiles({
    name: 'big-upload.bin',
    mimeType: 'application/octet-stream',
    buffer: Buffer.alloc(4_000_000),
  });
  const button = page.getByRole('button', { name: /^1 transfer, \d+ percent$/ });
  await expect(button).toBeVisible();
  await button.click();
  const panel = page.getByRole('dialog', { name: 'Transfers' });
  const upload = panel.getByRole('progressbar', { name: 'Upload of big-upload.bin' });
  await expect(upload).toHaveAttribute('aria-valuetext', /of 4 MB · .*\/s/);
  await panel.getByRole('button', { name: 'Cancel the transfer of big-upload.bin' }).click();
  await expect(upload).toHaveCount(0);
  await expect(page.getByRole('alertdialog')).toHaveCount(0);
  await expect(row(page, 'big-upload.bin')).toHaveCount(0);
  await page.keyboard.press('Escape');

  const download = page.waitForEvent('download');
  await row(page, 'large.bin').dblclick();
  await page.getByRole('button', { name: /^1 transfer/ }).click();
  await expect(
    page
      .getByRole('dialog', { name: 'Transfers' })
      .getByRole('progressbar', { name: 'Download of large.bin' }),
  ).toHaveAttribute('aria-valuetext', /of 3 MB/);
  await cdp.send('Network.emulateNetworkConditions', {
    offline: false,
    latency: 0,
    downloadThroughput: -1,
    uploadThroughput: -1,
  });
  expect((await download).suggestedFilename()).toBe('large.bin');
  await expect(page.getByRole('button', { name: /^1 transfer/ })).toHaveCount(0);
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

test('moves entries onto a breadcrumb segment and uploads onto a share in the sidebar', async ({
  page,
}) => {
  prepare('e2e-crumb/inner', { 'photo.jpg': 'p' });
  await openFolder(page, 'e2e-crumb');
  await enter(page, 'inner');
  const path = filesWindow(page).getByRole('navigation', { name: 'Path' });
  await row(page, 'photo.jpg').dragTo(path.getByRole('button', { name: 'e2e-crumb', exact: true }));
  await expect(row(page, 'photo.jpg')).toHaveCount(0);
  await expect.poll(() => existsSync(join(shared, 'e2e-crumb', 'photo.jpg'))).toBe(true);

  // Files from the device dropped on a share land in its root.
  const dataTransfer = await page.evaluateHandle(() => {
    const transfer = new DataTransfer();
    transfer.items.add(new File(['dropped'], 'e2e-sidebar-drop.txt', { type: 'text/plain' }));
    return transfer;
  });
  const target = filesWindow(page)
    .getByRole('navigation', { name: 'Shares and recycle bin' })
    .getByRole('button', { name: 'Shared', exact: true });
  await target.dispatchEvent('dragover', { dataTransfer });
  await target.dispatchEvent('drop', { dataTransfer });
  await expect
    .poll(
      () =>
        existsSync(join(shared, 'e2e-sidebar-drop.txt')) &&
        readFileSync(join(shared, 'e2e-sidebar-drop.txt'), 'utf8'),
    )
    .toBe('dropped');
});

test('reads a folder ahead while the mouse rests on it', async ({ page }) => {
  prepare('e2e-prefetch/ahead', { 'inside.txt': 'i' });
  await openFolder(page, 'e2e-prefetch');
  const ahead = page.waitForRequest(
    (request) =>
      request.url().includes('/list') &&
      new URL(request.url()).searchParams.get('path') === 'e2e-prefetch/ahead',
  );
  await row(page, 'ahead').hover();
  await ahead;

  // Opening it shows the rows read ahead.
  await enter(page, 'ahead');
  await expect(row(page, 'inside.txt')).toBeVisible();
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

test('a search keeps the matching names of the folder and deletes only what it shows', async ({
  page,
}) => {
  const files: Record<string, string> = { 'notes.txt': '' };
  for (let i = 0; i < 250; i++) {
    files[`Holiday ${String(i)}.jpg`] = '';
  }
  prepare('e2e-search', files);
  await openFolder(page, 'e2e-search');
  await expect(filesWindow(page).getByText('251 items')).toBeVisible();

  const search = filesWindow(page).getByRole('searchbox', { name: 'Search in e2e-search' });
  await search.fill('holiday 24');
  await expect(filesWindow(page).getByText('11 matches')).toBeVisible();
  await expect(row(page, 'Holiday 24.jpg')).toBeVisible();
  await expect(row(page, 'notes.txt')).toHaveCount(0);

  await row(page, 'Holiday 24.jpg').click();
  await page.keyboard.press('Control+a');
  await expect(filesWindow(page).getByText('11 selected')).toBeVisible();
  const request = page.waitForRequest('**/api/files/operations');
  await page.keyboard.press('Delete');
  const body = (await request).postDataJSON() as { items?: unknown[]; allIn?: unknown };
  expect(body.allIn).toBeUndefined();
  expect(body.items).toHaveLength(11);
  await expect(filesWindow(page).getByText('Nothing in this folder has')).toBeVisible();

  await search.fill('');
  await expect(filesWindow(page).getByText('240 items')).toBeVisible();
  expect(existsSync(join(shared, 'e2e-search', 'notes.txt'))).toBe(true);
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

test('administrators find every other home in one list under homes', async ({ page }) => {
  await signInAsAdmin(page);
  const user = await page.request.post('/api/users', {
    headers,
    data: { username: 'e2e-homes', password: 'river-candle-orbit-7' },
  });
  expect(user.ok()).toBe(true);
  await openFromStart(page, 'Files');
  const files = filesWindow(page);
  const sidebar = files.getByRole('navigation', { name: 'Shares and recycle bin' });
  await expect(sidebar.getByRole('button', { name: 'e2e-homes' })).toHaveCount(0);

  await sidebar.getByRole('button', { name: 'homes', exact: true }).click();
  // The homes are listed like the folders of any folder and open with a double click.
  const home = files.getByRole('row', { name: /^e2e-homes/ });
  await home.dblclick();
  const path = files.getByRole('navigation', { name: 'Path' });
  await expect(path).toContainText('homes');
  await expect(path).toContainText('e2e-homes');

  await files.getByRole('button', { name: 'Up one level' }).click();
  await expect(home).toBeVisible();
});

test('a file put on the desktop follows a rename, shows in Files and goes missing when it is gone', async ({
  page,
}) => {
  prepare('e2e-shortcuts', { 'plan.txt': 'x' });
  await openFolder(page, 'e2e-shortcuts');
  const shortcut = (name: string) =>
    page.getByRole('option', { name: `${name}, shortcut to a file or folder` });

  await test.step('Add to desktop puts a shortcut there', async () => {
    await row(page, 'plan.txt').click({ button: 'right' });
    await page.getByRole('menuitem', { name: 'Add to desktop' }).click();
    await expect(shortcut('plan.txt')).toBeVisible();
  });

  await test.step('a rename in Files renames the shortcut', async () => {
    await row(page, 'plan.txt').click();
    await page.keyboard.press('F2');
    await filesWindow(page).getByRole('textbox', { name: 'Rename plan.txt' }).fill('plan-2.txt');
    await page.keyboard.press('Enter');
    await expect(row(page, 'plan-2.txt')).toBeVisible();
    await expect(shortcut('plan-2.txt')).toBeVisible();
  });

  await test.step('Show in Files opens its folder with it selected', async () => {
    await filesWindow(page).getByRole('button', { name: 'Close' }).click();
    await shortcut('plan-2.txt').click({ button: 'right' });
    await page.getByRole('menuitem', { name: 'Show in Files' }).click();
    await expect(row(page, 'plan-2.txt')).toHaveAttribute('aria-selected', 'true');
    await filesWindow(page).getByRole('button', { name: 'Close' }).click();
  });

  await test.step('a target removed past the daemon leaves a missing shortcut to remove', async () => {
    rmSync(join(shared, 'e2e-shortcuts', 'plan-2.txt'));
    await page.reload();
    const missing = page.getByRole('option', {
      name: 'plan-2.txt, shortcut whose target is missing',
    });
    await missing.click({ button: 'right' });
    const menu = page.getByRole('menu');
    await expect(menu.getByRole('menuitem')).toHaveText(['Remove from desktop']);
    await menu.getByRole('menuitem').click();
    await expect(missing).toHaveCount(0);
  });

  await resetDesktopItems(page);
});
