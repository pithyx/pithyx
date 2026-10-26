import { expect, test } from './fixtures';
import { createTestCatalog } from './catalog';
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

test('an administrator adds a catalog in the Store settings and installs an app from it', async ({
  page,
}) => {
  const url = createTestCatalog();
  await openFromStart(page, 'Store');
  const store = appWindow(page, 'Store');
  await expect(store.getByRole('tab', { name: 'Installed' })).toBeVisible();
  // Both project catalogs are on (the community one since D343) and have not been fetched in the test.
  await expect(store.getByRole('tab', { name: 'Official' })).toBeVisible();
  await expect(store.getByRole('tab', { name: 'Community' })).toBeVisible();

  await test.step('the gear opens the settings with the policy, the check time, the channel and the catalogs', async () => {
    await store.getByRole('button', { name: 'Store settings' }).click();
    await expect(store.getByRole('heading', { name: 'Store settings' })).toBeVisible();
    await expect(store.getByRole('radio', { name: /Tell administrators/ })).toBeChecked();
    await expect(store.getByRole('textbox', { name: 'Daily check time' })).toHaveValue('03:00');
    await expect(store.getByRole('radio', { name: /Stable/ })).toBeChecked();
    await expect(store.getByText('https://github.com/pithyx/apps-community.git')).toBeVisible();
    await store.getByRole('button', { name: 'Add catalog' }).click();
    const dialog = page.getByRole('dialog', { name: 'Add a catalog' });
    await dialog.getByRole('textbox', { name: 'Repository URL' }).fill(url);
    await dialog.getByRole('button', { name: 'Add', exact: true }).click();
    await expect(dialog.getByText('The catalog is being fetched.')).toBeVisible();
    await dialog.getByRole('button', { name: 'Close' }).click();
    await expect(store.getByText('E2E catalog', { exact: true })).toBeVisible();
    await expect(store.getByText('Up to date', { exact: true })).toBeVisible();
    await store.getByRole('button', { name: 'Store', exact: true }).click();
  });

  await test.step('the tab Own lists the catalog app, and its page installs it', async () => {
    await store.getByRole('tab', { name: 'Own' }).click();
    await store.getByRole('searchbox', { name: 'Search apps' }).fill('demo');
    await store.getByRole('button', { name: /SDK demo/ }).click();
    await expect(store.getByRole('heading', { name: 'SDK demo', level: 2 })).toBeVisible();
    await expect(store.getByText('not verified by the Pithyx project')).toBeVisible();
    await expect(
      store.getByRole('link', { name: 'Source of this version in the catalog' }),
    ).toHaveAttribute(
      'href',
      `https://example.com/e2e-catalog/tree/${'a'.repeat(40)}/apps/${probeId}`,
    );
    await store.getByRole('button', { name: 'Install', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: 'Install an app' });
    await expect(dialog.getByText('From the catalog com.example.e2e')).toBeVisible();
    const pool = dialog.getByRole('combobox', { name: 'Pool for app data' });
    if (await pool.isVisible()) {
      await pool.selectOption('pithyxtestm');
    }
    await dialog.getByRole('button', { name: 'Install', exact: true }).click();
    await expect(dialog.getByText('SDK demo is installed.')).toBeVisible();
    await dialog.getByRole('button', { name: 'Close' }).click();
    await expect(store.getByText('Version 1.0.0 installed')).toBeVisible();
  });

  await test.step('the installed app names its catalog and follows the update policy of the box', async () => {
    await store.getByRole('button', { name: 'Own' }).click();
    await store.getByRole('tab', { name: 'Installed' }).click();
    await store.getByRole('button', { name: /SDK demo/ }).click();
    await expect(store.getByText('Installed from the catalog com.example.e2e')).toBeVisible();
    await expect(
      store.getByRole('combobox', { name: 'When a catalog has a newer version' }),
    ).toHaveValue('box');
    await expect(
      store.getByRole('link', { name: 'Source of this version in the catalog' }),
    ).toBeVisible();
  });

  await test.step('the catalog tab opens the same page of the installed app', async () => {
    await store.getByRole('button', { name: 'Installed', exact: true }).click();
    await store.getByRole('tab', { name: 'Own' }).click();
    await store.getByRole('searchbox', { name: 'Search apps' }).fill('demo');
    await store.getByRole('button', { name: /SDK demo/ }).click();
    await expect(store.getByText('Installed from the catalog com.example.e2e')).toBeVisible();
    await expect(store.getByRole('button', { name: 'Uninstall' })).toBeVisible();
    await expect(
      store.getByRole('combobox', { name: 'When a catalog has a newer version' }),
    ).toBeVisible();
    await expect(store.getByRole('button', { name: 'Own', exact: true })).toBeVisible();
  });
});
