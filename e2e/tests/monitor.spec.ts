import AxeBuilder from '@axe-core/playwright';
import type { Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { appWindow, openFromStart, signInAsAdmin } from './session';

// The daemon of the E2E run reads the real /proc of the machine it runs on; the test checks the shape, not numbers.

async function openMonitor(page: Page) {
  await signInAsAdmin(page);
  await openFromStart(page, 'Resource Monitor');
  const monitor = appWindow(page, 'Resource Monitor');
  await expect(monitor.getByRole('heading', { name: 'CPU', level: 2 })).toBeVisible();
  return monitor;
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

test('the Resource Monitor shows live CPU, memory, disks and a chart per range', async ({
  page,
}) => {
  const monitor = await openMonitor(page);

  await expect(monitor.getByText(/% in use$/)).toBeVisible();
  await expect(
    monitor.getByRole('img', { name: /^CPU usage over the last minute, now/ }),
  ).toBeVisible();
  await expect(monitor.getByRole('heading', { name: 'Memory', level: 2 })).toBeVisible();
  await expect(monitor.getByText(/used by programs$/)).toBeVisible();
  await expect(monitor.getByRole('heading', { name: 'Temperatures', level: 2 })).toBeVisible();
  await expect(monitor.getByText(/^Up /)).toBeVisible();

  await monitor.getByText('Hour', { exact: true }).click();
  await expect(monitor.getByRole('img', { name: /^CPU usage over the last hour/ })).toBeVisible();
  expect(await violations(page)).toEqual([]);
});

test('the process list is sortable and filterable', async ({ page }) => {
  const monitor = await openMonitor(page);
  await monitor.getByRole('button', { name: 'Processes' }).click();
  const table = monitor.getByRole('table', { name: 'Processes' });
  await expect(table.getByRole('row').nth(1)).toBeVisible();

  const name = table.getByRole('columnheader', { name: 'Name' });
  await name.getByRole('button').click();
  await expect(name).toHaveAttribute('aria-sort', 'ascending');
  await expect(table.getByRole('columnheader', { name: 'CPU' })).toHaveAttribute(
    'aria-sort',
    'none',
  );

  await monitor.getByLabel('Filter by name or user').fill('dotnet');
  await expect(table.getByRole('row').nth(1)).toContainText('dotnet');
  await monitor.getByLabel('Filter by name or user').fill('no process has this name');
  await expect(monitor.getByText('No process matches the filter.')).toBeVisible();
  expect(await violations(page)).toEqual([]);
});

test.describe('phone', () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });

  test('the Resource Monitor fits and has no accessibility violations', async ({ page }) => {
    const monitor = await openMonitor(page);
    await expect(monitor.getByText(/% in use$/)).toBeVisible();
    expect(await violations(page)).toEqual([]);
    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - window.innerWidth,
    );
    expect(overflow).toBeLessThanOrEqual(0);
  });
});
