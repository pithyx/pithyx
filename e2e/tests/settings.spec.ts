import AxeBuilder from '@axe-core/playwright';
import type { Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { appWindow, openAccountMenu, openFromStart, signInAsAdmin } from './session';

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
  await expect(settings.getByRole('heading', { name: 'Settings', level: 2 })).toBeVisible();
  return settings;
}

test('Settings opens as an overview of categories with a search', async ({ page }) => {
  const settings = await openSettings(page);
  for (const category of ['System', 'Files and sharing', 'Users']) {
    await expect(settings.getByRole('heading', { name: category, level: 3 })).toBeVisible();
  }
  for (const tile of [
    'Info Center',
    'Network',
    'Regional settings',
    'Shared folders',
    'File services',
    'Users',
  ]) {
    await expect(settings.getByRole('button', { name: new RegExp(`^${tile}`) })).toBeVisible();
  }
  // Personal settings live in the account menu.
  await expect(settings.getByRole('button', { name: /Personalization|Appearance/ })).toHaveCount(0);
  expect(await violations(page)).toEqual([]);

  const search = settings.getByRole('searchbox', { name: 'Search settings' });
  await search.fill('ntp');
  await expect(settings.getByRole('button', { name: /^Regional settings/ })).toBeVisible();
  await expect(settings.getByRole('button', { name: /^Network/ })).toHaveCount(0);
  await search.press('Enter');
  await expect(
    settings.getByRole('heading', { name: 'Regional settings', level: 2 }),
  ).toBeVisible();
  await settings.getByRole('button', { name: 'All settings' }).click();
  // The window title is a level 2 heading named Settings too, so the overview shows by its categories.
  await expect(settings.getByRole('heading', { name: 'System', level: 3 })).toBeVisible();

  await settings.getByRole('searchbox', { name: 'Search settings' }).fill('no such setting');
  await expect(settings).toContainText('No settings match your search.');
});

test('the Info Center, Network, File services and Users pages open from their tiles', async ({
  page,
}) => {
  const settings = await openSettings(page);
  await settings.getByRole('button', { name: /^Info Center/ }).click();
  await expect(settings.getByRole('heading', { name: 'Info Center', level: 2 })).toBeVisible();
  await expect(settings.getByText('Pithyx version')).toBeVisible();
  await expect(settings.getByText('Logical processors')).toBeVisible();
  expect(await violations(page)).toEqual([]);
  await settings.getByRole('button', { name: 'All settings' }).click();

  await settings.getByRole('button', { name: /^Network/ }).click();
  await expect(settings.getByText('Changes follow')).toBeVisible();
  await expect(settings.getByRole('heading', { name: 'Network', level: 2 })).toBeVisible();
  expect(await violations(page)).toEqual([]);
  await settings.getByRole('button', { name: 'All settings' }).click();

  await settings.getByRole('button', { name: /^File services/ }).click();
  await expect(settings.getByRole('heading', { name: 'File services', level: 2 })).toBeVisible();
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
  await settings.getByRole('button', { name: 'All settings' }).click();

  await settings.getByRole('button', { name: /^Users/ }).click();
  await expect(settings.getByRole('heading', { name: 'Users', level: 2 })).toBeVisible();
  await expect(settings.getByRole('button', { name: 'Create user' })).toBeVisible();
  expect(await violations(page)).toEqual([]);
});

test('the time zone and time servers are checked and saved', async ({ page }) => {
  const settings = await openSettings(page);
  await settings.getByRole('button', { name: /^Regional settings/ }).click();
  await settings.getByLabel('Time zone').selectOption('Europe/Vienna');
  await settings.getByLabel('Time servers').fill('0.pool.ntp.org 1.pool.ntp.org');
  await settings.getByRole('button', { name: 'Save time settings' }).click();
  await expect(settings.getByRole('status').filter({ hasText: 'Saved.' })).toBeVisible();

  await settings.getByLabel('Time servers').fill('not-a-host!');
  await settings.getByRole('button', { name: 'Save time settings' }).click();
  await expect(settings).toContainText('is neither a host name nor an address');
  expect(await violations(page)).toEqual([]);
});

test('the system region sets the formats everyone sees', async ({ page }) => {
  const settings = await openSettings(page);
  try {
    await settings.getByRole('button', { name: /^Regional settings/ }).click();
    const formats = settings.getByLabel('Formats', { exact: true });
    await formats.selectOption('DE');
    await expect(settings).toContainText('1.234.567,89');
    await settings.getByRole('button', { name: 'Save language and formats' }).click();
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

test('theme, accent color and wallpaper are in the account menu', async ({ page }) => {
  await signInAsAdmin(page);
  try {
    await openAccountMenu(page);
    await page.getByRole('menuitem', { name: 'Accent color' }).click();
    await page.getByRole('menuitemradio', { name: 'Moss' }).click();
    await expect(page.locator('html')).toHaveAttribute('data-accent', 'moss');

    await openAccountMenu(page);
    await page.getByRole('menuitem', { name: 'Wallpaper' }).click();
    await page.getByRole('menuitemradio', { name: 'Plain' }).click();
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
