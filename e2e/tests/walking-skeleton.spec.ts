import { expect, test } from '@playwright/test';
import { totp } from './totp';

const password = 'correct horse battery';

// One flow on one database: the steps depend on each other.
test('first run: set up the admin, see the disks, sign out and sign in again', async ({ page }) => {
  await page.goto('/');

  await test.step('set up the administrator', async () => {
    await expect(page.getByRole('heading', { name: 'Set up Vyxis' })).toBeVisible();
    await page.getByLabel('User name').fill('Admin');
    await page.getByLabel('Display name (optional)').fill('Ada Admin');
    await page.getByLabel('Password', { exact: true }).fill(password);
    await page.getByLabel('Repeat password').fill(password);
    await page.getByRole('button', { name: 'Create administrator' }).click();
  });

  await test.step('store the recovery key', async () => {
    await expect(page.getByTestId('recovery-key')).toHaveText(/^([0-9A-Z]{4}-){6}[0-9A-Z]{2}$/);
    const next = page.getByRole('button', { name: 'Continue to the desktop' });
    await expect(next).toBeDisabled();
    await page.getByLabel('I have stored the recovery key').check();
    await next.click();
  });

  await test.step('see the disk list live', async () => {
    await expect(page.getByText('Signed in as Ada Admin')).toBeVisible();
    const disks = page.getByRole('region', { name: 'Disks' });
    await expect(disks.getByRole('row')).toHaveCount(8);
    await expect(disks.getByRole('row', { name: /sdg/ })).toContainText('VYXTEST06');
  });

  await test.step('sign out', async () => {
    await page.getByRole('button', { name: 'Sign out' }).click();
    await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
  });

  await test.step('a wrong password shows a translated error with a request id', async () => {
    await page.getByLabel('User name').fill('admin');
    await page.getByLabel('Password').fill('not the right one');
    await page.getByRole('button', { name: 'Sign in' }).click();
    const alert = page.getByRole('alert');
    await expect(alert).toContainText('The user name or password is incorrect.');
    await expect(alert.locator('code')).toHaveText(/^[0-9a-f]{32}$/);
  });

  await test.step('sign in and see the disks again', async () => {
    await page.getByLabel('Password').fill(password);
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(
      page.getByRole('region', { name: 'Disks' }).getByRole('row', { name: /sdg/ }),
    ).toBeVisible();
  });

  await test.step('the desktop survives a reload', async () => {
    await page.reload();
    await expect(
      page.getByRole('region', { name: 'Disks' }).getByRole('row', { name: /sda/ }),
    ).toBeVisible();
  });
});

test('German browsers get the German interface', async ({ browser }) => {
  const context = await browser.newContext({ locale: 'de-DE' });
  const page = await context.newPage();
  await page.goto('/');

  await expect(page.getByRole('heading', { name: 'Anmelden' })).toBeVisible();
  await context.close();
});

test('two-factor authentication protects the next sign-in', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('User name').fill('admin');
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();

  await page.getByRole('button', { name: 'Two-factor authentication' }).click();
  const dialog = page.getByRole('dialog', { name: 'Two-factor authentication' });
  await dialog.getByRole('button', { name: 'Set up' }).click();
  await expect(
    dialog.getByRole('img', { name: 'QR code for your authenticator app' }),
  ).toBeVisible();
  const secret = (await dialog.locator('code').textContent()) ?? '';
  await dialog.getByLabel('Code').fill(totp(secret));
  await dialog.getByRole('button', { name: 'Turn on' }).click();
  await expect(dialog.getByRole('listitem')).toHaveCount(10);
  const recoveryCode = (await dialog.getByRole('listitem').first().textContent()) ?? '';
  await dialog.getByRole('button', { name: 'Close' }).click();

  await page.getByRole('button', { name: 'Sign out' }).click();
  await page.getByLabel('User name').fill('admin');
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('heading', { name: 'Two-factor authentication' })).toBeVisible();

  // The code used for confirmation is spent, so the recovery code signs in.
  await page.getByRole('button', { name: 'Use a recovery code instead' }).click();
  await page.getByLabel('Recovery code').fill(recoveryCode);
  await page.getByRole('button', { name: 'Verify' }).click();
  await expect(
    page.getByRole('region', { name: 'Disks' }).getByRole('row', { name: /sdg/ }),
  ).toBeVisible();
});
