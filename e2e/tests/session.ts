import { expect, type Page } from '@playwright/test';

/** The administrator every spec signs in as. auth.spec.ts ends with this password after its reset. */
export const admin = {
  username: 'admin',
  displayName: 'Ada Admin',
  password: 'staple battery horse correct',
};

/** Signs in, or sets up the administrator first when a spec runs on a fresh database on its own. */
export async function signInAsAdmin(page: Page) {
  await page.goto('/');
  const setup = page.getByRole('heading', { name: 'Set up Pithyx' });
  const login = page.getByRole('heading', { name: 'Welcome back' });
  await expect(setup.or(login)).toBeVisible();
  if (await setup.isVisible()) {
    await page.getByLabel('User name').fill(admin.username);
    await page.getByLabel('Display name (optional)').fill(admin.displayName);
    await page.getByLabel('Password', { exact: true }).fill(admin.password);
    await page.getByLabel('Repeat password').fill(admin.password);
    await page.getByRole('button', { name: 'Create administrator' }).click();
    await page.getByText('I have stored the recovery key').click();
    await page.getByRole('button', { name: 'Continue to the desktop' }).click();
  } else {
    await page.getByLabel('User name').fill(admin.username);
    await page.getByLabel('Password', { exact: true }).fill(admin.password);
    await page.getByRole('button', { name: 'Sign in' }).click();
  }
  await expect(page.getByRole('navigation', { name: 'Taskbar' })).toBeVisible();
  await desktopReady(page);
}

/**
 * Waits until the desktop takes input: the welcome card has left (it is the only <main> without a label) and the
 * taskbar has slid in. The taskbar ignores presses while it moves, so a click during the entrance would be lost.
 */
export async function desktopReady(page: Page) {
  await expect(page.locator('main:not([aria-label])')).toHaveCount(0);
  await expect(page.locator('[data-entering]')).toHaveCount(0);
}

/** Opens a core app from its desktop shortcut. */
export async function openFromDesktop(page: Page, name: string) {
  await page.getByRole('option', { name: `${name}, shortcut` }).dblclick();
}

/** Opens the account menu from the profile at the bottom of the start menu. */
export async function openAccountMenu(page: Page, name = admin.displayName) {
  const start = page.getByRole('dialog', { name: 'Pithyx menu' });
  if (!(await start.isVisible())) {
    await page.getByRole('button', { name: 'Pithyx menu' }).click();
  }
  await start.getByRole('button', { name: `Account menu for ${name}` }).click();
  await expect(page.getByRole('menu', { name: `Account menu for ${name}` })).toBeVisible();
}

/** Opens Personal settings from the account menu (D392) and returns its window. */
export async function openPersonalSettings(page: Page, name = admin.displayName) {
  await openAccountMenu(page, name);
  await page.getByRole('menuitem', { name: 'Personal settings' }).click();
  const window = appWindow(page, 'Personal settings');
  await expect(window).toBeVisible();
  return window;
}

/** Picks one choice of a setting in Personal settings, such as the taskbar position. */
export async function choosePersonalSetting(page: Page, setting: string, choice: string) {
  const window = await openPersonalSettings(page);
  await window.getByRole('button', { name: setting, exact: true }).click();
  await page.getByRole('menuitemradio', { name: choice }).click();
}

/** Opens an app from the start menu; apps without a desktop shortcut open this way. */
export async function openFromStart(page: Page, name: string) {
  await page.getByRole('button', { name: 'Pithyx menu' }).click();
  await page
    .getByRole('dialog', { name: 'Pithyx menu' })
    .getByRole('row', { name, exact: true })
    .click();
}

/**
 * Opens Files in the simulated shared folder "Shared": Files starts in the user's home (D348), which the specs that
 * prepare files on disk do not use.
 */
export async function openFilesInShared(page: Page) {
  await openFromStart(page, 'Files');
  const files = page.getByRole('region', { name: /Files$/ });
  await files
    .getByRole('navigation', { name: 'Shares and recycle bin' })
    .getByRole('button', { name: 'Shared', exact: true })
    .click();
  await expect(files.getByRole('navigation', { name: 'Path' })).toContainText('Shared');
}

export function appWindow(page: Page, name: string) {
  return page.getByRole('region', { name, exact: true });
}

/**
 * Puts the administrator's desktop items back to Storage and About; they are shared by every spec. The item specs measure
 * this grid, so the Files shortcut new users also get is left out; specs open Files from the start menu.
 */
export async function resetDesktopItems(page: Page) {
  const headers = { 'X-Pithyx-Request': '1' };
  const items = (await (await page.request.get('/api/desktop/items')).json()) as { id: number }[];
  for (const item of items) {
    await page.request.delete(`/api/desktop/items/${String(item.id)}`, { headers });
  }
  for (const [row, app] of ['org.pithyx.storage', 'org.pithyx.about'].entries()) {
    await page.request.post('/api/desktop/items', {
      headers,
      data: { kind: 'shortcut', app, column: 0, row },
    });
  }
}
