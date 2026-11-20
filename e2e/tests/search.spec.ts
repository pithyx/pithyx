import { expect, test } from './fixtures';
import { signInAsAdmin } from './session';

// The names are written before the daemon starts (playwright.config.ts), so its first walk indexed them.

test('the menu finds files by name and shows all of them in Files', async ({ page }) => {
  await signInAsAdmin(page);
  await page.getByRole('button', { name: 'Pithyx menu' }).click();
  const menu = page.getByRole('dialog', { name: 'Pithyx menu' });
  await menu.getByRole('searchbox', { name: 'Search apps, settings and files' }).fill('zephyr');

  const results = menu.getByRole('listbox', { name: 'Search results' });
  await expect(results.getByRole('option', { name: /^Zephyr plans/ })).toContainText(
    'Shared/e2e-find',
  );
  await expect(results.getByRole('option', { name: /^zephyr-notes\.txt/ })).toBeVisible();
  await expect(results.getByRole('option', { name: /^zephyr budget\.ods/ })).toContainText(
    'Shared/e2e-find/Zephyr plans',
  );

  await results.getByRole('option', { name: 'Show all in Files' }).click();
  const files = page.getByRole('region', { name: /Files$/ });
  const hits = files.getByRole('grid', { name: 'Search results for “zephyr”' });
  // A header row and the three matches.
  await expect(hits.getByRole('row')).toHaveCount(4);
  await expect(files.getByText('3 matches')).toBeVisible();

  // Typing narrows the search in place; a folder hit goes into the folder.
  await files.getByRole('searchbox', { name: 'Search all places' }).fill('zephyr budget');
  await expect(hits.getByRole('row')).toHaveCount(2);
  await files.getByRole('searchbox', { name: 'Search all places' }).fill('zephyr plans');
  // The whole name comes first.
  await expect(hits.getByRole('row').nth(1)).toContainText('Zephyr plans');
  await hits.getByRole('row', { name: /^Zephyr plans/ }).dblclick();
  await expect(
    files.getByRole('navigation', { name: 'Path' }).getByRole('button', { name: 'Zephyr plans' }),
  ).toHaveAttribute('aria-current', 'page');
  await expect(files.getByRole('grid', { name: 'Folder contents' })).toContainText(
    'zephyr budget.ods',
  );

  // A folder's own search leads on to every place.
  await files.getByRole('searchbox', { name: 'Search in Zephyr plans' }).fill('notes');
  await files.getByRole('button', { name: 'Search all places' }).click();
  const notes = files.getByRole('grid', { name: 'Search results for “notes”' });
  await expect(notes.getByRole('row', { name: /^zephyr-notes\.txt/ })).toBeVisible();

  await files.getByRole('button', { name: 'Back' }).click();
  await expect(files.getByRole('navigation', { name: 'Path' })).toContainText('Zephyr plans');
});
