import AxeBuilder from '@axe-core/playwright';
import type { Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { appWindow, openFromStart, signInAsAdmin } from './session';

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

async function openLogCenter(page: Page) {
  await signInAsAdmin(page);
  await openFromStart(page, 'Log Center');
  const logs = appWindow(page, 'Log Center');
  await expect(logs.getByRole('listbox', { name: 'Log entries' })).toBeVisible();
  return logs;
}

test('the Log Center shows the daemon and app entries, filters and explains an entry', async ({
  page,
}) => {
  const logs = await openLogCenter(page);
  // An app's entry through the daemon API, as the SDK's log calls send it.
  const written = await page.request.post('/api/apps/org.pithyx.files/logs', {
    headers: { 'X-Pithyx-Request': '1' },
    data: { entries: [{ level: 'warn', message: 'e2e upload retried', data: { file: 'a.jpg' } }] },
  });
  expect(written.ok()).toBe(true);
  // The daemon writes its log in batches, about once a second.
  await expect
    .poll(
      async () =>
        (
          (await (await page.request.get('/api/logs?search=e2e%20upload')).json()) as {
            entries: unknown[];
          }
        ).entries.length,
    )
    .toBe(1);
  const list = logs.getByRole('listbox', { name: 'Log entries' });
  await logs.getByRole('button', { name: 'Refresh' }).click();
  await expect(list.getByRole('option').first()).toBeVisible();

  await logs.getByLabel('Search logs').fill('e2e upload');
  const entry = list.getByRole('option', { name: /e2e upload retried/ });
  await expect(entry).toBeVisible();
  await expect(list.getByRole('option')).toHaveCount(1);
  await entry.click();
  const details = logs.getByRole('complementary', { name: 'Entry details' });
  await expect(details.getByText('App · Files')).toBeVisible();
  await expect(details.getByText('a.jpg')).toBeVisible();

  await logs.getByRole('button', { name: 'Warning', exact: true }).click();
  await expect(logs.getByText('No entries match these filters.')).toBeVisible();
  await logs.getByRole('button', { name: 'Warning', exact: true }).click();
  await expect(entry).toBeVisible();
  expect(await violations(page)).toEqual([]);

  const download = page.waitForEvent('download');
  await logs.getByRole('button', { name: 'Export' }).click();
  await page.getByRole('menuitem', { name: 'Export as text' }).click();
  expect((await download).suggestedFilename()).toMatch(/^pithyx-logs-\d{8}-\d{6}\.log$/);
});

test('retention is set in the Log Center and checked by the daemon', async ({ page }) => {
  const logs = await openLogCenter(page);
  await logs.getByRole('button', { name: 'Retention' }).click();
  const dialog = page.getByRole('dialog', { name: 'Log retention' });
  await expect(dialog.getByText(/^They take /)).toBeVisible();
  expect(await violations(page)).toEqual([]);

  await dialog.getByLabel('Largest size in GB').fill('9');
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(dialog.getByText('maxBytes must be between 67108864 and 4294967296.')).toBeVisible();
  await dialog.getByLabel('Largest size in GB').fill('2');
  await dialog.getByLabel('Keep for days').fill('14');
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(dialog).toBeHidden();
  const settings = (await (await page.request.get('/api/logs/settings')).json()) as {
    settings: { retentionDays: number; maxBytes: number };
  };
  expect(settings.settings).toMatchObject({ retentionDays: 14, maxBytes: 2 * 1024 ** 3 });
  await page.request.put('/api/logs/settings', {
    headers: { 'X-Pithyx-Request': '1' },
    data: { ...settings.settings, retentionDays: 30, maxBytes: 4 * 1024 ** 3 },
  });
});

test.describe('phone', () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });

  test('the Log Center fits and has no accessibility violations', async ({ page }) => {
    const logs = await openLogCenter(page);
    await expect(
      logs.getByRole('listbox', { name: 'Log entries' }).getByRole('option').first(),
    ).toBeVisible();
    expect(await violations(page)).toEqual([]);
    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - window.innerWidth,
    );
    expect(overflow).toBeLessThanOrEqual(0);
  });
});
