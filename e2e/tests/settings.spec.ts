import AxeBuilder from '@axe-core/playwright';
import type { Page } from '@playwright/test';
import { expect, test } from './fixtures';
import {
  appWindow,
  choosePersonalSetting,
  openAccountMenu,
  openFromStart,
  openSettingsPage,
  settingsNavigation,
  signInAsAdmin,
} from './session';

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];
const headers = { 'X-Pithyx-Request': '1' };

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

async function openSettings(page: Page) {
  await signInAsAdmin(page);
  await openFromStart(page, 'Settings');
  const settings = appWindow(page, 'Settings');
  await expect(settings.getByRole('heading', { name: 'Overview', level: 1 })).toBeVisible();
  return settings;
}

test('Settings opens with the overview and a grouped navigation with a search', async ({
  page,
}) => {
  const settings = await openSettings(page);
  const navigation = settingsNavigation(settings);
  for (const group of ['System', 'Access']) {
    await expect(navigation.getByRole('heading', { name: group })).toBeVisible();
  }
  for (const item of [
    'Info Center',
    'Network',
    'Regional settings',
    'Backup and restore',
    'Shared folders',
    'Home folders',
    'File services',
    'Users and groups',
  ]) {
    await expect(navigation.getByRole('button', { name: item, exact: true })).toBeVisible();
  }
  await expect(settings.getByRole('heading', { name: 'All settings', level: 2 })).toBeVisible();
  // Personal settings are an app of their own.
  await expect(settings.getByRole('button', { name: /Personalization|Appearance/ })).toHaveCount(0);
  expect(await violations(page)).toEqual([]);

  const search = navigation.getByRole('searchbox', { name: 'Search settings' });
  await search.fill('ntp');
  await expect(
    navigation.getByRole('button', { name: 'Regional settings', exact: true }),
  ).toBeVisible();
  await expect(navigation.getByRole('button', { name: 'Network', exact: true })).toHaveCount(0);
  await navigation.getByRole('button', { name: 'Regional settings', exact: true }).click();
  await expect(
    settings.getByRole('heading', { name: 'Regional settings', level: 1 }),
  ).toBeVisible();
  await search.fill('');
  await openSettingsPage(settings, 'Overview');
  await expect(settings.getByRole('heading', { name: 'All settings', level: 2 })).toBeVisible();
});

test('the Info Center, Network, File services and Users pages open from the navigation', async ({
  page,
}) => {
  const settings = await openSettings(page);
  await openSettingsPage(settings, 'Info Center');
  await expect(settings.getByRole('heading', { name: 'Info Center', level: 1 })).toBeVisible();
  await expect(settings.getByText('Pithyx version')).toBeVisible();
  await expect(settings.getByText('Logical processors')).toBeVisible();
  expect(await violations(page)).toEqual([]);

  await openSettingsPage(settings, 'Network');
  await expect(settings.getByRole('tab', { name: 'Interfaces' })).toHaveAttribute(
    'aria-selected',
    'true',
  );
  await settings.getByRole('tab', { name: 'Host name and name servers' }).click();
  await expect(settings.getByRole('heading', { name: 'Host name and name servers' })).toBeVisible();
  await settings.getByRole('tab', { name: 'Reverse proxy' }).click();
  await expect(settings.getByRole('tabpanel', { name: 'Reverse proxy' })).toBeVisible();
  await expect(settings.getByRole('heading', { name: 'Network', level: 1 })).toBeVisible();
  expect(await violations(page)).toEqual([]);

  await openSettingsPage(settings, 'File services');
  await expect(settings.getByRole('heading', { name: 'File services', level: 1 })).toBeVisible();
  await expect(
    settings.getByRole('switch', { name: 'Offer shared folders over SMB' }),
  ).toBeChecked();
  const sftp = settings.getByRole('switch', { name: 'Offer shared folders over SFTP' });
  await expect(sftp).not.toBeChecked();
  await settings.getByText('Offer shared folders over SFTP').click();
  await expect(sftp).toBeChecked();
  await settings.getByLabel('Workgroup').fill('home-lab');
  await settings.getByRole('button', { name: 'Save' }).click();
  await expect(settings.getByLabel('Workgroup')).toHaveValue('HOME-LAB');
  expect(await violations(page)).toEqual([]);

  await openSettingsPage(settings, 'Users and groups');
  await expect(settings.getByRole('heading', { name: 'Users and groups', level: 1 })).toBeVisible();
  await expect(settings.getByRole('button', { name: 'Create user' })).toBeVisible();
  // The groups are a tab of their own (D394).
  await expect(settings.getByRole('grid', { name: 'Groups' })).toHaveCount(0);
  await settings
    .getByRole('grid', { name: 'Users' })
    .getByRole('row', { name: /^Ada Admin/ })
    .click();
  await expect(settings.getByRole('complementary', { name: 'Details' })).toContainText(
    'Special rights',
  );
  expect(await violations(page)).toEqual([]);
});

test('the time zone and time servers are checked and saved', async ({ page }) => {
  const settings = await openSettings(page);
  await openSettingsPage(settings, 'Regional settings');
  await settings.getByRole('button', { name: /Time zone/ }).click();
  await page.getByRole('option', { name: 'Europe/Vienna', exact: true }).click();
  await settings.getByLabel('Time servers').fill('0.pool.ntp.org 1.pool.ntp.org');
  await settings.getByRole('button', { name: 'Apply', exact: true }).click();
  await expect(settings.getByRole('status').filter({ hasText: 'Saved.' })).toBeVisible();

  await settings.getByLabel('Time servers').fill('not-a-host!');
  await settings.getByRole('button', { name: 'Apply', exact: true }).click();
  await expect(settings).toContainText('is neither a host name nor an address');
  expect(await violations(page)).toEqual([]);
});

test('the system region sets the formats everyone sees', async ({ page }) => {
  const settings = await openSettings(page);
  try {
    await openSettingsPage(settings, 'Regional settings');
    await settings.getByRole('button', { name: /Formats/ }).click();
    await page.getByRole('option', { name: 'Germany', exact: true }).click();
    await expect(settings).toContainText('1.234.567,89');
    await settings.getByRole('button', { name: 'Apply', exact: true }).click();
    await expect(settings.getByRole('status').filter({ hasText: 'Saved.' })).toBeVisible();

    const setup = (await (await page.request.get('/api/setup')).json()) as { region: string };
    expect(setup.region).toBe('DE');
  } finally {
    await page.request.put('/api/system/region', {
      headers,
      data: { language: 'en', region: null },
    });
  }
});

test('accent color and wallpaper are in Personal settings, the theme also in the account menu', async ({
  page,
}) => {
  await signInAsAdmin(page);
  try {
    await choosePersonalSetting(page, 'Accent color', 'Moss');
    await expect(page.locator('html')).toHaveAttribute('data-accent', 'moss');

    await choosePersonalSetting(page, 'Wallpaper', 'Plain');
    await expect(page.locator('svg[data-plain]')).toHaveCount(1);

    await openAccountMenu(page);
    await page.getByRole('menuitem', { name: 'Appearance' }).click();
    await page.getByRole('menuitemradio', { name: 'Dark' }).click();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');

    // Accent and wallpaper follow the user to every device; the theme stays on this one.
    const desktop = (await (await page.request.get('/api/desktop', { headers })).json()) as {
      appearance: { accent: string; wallpaper: string };
    };
    expect(desktop.appearance).toEqual({ accent: 'moss', wallpaper: 'plain' });
  } finally {
    await page.request.put('/api/desktop/appearance', {
      headers,
      data: { accent: 'petrol', wallpaper: 'boxes' },
    });
    await page.evaluate(() => {
      localStorage.removeItem('pithyx.theme');
      localStorage.removeItem('pithyx.appearance');
    });
  }
});

test('groups and their rights reach their members, and the editors are accessible', async ({
  page,
}) => {
  const settings = await openSettings(page);
  // Created before the page opens, so its lists have the user.
  const user = await page.request.post('/api/users', {
    headers,
    data: { username: 'e2e-rights', password: 'river-candle-orbit-7' },
  });
  expect(user.ok()).toBe(true);
  const userId = ((await user.json()) as { id: number }).id;
  await openSettingsPage(settings, 'Users and groups');
  await settings.getByRole('tab', { name: /^Groups/ }).click();

  // A group with the new user, created on the page.
  await settings.getByRole('button', { name: 'Create group' }).click();
  const create = page.getByRole('dialog', { name: 'Create group' });
  await create.getByLabel('Group name').fill('e2e-media');
  // Checkbox inputs are visually hidden behind the drawn box, so a user clicks the label.
  await create.getByText('e2e-rights', { exact: true }).click();
  await expect(create.getByRole('checkbox', { name: 'e2e-rights' })).toBeChecked();
  await create.getByRole('button', { name: 'Create' }).click();
  await expect(create).toHaveCount(0);
  const groups = settings.getByRole('grid', { name: 'Groups' });
  await expect(groups.getByRole('row', { name: /^e2e-media/ })).toContainText('e2e-rights');

  // FTP for the group, then the user's own rights show it as given by the group.
  await groups.getByRole('row', { name: /^e2e-media/ }).click();
  await settings.getByRole('button', { name: 'Rights', exact: true }).click();
  const groupRights = page.getByRole('dialog', { name: 'Rights of the group e2e-media' });
  await groupRights.getByText('FTP', { exact: true }).click();
  await expect(groupRights.getByRole('checkbox', { name: 'FTP', exact: true })).toBeChecked();
  expect(await violations(page)).toEqual([]);
  await groupRights.getByRole('button', { name: 'Save rights' }).click();
  await expect(groupRights).toHaveCount(0);

  await settings.getByRole('tab', { name: /^Users/ }).click();
  const users = settings.getByRole('grid', { name: 'Users' });
  await users.getByRole('row', { name: /^e2e-rights/ }).click();
  await settings.getByRole('button', { name: 'Rights', exact: true }).click();
  const userRights = page.getByRole('dialog', { name: 'Rights of e2e-rights' });
  await expect(userRights.getByRole('checkbox', { name: 'FTP', exact: true })).toBeChecked();
  await expect(userRights.getByRole('checkbox', { name: 'FTP', exact: true })).toBeDisabled();
  await expect(userRights).toContainText('Through the group e2e-media');
  await expect(userRights.getByRole('checkbox', { name: 'SMB (network drives)' })).toBeChecked();
  await userRights.getByText('Restart and shut down', { exact: true }).click();
  await expect(userRights.getByRole('checkbox', { name: 'Restart and shut down' })).toBeChecked();
  await userRights.getByRole('button', { name: 'Save rights' }).click();
  await expect(userRights).toHaveCount(0);
  await expect(users.getByRole('row', { name: /^e2e-rights/ })).toContainText('FTP');

  const grants = await page.request.get(`/api/users/${String(userId)}/grants`, { headers });
  const effective = (
    (await grants.json()) as { effective: { rights: string[]; protocols: string[] } }
  ).effective;
  expect(effective.rights).toContain('right:power');
  expect(effective.protocols).toEqual(['protocol:smb', 'protocol:ftp']);
});

test('a configuration backup downloads encrypted and is checked before a restore', async ({
  page,
}, testInfo) => {
  const settings = await openSettings(page);
  await openSettingsPage(settings, 'Backup and restore');
  await expect(
    settings.getByRole('heading', { name: 'Backup and restore', level: 1 }),
  ).toBeVisible();
  expect(await violations(page)).toEqual([]);

  const password = 'orange tractor sunset';
  await settings.getByLabel('Backup password', { exact: true }).first().fill(password);
  await settings.getByLabel('Repeat the backup password').fill('something else');
  await settings.getByRole('button', { name: 'Create and download' }).click();
  await expect(settings.getByText('The passwords do not match.')).toBeVisible();
  await settings.getByLabel('Repeat the backup password').fill(password);
  const download = page.waitForEvent('download');
  await settings.getByRole('button', { name: 'Create and download' }).click();
  const file = await download;
  expect(file.suggestedFilename()).toMatch(/^pithyx-config-.+\.pithyxbackup$/);
  const path = testInfo.outputPath(file.suggestedFilename());
  await file.saveAs(path);
  await expect(settings.getByRole('status')).toContainText(file.suggestedFilename());

  const restore = settings.getByRole('region', { name: 'Restore a backup' });
  await restore.getByLabel('Backup file').setInputFiles(path);
  await restore.getByLabel('Backup password').fill('not the password');
  await restore.getByRole('button', { name: 'Check backup' }).click();
  await expect(restore.getByText('The backup password is wrong.')).toBeVisible();
  await restore.getByLabel('Backup password').fill(password);
  await restore.getByRole('button', { name: 'Check backup' }).click();
  await expect(restore.getByText('This backup holds')).toBeVisible();
  expect(await violations(page)).toEqual([]);

  // Only the storage settings: the other tests share this daemon and its users.
  for (const part of ['Users and groups', 'Shared folders and file services', 'System settings']) {
    // The checkbox input is visually hidden behind the drawn box, so a user clicks the label.
    await restore.getByText(part, { exact: true }).click();
    await expect(restore.getByRole('checkbox', { name: part })).not.toBeChecked();
  }
  // Focusing a checkbox once scrolled the whole window out of sight.
  await restore.getByRole('checkbox', { name: 'Storage settings' }).focus();
  await expect(settingsNavigation(settings)).toBeInViewport();
  await restore.getByRole('button', { name: 'Restore…' }).click();
  const confirm = page.getByRole('dialog', { name: 'Restore the configuration?' });
  await expect(confirm.getByText('Storage settings')).toBeVisible();
  await expect(confirm.getByText(/signs everybody out/)).toHaveCount(0);
  await confirm.getByRole('button', { name: 'Restore now' }).click();
  await expect(restore.getByText(/The configuration was restored/)).toBeVisible();
});
