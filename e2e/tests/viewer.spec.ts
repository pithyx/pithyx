import AxeBuilder from '@axe-core/playwright';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { crc32, deflateSync } from 'node:zlib';
import type { Locator, Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { appWindow, openAccountMenu, openFilesInShared, signInAsAdmin } from './session';

// The simulated share "Shared" lives in the daemon's data directory (playwright.config.ts).
const shared = join(process.env.PITHYX_E2E_DATA ?? '', 'shares', 'shared');

/** A real PNG of the given size, made here so the tests need no binary fixtures. */
function png(width: number, height: number): Buffer {
  const chunk = (type: string, data: Buffer) => {
    const length = Buffer.alloc(4);
    length.writeUInt32BE(data.length);
    const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(body));
    return Buffer.concat([length, body, crc]);
  };
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header.set([8, 2, 0, 0, 0], 8);
  const rows = Buffer.concat(
    Array.from({ length: height }, (_, y) =>
      Buffer.from([0, ...Array.from({ length: width }, (_, x) => [x * 6, y * 8, 120]).flat()]),
    ),
  );
  return Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    chunk('IHDR', header),
    chunk('IDAT', deflateSync(rows)),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

/** A one-page PDF with a line of text; pdf.js repairs the missing cross-reference table. */
const pdf = [
  '%PDF-1.4',
  '1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj',
  '2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj',
  '3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >> endobj',
  '4 0 obj << /Length 44 >> stream',
  'BT /F1 24 Tf 40 100 Td (Pithyx) Tj ET',
  'endstream endobj',
  '5 0 obj << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> endobj',
  'trailer << /Root 1 0 R >>',
  '%%EOF',
].join('\n');

function prepare(name: string, files: Record<string, string | Buffer>): void {
  const folder = join(shared, name);
  mkdirSync(folder, { recursive: true });
  for (const [file, content] of Object.entries(files)) {
    writeFileSync(join(folder, file), content);
  }
}

function filesWindow(page: Page): Locator {
  return page.getByRole('region', { name: /Files$/ });
}

function grid(page: Page): Locator {
  return filesWindow(page).getByRole('grid', { name: 'Folder contents' });
}

async function openFolder(page: Page, folder: string) {
  await signInAsAdmin(page);
  await openFilesInShared(page);
  await grid(page)
    .getByRole('row', { name: new RegExp(`^${folder}`) })
    .dblclick();
  await expect(filesWindow(page).getByRole('navigation', { name: 'Path' })).toContainText(folder);
}

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];

async function violations(page: Page) {
  const result = await new AxeBuilder({ page })
    .withTags(TAGS)
    .exclude('[data-live-announcer]')
    .analyze();
  return result.violations.map(
    (violation) =>
      `${violation.id}: ${violation.help} (${violation.nodes.map((node) => node.target.join(' ')).join(', ')})`,
  );
}

test('thumbnails replace the icons of images in the list and as tiles', async ({ page }) => {
  prepare('e2e-thumbnails', {
    'beach.png': png(400, 300),
    'broken.png': 'not an image',
    'notes.txt': 'hello',
  });
  await openFolder(page, 'e2e-thumbnails');

  const listThumbnail = grid(page)
    .getByRole('row', { name: /^beach\.png/ })
    .locator('img');
  await expect(listThumbnail).toHaveAttribute('src', /\/thumbnail\?.*size=small/);
  await expect
    .poll(() => listThumbnail.evaluate((image: HTMLImageElement) => image.naturalWidth))
    .toBe(64);

  await filesWindow(page).getByRole('button', { name: 'Show as tiles' }).click();
  const tile = grid(page).getByRole('gridcell', { name: /^beach\.png/ });
  await expect(tile.locator('img')).toHaveAttribute('src', /size=large/);
  await expect
    .poll(() => tile.locator('img').evaluate((image: HTMLImageElement) => image.naturalWidth))
    .toBe(256);
  // A file that only claims to be an image keeps its icon.
  await expect(
    grid(page)
      .getByRole('gridcell', { name: /^broken\.png/ })
      .locator('img'),
  ).toHaveCount(0);
  expect(await violations(page)).toEqual([]);

  // The arrow keys move across the tiles; Enter opens the viewer.
  await tile.click();
  await page.keyboard.press('ArrowRight');
  await expect(grid(page).getByRole('gridcell', { name: /^broken\.png/ })).toHaveAttribute(
    'aria-selected',
    'true',
  );
  await filesWindow(page).getByRole('button', { name: 'Show as list' }).click();
});

test('a double click opens images in the viewer, which goes through the folder', async ({
  page,
}) => {
  prepare('e2e-viewer', { 'a.png': png(40, 30), 'b.png': png(30, 40), 'c.zip': 'PK' });
  await openFolder(page, 'e2e-viewer');

  await grid(page)
    .getByRole('row', { name: /^a\.png/ })
    .dblclick();
  const viewer = appWindow(page, 'a.png');
  await expect(viewer.getByRole('img', { name: 'a.png' })).toBeVisible();
  await expect(viewer).toContainText('1 of 2');
  expect(await violations(page)).toEqual([]);

  await viewer.getByRole('button', { name: 'Next file' }).click();
  const next = appWindow(page, 'b.png');
  await expect(next.getByRole('img', { name: 'b.png' })).toBeVisible();
  await next.getByRole('button', { name: 'Zoom in' }).click();
  await expect(next).toContainText('150 %');
});

test('HTML and SVG never run: HTML shows as its source, SVG only as a picture', async ({
  page,
}) => {
  prepare('e2e-unsafe', {
    'page.html': '<script>window.pwned = 1</script><b>bold</b>',
    'drawing.svg':
      '<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" onload="window.pwned = 2"><script>window.pwned = 3</script><rect width="20" height="20"/></svg>',
  });
  await openFolder(page, 'e2e-unsafe');

  await grid(page)
    .getByRole('row', { name: /^page\.html/ })
    .dblclick();
  const html = appWindow(page, 'page.html');
  await expect(html.getByText('<script>window.pwned = 1</script><b>bold</b>')).toBeVisible();
  await expect(html.locator('b')).toHaveCount(0);
  await html.getByRole('button', { name: 'Close' }).click();

  await grid(page)
    .getByRole('row', { name: /^drawing\.svg/ })
    .dblclick();
  await expect(
    appWindow(page, 'drawing.svg').getByRole('img', { name: 'drawing.svg' }),
  ).toBeVisible();

  expect(await page.evaluate(() => (window as { pwned?: number }).pwned)).toBeUndefined();
});

test('Open with lists the viewer and the download', async ({ page }) => {
  prepare('e2e-open-with', { 'notes.md': '# Notes' });
  await openFolder(page, 'e2e-open-with');

  await grid(page)
    .getByRole('row', { name: /^notes\.md/ })
    .click({ button: 'right' });
  await page.getByRole('menuitem', { name: 'Open with…' }).click();
  const dialog = page.getByRole('dialog', { name: 'Open notes.md with' });
  await expect(dialog.getByRole('button', { name: 'Download' })).toBeVisible();
  await dialog.getByRole('button', { name: 'Viewer' }).click();

  await expect(appWindow(page, 'notes.md').getByText('# Notes')).toBeVisible();
});

test('Open with remembers a choice for the file type until it is forgotten', async ({ page }) => {
  prepare('e2e-always', { 'todo.md': '# Todo', 'plan.md': '# Plan' });
  await openFolder(page, 'e2e-always');
  const todo = grid(page).getByRole('row', { name: /^todo\.md/ });
  const plan = grid(page).getByRole('row', { name: /^plan\.md/ });

  await todo.click({ button: 'right' });
  await page.getByRole('menuitem', { name: 'Open with…' }).click();
  const dialog = page.getByRole('dialog', { name: 'Open todo.md with' });
  // The checkbox input is visually hidden behind the drawn box, so a user clicks the label.
  await dialog.getByText('Always open .md files this way').click();
  await expect(
    dialog.getByRole('checkbox', { name: 'Always open .md files this way' }),
  ).toBeChecked();
  const first = page.waitForEvent('download');
  await dialog.getByRole('button', { name: 'Download' }).click();
  expect((await first).suggestedFilename()).toBe('todo.md');

  // Another file of the type now downloads on a double click instead of opening in the viewer.
  const second = page.waitForEvent('download');
  await plan.dblclick();
  expect((await second).suggestedFilename()).toBe('plan.md');

  await openAccountMenu(page);
  await page.getByRole('menuitem', { name: 'Default apps' }).click();
  const defaults = page.getByRole('dialog', { name: 'Default apps' });
  await expect(defaults.getByText('.md')).toBeVisible();
  await defaults.getByRole('button', { name: 'Forget the choice for .md files' }).click();
  await expect(defaults.getByText(/not chosen a default app yet/)).toBeVisible();
  await defaults.getByRole('button', { name: 'Close' }).click();

  await plan.dblclick();
  await expect(appWindow(page, 'plan.md').getByText('# Plan')).toBeVisible();
});

test('PDFs open page by page, with pdf.js loaded only then', async ({ page }) => {
  prepare('e2e-pdf', { 'manual.pdf': pdf });
  const pdfChunks: string[] = [];
  page.on('request', (request) => {
    if (/PdfView|pdf\.worker/.test(request.url())) {
      pdfChunks.push(request.url());
    }
  });
  await openFolder(page, 'e2e-pdf');
  expect(pdfChunks).toEqual([]);

  await grid(page)
    .getByRole('row', { name: /^manual\.pdf/ })
    .dblclick();

  const pageOne = appWindow(page, 'manual.pdf').getByRole('img', { name: 'Page 1 of 1' });
  await expect(pageOne).toBeVisible();
  await expect
    .poll(() => pageOne.evaluate((canvas: HTMLCanvasElement) => canvas.width))
    .toBeGreaterThan(100);
  expect(pdfChunks.length).toBeGreaterThan(0);
});
