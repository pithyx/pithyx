import type { Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { openFromStart, signInAsAdmin } from './session';

// Runs on http://pithyx.test:7484 (playwright.config.ts): a plain HTTP origin that is not localhost, like a box reached
// by its address. Nothing may depend on a secure context there.

function filesWindow(page: Page) {
  return page.getByRole('region', { name: /Files$/ });
}

test('signs in, uploads and drags files without a secure context', async ({ page }) => {
  await signInAsAdmin(page);
  expect(await page.evaluate(() => window.isSecureContext)).toBe(false);
  await openFromStart(page, 'Files');
  const files = filesWindow(page);
  const grid = files.getByRole('grid', { name: 'Folder contents' });
  const folder = `insecure-${String(Date.now())}`;

  await files.getByRole('button', { name: 'New folder' }).click();
  const input = files.getByRole('textbox', { name: 'Rename New folder' });
  await input.fill(folder);
  await input.press('Enter');
  await grid.getByRole('row', { name: new RegExp(`^${folder}`) }).dblclick();
  await expect(files.getByText('This folder is empty.')).toBeVisible();

  const chooser = page.waitForEvent('filechooser');
  await files.getByRole('button', { name: 'Upload' }).click();
  await (
    await chooser
  ).setFiles({ name: 'picked.txt', mimeType: 'text/plain', buffer: Buffer.from('p') });
  await expect(grid.getByRole('row', { name: /^picked\.txt/ })).toBeVisible();

  // Files dropped from the system arrive as a drop event with files in its data transfer.
  const dataTransfer = await page.evaluateHandle(() => {
    const transfer = new DataTransfer();
    transfer.items.add(new File(['dropped'], 'dropped.txt', { type: 'text/plain' }));
    return transfer;
  });
  const list = grid.locator('..');
  await list.dispatchEvent('dragover', { dataTransfer });
  await list.dispatchEvent('drop', { dataTransfer });
  await expect(grid.getByRole('row', { name: /^dropped\.txt/ })).toBeVisible();

  await files.getByRole('button', { name: 'New folder' }).click();
  const box = files.getByRole('textbox', { name: 'Rename New folder' });
  await box.fill('box');
  await box.press('Enter');
  // Drag only once the new folder is named and listed; a drop during the rename can land on the list instead.
  await expect(box).toHaveCount(0);
  await expect(grid.getByRole('row', { name: /^box/ })).toBeVisible();
  await grid
    .getByRole('row', { name: /^picked\.txt/ })
    .dragTo(grid.getByRole('row', { name: /^box/ }));
  await expect(grid.getByRole('row', { name: /^picked\.txt/ })).toHaveCount(0);
  await grid.getByRole('row', { name: /^box/ }).dblclick();
  await expect(grid.getByRole('row', { name: /^picked\.txt/ })).toBeVisible();
});
