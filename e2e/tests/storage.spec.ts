import AxeBuilder from '@axe-core/playwright';
import type { Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { appWindow, openFromStart, signInAsAdmin } from './session';

// The simulated system: the dev VM's disks with a mirror (sde, sdf) and a single-disk pool (sdg), an old ZFS label on
// sdb, and the system on sda (daemon/tests/fixtures/commands/default).

async function openStorage(page: Page) {
  await signInAsAdmin(page);
  await openFromStart(page, 'Storage');
  const storage = appWindow(page, 'Storage');
  await expect(storage.getByRole('heading', { name: 'Pools', level: 2 })).toBeVisible();
  return storage;
}

async function openDisks(storage: ReturnType<typeof appWindow>) {
  await storage.getByRole('button', { name: 'Disks', exact: true }).click();
  await expect(storage.getByRole('heading', { name: 'Disks', level: 2 })).toBeVisible();
}

function diskRow(storage: ReturnType<typeof appWindow>, name: string) {
  return storage
    .getByRole('table', { name: 'Disks and whether a new pool may use them' })
    .getByRole('row', { name: new RegExp(`^${name} `) });
}

test('Storage shows the pools and whether each disk may be used', async ({ page }) => {
  const storage = await openStorage(page);

  const mirror = storage.getByRole('article', { name: 'pithyxtestm' });
  await expect(mirror).toContainText('Healthy');
  await expect(mirror).toContainText('Mirror of 2 disks');
  await expect(storage.getByRole('article', { name: 'pithyxtests' })).toContainText('Single disk');
  await expect(storage).toContainText('only the declared test disks can be used');

  await openDisks(storage);

  await expect(diskRow(storage, 'sda')).toContainText('Holds the system. Never used for pools.');
  await expect(diskRow(storage, 'sdb')).toContainText('Needs confirmation');
  await expect(diskRow(storage, 'sdc')).toContainText('Empty and ready.');
  await expect(diskRow(storage, 'sde')).toContainText('Part of the pool pithyxtestm.');
  await expect(diskRow(storage, 'sdg')).toContainText('No SMART data in a virtual machine');
});

test('Settings has no storage page and no link to Storage', async ({ page }) => {
  await signInAsAdmin(page);
  await openFromStart(page, 'Settings');
  const settings = appWindow(page, 'Settings');
  await expect(settings.getByRole('heading', { name: 'Settings', level: 2 })).toBeVisible();
  await expect(settings.getByRole('button', { name: /Storage/ })).toHaveCount(0);
});

test('the pool wizard compares layouts, asks about encryption and lists the exact commands', async ({
  page,
}) => {
  const storage = await openStorage(page);
  await storage.getByRole('button', { name: 'Create pool…' }).click();
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
  await expect(commands).toHaveCount(9);
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
  const storage = await openStorage(page);
  const mirror = storage.getByRole('article', { name: 'pithyxtestm' });
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
  const storage = await openStorage(page);
  await storage
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

  // The destroy runs in the background: the dialog closes at once and the notifications say when it is done.
  await dialog.getByRole('button', { name: 'Destroy pool', exact: true }).click();
  await expect(dialog).toBeHidden();
  await expect(page.getByRole('region', { name: 'New notifications' })).toContainText(
    'Deleting a pool: done',
  );
});

async function openAdvanced(storage: ReturnType<typeof appWindow>) {
  await storage.getByRole('button', { name: 'Advanced', exact: true }).click();
  await expect(storage.getByRole('heading', { name: 'Advanced', level: 2 })).toBeVisible();
}

async function openDatasets(storage: ReturnType<typeof appWindow>) {
  await openAdvanced(storage);
  await storage.getByRole('button', { name: 'Show datasets' }).click();
  await expect(
    storage.getByRole('heading', { name: 'Datasets and volumes', level: 2 }),
  ).toBeVisible();
}

test('the Datasets section lists every dataset and explains pools without the Pithyx layout', async ({
  page,
}) => {
  const storage = await openStorage(page);
  await openDatasets(storage);

  const mirror = storage.getByRole('table', { name: 'Datasets and volumes of pithyxtestm' });
  await expect(mirror.getByRole('rowheader', { name: 'pithyxtestm', exact: true })).toBeVisible();
  await expect(
    storage
      .getByRole('table', { name: 'Datasets and volumes of pithyxtests' })
      .getByRole('rowheader', { name: 'pithyxtests', exact: true }),
  ).toBeVisible();
  // The simulated pools were not created by Pithyx: no volumes, and their roots are never renamed or deleted.
  await expect(storage.getByRole('button', { name: 'Create volume…' })).toHaveCount(0);
  await expect(storage.getByText('Pithyx did not set up this pool').first()).toBeVisible();
  await expect(storage.getByRole('button', { name: 'Delete pithyxtestm' })).toHaveCount(0);
  await expect(storage.getByRole('button', { name: 'Rename pithyxtestm' })).toHaveCount(0);
});

test('the limits dialog shows the exact zfs set command before it runs', async ({ page }) => {
  const storage = await openStorage(page);
  await openDatasets(storage);
  await storage.getByRole('button', { name: 'Limits for pithyxtestm' }).click();
  const dialog = page.getByRole('dialog', { name: 'Limits and compression of pithyxtestm' });

  await expect(dialog).toContainText('takes only a compression setting');
  await expect(dialog.getByLabel('Quota')).toHaveCount(0);
  await expect(dialog.getByRole('button', { name: 'Show commands' })).toBeDisabled();
  await dialog.getByLabel('Compression').selectOption('zstd-3');
  expect(await violations(page)).toEqual([]);
  await dialog.getByRole('button', { name: 'Show commands' }).click();

  await expect(dialog.getByRole('status').getByRole('listitem')).toHaveText([
    'zfs set compression=zstd-3 pithyxtestm',
  ]);
  await expect(dialog.getByRole('button', { name: 'Apply' })).toBeEnabled();
});

test('deleting a dataset needs its full name typed', async ({ page }) => {
  // The simulated pools hold only their roots, which are never deleted; a child is added to what the page reads.
  await page.route('**/api/storage/pools', async (route) => {
    const response = await route.fetch();
    const list = (await response.json()) as { datasets: Record<string, unknown>[] };
    const root = list.datasets.find((dataset) => dataset.name === 'pithyxtestm');
    list.datasets.push({ ...root, name: 'pithyxtestm/data', mountPoint: '/pithyxtestm/data' });
    await route.fulfill({ response, json: list });
  });
  const storage = await openStorage(page);
  await openDatasets(storage);
  await storage.getByRole('button', { name: 'Delete pithyxtestm/data' }).click();
  const dialog = page.getByRole('dialog', { name: 'Delete pithyxtestm/data' });
  const show = dialog.getByRole('button', { name: 'Show commands' });

  await expect(show).toBeDisabled();
  await dialog.getByLabel('Type pithyxtestm/data to confirm').fill('data');
  await expect(show).toBeDisabled();
  await dialog.getByLabel('Type pithyxtestm/data to confirm').fill('pithyxtestm/data');
  await expect(show).toBeEnabled();

  // The daemon has no such dataset and says so.
  await show.click();
  await expect(dialog).toContainText('There is no dataset named pithyxtestm/data.');
  await expect(dialog.getByRole('button', { name: 'Delete', exact: true })).toHaveCount(0);
});

test('Advanced explains volumes, export and import, and Storage has no snapshot section', async ({
  page,
}) => {
  const storage = await openStorage(page);
  await expect(storage.getByRole('button', { name: 'Snapshots', exact: true })).toHaveCount(0);
  await expect(
    storage.getByRole('button', { name: 'Datasets and volumes', exact: true }),
  ).toHaveCount(0);
  // Export and import left the pool cards and the Pools header.
  await expect(storage.getByRole('button', { name: 'Export…' })).toHaveCount(0);
  await expect(storage.getByRole('button', { name: 'Import a pool…' })).toHaveCount(0);

  await openAdvanced(storage);
  await expect(storage).toContainText('Export a pool to move its disks to another computer.');
  await expect(storage).toContainText('Volumes need a pool that Pithyx created.');
  await storage.getByRole('button', { name: 'Export pithyxtests…' }).click();
  const dialog = page.getByRole('dialog', { name: 'Export pithyxtests' });
  await expect(dialog).toBeVisible();
  expect(await violations(page)).toEqual([]);
});

test('importing a pool scans only when asked and imports it read-only first', async ({ page }) => {
  const storage = await openStorage(page);
  await openAdvanced(storage);
  await storage.getByRole('button', { name: 'Import a pool…' }).click();
  const dialog = page.getByRole('dialog', { name: 'Import a pool' });

  await expect(dialog).toContainText('may wake a sleeping disk');
  await expect(dialog.getByRole('radio')).toHaveCount(0);
  await dialog.getByRole('button', { name: 'Scan disks' }).click();

  const oldTank = dialog.getByRole('radio', { name: /^oldtank/ });
  await expect(oldTank).toBeEnabled();
  await expect(dialog.getByRole('radio', { name: /^archive/ })).toBeDisabled();
  await expect(dialog.getByRole('group', { name: 'oldtank' })).toContainText('Can be imported.');
  await expect(dialog.getByRole('group', { name: 'archive' })).toContainText(
    'This pool cannot be imported',
  );
  expect(await violations(page)).toEqual([]);

  await oldTank.check();
  await dialog.getByRole('button', { name: 'Show commands' }).click();
  await expect(dialog.getByRole('status').getByRole('listitem')).toHaveText([
    'zpool import -o readonly=on -o cachefile=none -N -d /dev/disk/by-id 5249471309174385122',
  ]);
  await expect(dialog.getByRole('button', { name: 'Import read-only' })).toBeEnabled();
});

test('adding a single disk to a mirror warns and needs the pool name typed', async ({ page }) => {
  const storage = await openStorage(page);
  await storage
    .getByRole('article', { name: 'pithyxtestm' })
    .getByRole('button', { name: 'Add disks…' })
    .click();
  const dialog = page.getByRole('dialog', { name: 'Add disks to pithyxtestm' });

  await dialog.getByRole('checkbox', { name: /^sdc / }).check();
  await expect(dialog.getByRole('radio', { name: 'Single disk, no protection' })).toBeChecked();
  await dialog.getByRole('button', { name: 'Show commands' }).click();
  await expect(dialog.getByRole('note', { name: 'Warnings' })).toContainText(
    'The new disks protect less than the rest of the pool',
  );
  await expect(dialog.getByRole('status')).toHaveText(
    'This changes the pool for good. Type its name to see the commands.',
  );
  await expect(dialog.getByRole('button', { name: 'Add disks' })).toHaveCount(0);

  await dialog.getByLabel('Type pithyxtestm to confirm').fill('pithyxtestm');
  await dialog.getByRole('button', { name: 'Show commands' }).click();
  await expect(dialog.getByRole('status').getByRole('listitem')).toHaveText([
    'zpool add --allow-replication-mismatch pithyxtestm /dev/disk/by-id/scsi-0QEMU_QEMU_HARDDISK_drive-scsi2',
  ]);
  await expect(dialog.getByRole('button', { name: 'Add disks' })).toBeEnabled();
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

test('Storage and the preview have no accessibility violations', async ({ page }) => {
  const storage = await openStorage(page);
  for (const section of ['Disks', 'Health and maintenance', 'Advanced']) {
    await storage.getByRole('button', { name: section, exact: true }).click();
    await expect(storage.getByRole('heading', { name: section, level: 2 })).toBeVisible();
    expect(await violations(page)).toEqual([]);
  }
  await storage.getByRole('button', { name: 'Pools', exact: true }).click();
  await storage.getByRole('button', { name: 'Create pool…' }).click();
  const dialog = page.getByRole('dialog', { name: 'Create a pool' });
  await dialog.getByRole('checkbox', { name: /^sdc / }).check();
  await dialog.getByRole('checkbox', { name: /^sdd / }).check();
  await dialog.getByRole('button', { name: 'Next' }).click();
  await expect(dialog.getByRole('radiogroup', { name: 'Layout' })).toBeVisible();
  expect(await violations(page)).toEqual([]);
});

test.describe('phone', () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });

  test('Storage fits and has no accessibility violations', async ({ page }) => {
    const storage = await openStorage(page);
    await openDisks(storage);
    await expect(diskRow(storage, 'sda')).toBeVisible();
    expect(await violations(page)).toEqual([]);
    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - window.innerWidth,
    );
    expect(overflow).toBeLessThanOrEqual(0);
  });
});
