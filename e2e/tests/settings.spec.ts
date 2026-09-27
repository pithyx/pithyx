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

test('the pool wizard compares layouts, asks about encryption and lists the exact commands', async ({
  page,
}) => {
  const settings = await openStorage(page);
  await settings.getByRole('button', { name: 'Create pool…' }).click();
  const dialog = page.getByRole('dialog', { name: 'Create a pool' });

  await dialog.getByRole('checkbox', { name: /^sdb / }).check();
  await dialog.getByRole('checkbox', { name: /^sdc / }).check();
  await dialog.getByRole('checkbox', { name: /^sdd / }).check();
  await expect(dialog.getByRole('checkbox', { name: /^sda / })).toHaveCount(0);
  await dialog.getByLabel('Type the serial number of sdb (VYXTEST01)').fill('VYXTEST01');
  await dialog.getByRole('button', { name: 'Next' }).click();

  await expect(dialog).toContainText('For 3 disks we recommend RAID-Z1');
  const layouts = dialog.getByRole('radiogroup', { name: 'Layout' });
  await expect(layouts.getByRole('radio')).toHaveCount(3);
  await expect(layouts.getByRole('radio', { name: /^RAID-Z1/ })).toHaveAttribute(
    'aria-checked',
    'true',
  );
  await expect(layouts.getByRole('radio', { name: /^Stripe/ })).toContainText('No disk may fail');
  await dialog.getByRole('button', { name: 'Next' }).click();

  await expect(dialog.getByRole('radio', { name: 'Encrypt with a key' })).toBeDisabled();
  await expect(dialog.getByRole('button', { name: 'Next' })).toBeDisabled();
  await dialog.getByRole('radio', { name: 'No encryption' }).check();
  await dialog.getByRole('button', { name: 'Next' }).click();

  await dialog.getByLabel('Pool name').fill('tank');
  await dialog.getByRole('button', { name: 'Show commands' }).click();
  const commands = dialog.getByRole('status').getByRole('listitem');
  await expect(commands).toHaveCount(8);
  await expect(commands.nth(0)).toHaveText(
    /^zpool labelclear -f \/dev\/disk\/by-id\/scsi-0QEMU_QEMU_HARDDISK_drive-scsi1-part1$/,
  );
  await expect(commands.nth(2)).toHaveText(
    /zpool create .* tank raidz1 \/dev\/disk\/by-id\/scsi-0QEMU_QEMU_HARDDISK_drive-scsi1 .*scsi2 .*scsi3$/,
  );
  await expect(commands.nth(3)).toHaveText(
    'zfs create -o canmount=off -o mountpoint=none tank/pithyx',
  );
  await expect(dialog.getByRole('button', { name: 'Create pool' })).toBeEnabled();
});

test('replacing a device always asks for the serial number of the new disk', async ({ page }) => {
  const settings = await openStorage(page);
  const mirror = settings.getByRole('article', { name: 'pithyxtestm' });
  await mirror
    .getByRole('listitem')
    .filter({ hasText: 'drive-scsi5' })
    .last()
    .getByRole('button', { name: 'Replace…' })
    .click();
  const dialog = page.getByRole('dialog', { name: /^Replace / });

  await dialog.getByRole('radio', { name: /^sdc / }).check();
  await expect(dialog.getByLabel('Type the serial number of sdc (VYXTEST02)')).toBeVisible();
  await dialog.getByRole('button', { name: 'Show commands' }).click();
  await expect(dialog.getByRole('status')).toContainText(
    'A replacement disk is always confirmed with its serial number.',
  );

  await dialog.getByLabel('Type the serial number of sdc (VYXTEST02)').fill('VYXTEST02');
  await dialog.getByRole('button', { name: 'Show commands' }).click();
  await expect(dialog.getByRole('status').getByRole('listitem')).toHaveText([
    /^zpool replace pithyxtestm \d+ \/dev\/disk\/by-id\/scsi-0QEMU_QEMU_HARDDISK_drive-scsi2$/,
  ]);
  await expect(dialog.getByRole('button', { name: 'Replace', exact: true })).toBeEnabled();
});

test('destroying a pool needs its name typed and lists its datasets', async ({ page }) => {
  const settings = await openStorage(page);
  await settings
    .getByRole('article', { name: 'pithyxtests' })
    .getByRole('button', { name: 'Destroy pool…' })
    .click();
  const dialog = page.getByRole('dialog', { name: 'Destroy pithyxtests' });

  await expect(dialog.getByRole('button', { name: 'Show commands' })).toBeDisabled();
  await dialog.getByLabel('Type pithyxtests to confirm').fill('pithyxtests');
  await dialog.getByRole('button', { name: 'Show commands' }).click();
  await expect(dialog.getByRole('status')).toContainText('including these datasets');
  await expect(dialog.getByRole('status').getByRole('listitem').last()).toHaveText(
    'zpool destroy pithyxtests',
  );
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
  const dialog = page.getByRole('dialog', { name: 'Create a pool' });
  await dialog.getByRole('checkbox', { name: /^sdc / }).check();
  await dialog.getByRole('checkbox', { name: /^sdd / }).check();
  await dialog.getByRole('button', { name: 'Next' }).click();
  await expect(dialog.getByRole('radiogroup', { name: 'Layout' })).toBeVisible();
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
