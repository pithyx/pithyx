import { expect, test } from './fixtures';
import { installProbe, probeId, probePackage, uninstallProbe } from './probe';
import { appWindow, openFromStart, signInAsAdmin } from './session';

// The probe leaves the box first, so the Store installs it the way an administrator does; the specs after this one
// find it installed again.
test.beforeAll(async ({ baseURL }) => {
  await uninstallProbe(baseURL ?? '');
});

test.afterAll(async ({ baseURL }) => {
  await installProbe(baseURL ?? '');
});

test.beforeEach(async ({ page }) => {
  await signInAsAdmin(page);
});

test('an administrator installs an app from its file in the Store, opens it and uninstalls it', async ({
  page,
}) => {
  await openFromStart(page, 'Store');
  const store = appWindow(page, 'Store');
  await expect(store.getByRole('heading', { name: 'Installed apps' })).toBeVisible();

  await test.step('the package is checked and shown before the install', async () => {
    await store.getByRole('button', { name: 'Install from file' }).click();
    const dialog = page.getByRole('dialog', { name: 'Install an app' });
    await dialog.locator('input[type="file"]').setInputFiles(probePackage());
    await expect(dialog.getByRole('heading', { name: 'SDK demo' })).toBeVisible();
    await expect(dialog.getByText('Shows notifications to the user who uses it.')).toBeVisible();
    await expect(dialog.getByText('not verified')).toBeVisible();
    await expect(dialog.getByRole('radio', { name: 'Everybody' })).toBeChecked();
    const pool = dialog.getByRole('combobox', { name: 'Pool for app data' });
    if (await pool.isVisible()) {
      await pool.selectOption('pithyxtestm');
    }
    await dialog.getByRole('button', { name: 'Install', exact: true }).click();
    await expect(dialog.getByText('SDK demo is installed.')).toBeVisible();
    await dialog.getByRole('button', { name: 'Open app' }).click();
  });

  await test.step('the app opens in its sandboxed window and shows its icon in the menu', async () => {
    await expect(appWindow(page, 'SDK demo')).toBeVisible();
    const frame = page.frameLocator(`iframe[src="/apps/${probeId}/index.html"]`);
    await expect(frame.getByRole('heading', { name: 'SDK demo' })).toBeVisible();
    await page.getByRole('button', { name: 'Pithyx menu' }).click();
    await expect(
      page
        .getByRole('dialog', { name: 'Pithyx menu' })
        .locator(`img[src^="/api/apps/${probeId}/icon"]`)
        .first(),
    ).toBeVisible();
    await page.keyboard.press('Escape');
  });

  await test.step('its page manages it, and uninstalling closes its window', async () => {
    // The app's window opened above the Store; its taskbar entry brings the Store back to the front.
    await page
      .getByRole('navigation', { name: 'Taskbar' })
      .getByRole('button', { name: /^Store/ })
      .click();
    await store.getByRole('button', { name: /SDK demo/ }).click();
    await expect(store.getByRole('heading', { name: 'SDK demo', level: 2 })).toBeVisible();
    await expect(store.getByRole('radio', { name: 'Everybody' })).toBeChecked();
    await store.getByRole('button', { name: 'Uninstall' }).click();
    const dialog = page.getByRole('alertdialog', { name: 'Uninstall SDK demo?' });
    await dialog.getByRole('radio', { name: 'Delete the data' }).check();
    await dialog.getByRole('button', { name: 'Uninstall' }).click();
    await expect(dialog.getByText('SDK demo is removed.')).toBeVisible();
    await expect(appWindow(page, 'SDK demo')).toHaveCount(0);
    await dialog.getByRole('button', { name: 'Close' }).click();
    await expect(store.getByText('No apps are installed yet.')).toBeVisible();
  });
});
