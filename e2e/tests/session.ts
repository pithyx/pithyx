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
  const setup = page.getByRole('heading', { name: 'Set up Vyxis' });
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
}

/** Opens a core app from its desktop shortcut. */
export async function openFromDesktop(page: Page, name: string) {
  await page.getByRole('button', { name: `${name}, shortcut` }).dblclick();
}

export function appWindow(page: Page, name: string) {
  return page.getByRole('region', { name, exact: true });
}
