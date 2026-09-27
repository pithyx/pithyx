import { type Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { admin, openFromDesktop } from './session';
import { totp } from './totp';

const password = 'correct horse battery';
const newPassword = admin.password;
const recoveryKeyFormat = /^([0-9A-Z]{4}-){6}[0-9A-Z]{2}$/;

async function signIn(page: Page, secret: string) {
  await page.getByLabel('User name').fill('admin');
  await page.getByLabel('Password', { exact: true }).fill(secret);
  await page.getByRole('button', { name: 'Sign in' }).click();
}

async function signOut(page: Page) {
  await page.getByRole('button', { name: 'Account menu for Ada Admin' }).click();
  await page.getByRole('menuitem', { name: 'Sign out' }).click();
  await expect(page.getByRole('heading', { name: 'Welcome back' })).toBeVisible();
}

// The checkbox input is visually hidden behind the drawn box, so a user clicks the label.
async function confirmStored(page: Page) {
  await page.getByText('I have stored the recovery key').click();
  await expect(
    page.getByRole('checkbox', { name: 'I have stored the recovery key' }),
  ).toBeChecked();
}

function disks(page: Page) {
  return page.getByRole('region', { name: 'Disks', exact: true });
}

async function openDisks(page: Page) {
  await openFromDesktop(page, 'Disks');
  await expect(disks(page).getByRole('row', { name: /sdg/ })).toBeVisible();
}

// One flow on one database: the steps depend on each other, and the tests below build on its end state.
test.describe.configure({ mode: 'serial' });

test('setup, recovery key, TOTP sign-in and password reset with the recovery key', async ({
  page,
}) => {
  let recoveryKey = '';
  let secret = '';
  await page.goto('/');

  await test.step('set up the administrator', async () => {
    await expect(page.getByRole('heading', { name: 'Set up Vyxis' })).toBeVisible();
    await expect(page.getByText('Step 1 of 2')).toBeVisible();
    await page.getByLabel('User name').fill('Admin');
    await page.getByLabel('Display name (optional)').fill('Ada Admin');
    await page.getByLabel('Password', { exact: true }).fill(password);
    await page.getByLabel('Repeat password').fill(`${password}!`);
    await page.getByRole('button', { name: 'Create administrator' }).click();
    // A failed submit moves focus to the first invalid field, which describes the error.
    const repeat = page.getByLabel('Repeat password');
    await expect(repeat).toBeFocused();
    await expect(repeat).toHaveAccessibleDescription('The passwords do not match.');
    await page.getByLabel('Repeat password').fill(password);
    await page.getByRole('button', { name: 'Create administrator' }).click();
  });

  await test.step('confirm the recovery key', async () => {
    await expect(page.getByRole('heading', { name: 'Your recovery key' })).toBeVisible();
    const key = page.getByTestId('recovery-key');
    await expect(key).toHaveText(recoveryKeyFormat);
    recoveryKey = (await key.textContent()) ?? '';
    const next = page.getByRole('button', { name: 'Continue to the desktop' });
    await expect(next).toBeDisabled();
    await confirmStored(page);
    await next.click();
  });

  await test.step('the desktop shows the disk list live', async () => {
    await openDisks(page);
    await expect(disks(page).getByRole('row')).toHaveCount(8);
    await expect(disks(page).getByRole('row', { name: /sdg/ })).toContainText('VYXTEST06');
    await page.getByRole('button', { name: 'Account menu for Ada Admin' }).click();
    await expect(page.getByText('Signed in as Ada Admin')).toBeVisible();
    await expect(page.getByText('Connected to daemon (simulated)')).toBeVisible();
    await page.keyboard.press('Escape');
  });

  await test.step('the session survives a reload', async () => {
    await page.reload();
    await expect(page.getByRole('navigation', { name: 'Taskbar' })).toBeVisible();
  });

  await test.step('turn on two-factor authentication', async () => {
    await page.getByRole('button', { name: 'Account menu for Ada Admin' }).click();
    await page.getByRole('menuitem', { name: 'Two-factor authentication' }).click();
    const dialog = page.getByRole('dialog', { name: 'Two-factor authentication' });
    await dialog.getByRole('button', { name: 'Set up' }).click();
    await expect(
      dialog.getByRole('img', { name: 'QR code for your authenticator app' }),
    ).toBeVisible();
    secret = (await dialog.locator('code').textContent()) ?? '';
    await dialog.getByLabel('Code').fill(totp(secret));
    await dialog.getByRole('button', { name: 'Turn on' }).click();
    await expect(dialog.getByRole('listitem')).toHaveCount(10);
    await dialog.getByRole('button', { name: 'Close' }).click();
    await expect(dialog).toBeHidden();
  });

  await test.step('sign out', async () => {
    await signOut(page);
  });

  await test.step('a wrong password shows a translated error with a request id', async () => {
    await signIn(page, 'not the right one');
    const alert = page.getByRole('alert');
    await expect(alert).toContainText('The user name or password is incorrect.');
    await expect(alert.locator('code')).toHaveText(/^[0-9a-f]{32}$/);
    await expect(page.getByLabel('Password', { exact: true })).toBeFocused();
  });

  await test.step('sign in with a code from the authenticator app', async () => {
    await page.getByLabel('Password', { exact: true }).fill(password);
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.getByRole('heading', { name: 'Two-factor authentication' })).toBeVisible();
    // The code used to turn TOTP on is spent; the daemon accepts the next time step as clock drift.
    await page
      .getByLabel('Code from your authenticator app')
      .fill(totp(secret, Date.now() + 30_000));
    await page.getByRole('button', { name: 'Verify' }).click();
    await openDisks(page);
  });

  await test.step('reset the password with the recovery key', async () => {
    await signOut(page);
    await page.getByRole('button', { name: 'Forgot password?' }).click();
    await expect(page.getByRole('heading', { name: 'Reset your password' })).toBeVisible();
    await page.getByLabel('User name').fill('admin');
    await page.getByLabel('Recovery key').fill(recoveryKey.toLowerCase());
    await page.getByLabel('New password', { exact: true }).fill(newPassword);
    await page.getByRole('button', { name: 'Reset password' }).click();

    await expect(page.getByRole('heading', { name: 'Password reset' })).toBeVisible();
    const key = page.getByTestId('recovery-key');
    await expect(key).toHaveText(recoveryKeyFormat);
    await expect(key).not.toHaveText(recoveryKey);
    await confirmStored(page);
    await page.getByRole('button', { name: 'Continue to sign in' }).click();
  });

  await test.step('the old password fails, the new one signs in without TOTP', async () => {
    await expect(page.getByLabel('User name')).toHaveValue('admin');
    await signIn(page, password);
    await expect(page.getByRole('alert')).toContainText('The user name or password is incorrect.');
    await page.getByLabel('Password', { exact: true }).fill(newPassword);
    await page.getByRole('button', { name: 'Sign in' }).click();
    await openDisks(page);
  });
});

test('German browsers get the German interface', async ({ browser }) => {
  const context = await browser.newContext({ locale: 'de-DE' });
  const page = await context.newPage();
  await page.goto('/');

  await expect(page.getByRole('heading', { name: 'Willkommen zurück' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Anmelden' })).toBeVisible();
  await context.close();
});

test('the language select switches the sign-in screen', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('combobox', { name: 'Language' }).selectOption({ label: 'Deutsch' });
  await expect(page.getByRole('heading', { name: 'Willkommen zurück' })).toBeVisible();
  await page.getByRole('combobox', { name: 'Sprache' }).selectOption({ label: 'Automatisch' });
  await expect(page.getByRole('heading', { name: 'Welcome back' })).toBeVisible();
});

test('phones sign in and see the disks full screen', async ({ browser }) => {
  const context = await browser.newContext({
    viewport: { width: 390, height: 844 },
    hasTouch: true,
    isMobile: true,
  });
  const page = await context.newPage();
  await page.goto('/');
  await signIn(page, newPassword);
  await page.getByRole('button', { name: 'Disks, shortcut' }).tap();
  const region = disks(page);
  await expect(region.getByRole('row', { name: /sdg/ })).toBeVisible();
  const box = await region.boundingBox();
  expect(box?.width).toBe(390);
  await context.close();
});
