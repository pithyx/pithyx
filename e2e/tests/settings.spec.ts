import AxeBuilder from '@axe-core/playwright';
import type { Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { appWindow, openFromStart, signInAsAdmin } from './session';

// The simulated system: the dev VM's disks with a mirror (sde, sdf) and a single-disk pool (sdg), an old ZFS label on
// sdb, and the system on sda (daemon/tests/fixtures/commands/default).

async function openStorage(page: Page) {
  await signInAsAdmin(page);
  await openFromStart(page, 'Settings');
  const settings = appWindow(page, 'Settings');
  await expect(settings.getByRole('heading', { name: 'Storage', level: 2 })).toBeVisible();
  return settings;
}

function diskRow(settings: ReturnType<typeof appWindow>, name: string) {
  return settings
    .getByRole('table', { name: 'Disks and whether a new pool may use them' })
    .getByRole('row', { name: new RegExp(`^${name} `) });
}

test('the Storage page shows the pools and whether each disk may be used', async ({ page }) => {
  const settings = await openStorage(page);

  const mirror = settings.getByRole('article', { name: 'pithyxtestm' });
  await expect(mirror).toContainText('Healthy');
  await expect(mirror).toContainText('Mirror of 2 disks');
  await expect(settings.getByRole('article', { name: 'pithyxtests' })).toContainText('Single disk');

  await expect(diskRow(settings, 'sda')).toContainText('Holds the system. Never used for pools.');
  await expect(diskRow(settings, 'sdb')).toContainText('Needs confirmation');
  await expect(diskRow(settings, 'sdc')).toContainText('Empty and ready.');
  await expect(diskRow(settings, 'sde')).toContainText('Part of the pool pithyxtestm.');
  await expect(settings).toContainText('only the declared test disks can be used');
});

test('the pool preview lists the exact commands and asks for the serial of a disk with data', async ({
  page,
}) => {
  const settings = await openStorage(page);
  await settings.getByRole('button', { name: 'Create pool…' }).click();
  const dialog = page.getByRole('dialog', { name: 'Create a pool' });

  await dialog.getByLabel('Pool name').fill('tank');
  await dialog.getByLabel('Layout').selectOption('mirror');
  await dialog.getByRole('checkbox', { name: /^sdb / }).check();
  await dialog.getByRole('checkbox', { name: /^sdc / }).check();
  await expect(dialog.getByRole('checkbox', { name: /^sda / })).toHaveCount(0);
  await dialog.getByRole('button', { name: 'Show commands' }).click();
  await expect(dialog.getByRole('status')).toContainText('Not every chosen disk can be used');

  await dialog.getByLabel('Type the serial number of sdb (VYXTEST01)').fill('VYXTEST01');
  await dialog.getByRole('button', { name: 'Show commands' }).click();
  const commands = dialog.getByRole('status').getByRole('listitem');
  await expect(commands).toHaveCount(3);
  await expect(commands.nth(0)).toHaveText(
    /^zpool labelclear -f \/dev\/disk\/by-id\/scsi-0QEMU_QEMU_HARDDISK_drive-scsi1-part1$/,
  );
  await expect(commands.nth(2)).toHaveText(
    /zpool create .* tank mirror \/dev\/disk\/by-id\/scsi-0QEMU_QEMU_HARDDISK_drive-scsi1 \/dev\/disk\/by-id\/scsi-0QEMU_QEMU_HARDDISK_drive-scsi2$/,
  );
  await expect(dialog).toContainText('Nothing was run.');
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

test('the Storage page and the preview have no accessibility violations', async ({ page }) => {
  const settings = await openStorage(page);
  await expect(diskRow(settings, 'sda')).toBeVisible();
  expect(await violations(page)).toEqual([]);
  await settings.getByRole('button', { name: 'Create pool…' }).click();
  await expect(page.getByRole('dialog', { name: 'Create a pool' })).toBeVisible();
  expect(await violations(page)).toEqual([]);
});

test.describe('phone', () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });

  test('the Storage page fits and has no accessibility violations', async ({ page }) => {
    const settings = await openStorage(page);
    await expect(diskRow(settings, 'sda')).toBeVisible();
    expect(await violations(page)).toEqual([]);
    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - window.innerWidth,
    );
    expect(overflow).toBeLessThanOrEqual(0);
  });
});
