import AxeBuilder from '@axe-core/playwright';
import { existsSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import type { Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { appWindow, openFromStart, openSettingsPage, signInAsAdmin } from './session';

// Simulation: the seeded folder share "Shared" and pools without the Pithyx layout, so no shared folder can be created
// as a dataset (daemon/tests/fixtures/commands/default). Other specs use "Shared"; this one only reads it.
const headers = { 'X-Pithyx-Request': '1' };

async function openSharedFolders(page: Page) {
  await signInAsAdmin(page);
  await openFromStart(page, 'Settings');
  const settings = appWindow(page, 'Settings');
  await openSettingsPage(settings, 'Shared folders');
  await expect(settings.getByRole('heading', { name: 'Shared folders', level: 1 })).toBeVisible();
  return settings;
}

function shareRow(settings: ReturnType<typeof appWindow>, name: string) {
  return settings
    .getByRole('grid', { name: 'Shared folders with their pool, space, description and rights' })
    .getByRole('row', { name: new RegExp(`^${name} `) });
}

test('Shared folders lists the seeded share with its rights', async ({ page }) => {
  const settings = await openSharedFolders(page);

  const shared = shareRow(settings, 'Shared');
  await expect(shared).toContainText('Folder');
  await expect(shared).toContainText('No description');
  await expect(shared).toContainText('Administrators only');
  // A folder share has no dataset, so it has no restore points.
  await expect(shared).toContainText('Not for folder shares');
  await expect(settings.getByRole('button', { name: 'Open Storage' })).toHaveCount(0);
});

test('the restore points dialog explains space, deletes and backups in plain words', async ({
  page,
}) => {
  const settings = await openSharedFolders(page);
  await settings.getByRole('button', { name: 'What are restore points?' }).click();
  const dialog = page.getByRole('dialog', { name: 'What are restore points?' });

  await expect(dialog).toContainText('keeps each one for 7 days');
  await expect(dialog).toContainText('only for files that were changed or deleted after it');
  await expect(dialog).toContainText('frees its space only when the last restore point');
  await expect(dialog).toContainText('ransomware');
  await expect(dialog).toContainText('no replacement for a backup on another device');
  expect(await violations(page)).toEqual([]);
  await dialog.getByRole('button', { name: 'Got it' }).click();
  await expect(dialog).toHaveCount(0);
});

test('the create dialog says when no pool is ready for shared folders', async ({ page }) => {
  const settings = await openSharedFolders(page);
  await settings.getByRole('button', { name: 'Create shared folder…' }).click();
  const dialog = page.getByRole('dialog', { name: 'Create a shared folder' });

  await expect(dialog).toContainText('No pool is ready for shared folders yet.');
  await dialog.getByLabel('Name', { exact: true }).fill('Team files');
  await expect(dialog.getByRole('button', { name: 'Show commands' })).toBeDisabled();
  await dialog.getByRole('button', { name: 'Close' }).click();
  await expect(dialog).toHaveCount(0);
});

test('a shared folder is edited and deleted with its files kept', async ({ page }) => {
  const folder = join(process.env.PITHYX_E2E_DATA ?? '', 'shares', 'e2e-team');
  mkdirSync(folder, { recursive: true });
  const settings = await openSharedFolders(page);
  // Folder shares can still be registered in development; the list follows the shares topic.
  const created = await page.request.post('/api/shares', {
    headers,
    data: { name: 'Team files', path: folder },
  });
  expect(created.ok()).toBe(true);
  const row = shareRow(settings, 'Team files');
  await expect(row).toBeVisible();

  await row.click();
  await settings.getByRole('button', { name: 'Edit', exact: true }).click();
  const edit = page.getByRole('dialog', { name: 'Edit Team files' });
  await edit.getByLabel('Description', { exact: true }).fill('Plans and notes');
  await edit.getByRole('button', { name: 'Save' }).click();
  await expect(edit.getByText('Saved.')).toBeVisible();
  await expect(edit).toContainText('Administrator, full access');
  await edit.getByRole('button', { name: 'Close' }).click();
  await expect(row).toContainText('Plans and notes');

  // The folder is still selected; a second click would clear the selection.
  await settings.getByRole('button', { name: 'Delete…', exact: true }).click();
  const remove = page.getByRole('dialog', { name: 'Delete Team files' });
  await expect(remove.getByRole('radio', { name: /^Delete the data for good/ })).toBeDisabled();
  await remove.getByRole('radio', { name: /^Keep the data/ }).check();
  await remove.getByLabel('Type Team files to confirm').fill('Team files');
  await remove.getByRole('button', { name: 'Show what happens' }).click();
  await expect(remove).toContainText('No command runs.');
  await remove.getByRole('button', { name: 'Delete shared folder' }).click();
  await expect(remove).toContainText('Done. Every command ran.');
  await remove.getByRole('button', { name: 'Close' }).click();

  await expect(row).toHaveCount(0);
  await expect(shareRow(settings, 'Shared')).toBeVisible();
  expect(existsSync(folder)).toBe(true);
});

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

test('Shared folders and the delete dialog have no accessibility violations', async ({ page }) => {
  const settings = await openSharedFolders(page);
  await expect(shareRow(settings, 'Shared')).toBeVisible();
  expect(await violations(page)).toEqual([]);
  await shareRow(settings, 'Shared').click();
  await settings.getByRole('button', { name: 'Delete…', exact: true }).click();
  const remove = page.getByRole('dialog', { name: 'Delete Shared' });
  await expect(remove.getByRole('radio', { name: /^Keep the data/ })).toBeVisible();
  expect(await violations(page)).toEqual([]);
  await remove.getByRole('button', { name: 'Close' }).click();
});

test.describe('phone', () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });

  test('Shared folders fit as cards', async ({ page }) => {
    const settings = await openSharedFolders(page);
    await expect(shareRow(settings, 'Shared')).toBeVisible();
    expect(await violations(page)).toEqual([]);
    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - window.innerWidth,
    );
    expect(overflow).toBeLessThanOrEqual(0);
  });
});
