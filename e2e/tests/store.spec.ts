import type { Locator } from '@playwright/test';
import { expect, test } from './fixtures';
import { createTestCatalog } from './catalog';
import { installProbe, probeId, probePackage, rootProbePackage, uninstallProbe } from './probe';
import { admin, appWindow, openFromStart, signInAsAdmin } from './session';

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

/** Opens a place of the Store's navigation. */
async function show(store: Locator, place: string) {
  await store
    .getByRole('navigation', { name: 'Store' })
    .getByRole('button', { name: new RegExp(`^${place}`) })
    .click();
}

test('an administrator installs an app from its file in the Store, opens it and uninstalls it', async ({
  page,
}) => {
  await openFromStart(page, 'Store');
  const store = appWindow(page, 'Store');
  await expect(store.getByRole('heading', { name: 'Discover', level: 1 })).toBeVisible();
  await show(store, 'Installed');
  await expect(store.getByRole('heading', { name: 'Installed', level: 1 })).toBeVisible();

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
    const card = store.getByRole('article', { name: 'SDK demo' });
    await expect(card.getByText('Installed from a file')).toBeVisible();
    await expect(card.getByRole('button', { name: 'Open' })).toBeEnabled();
    await card.getByRole('button', { name: 'SDK demo' }).click();
    await expect(store.getByRole('heading', { name: 'SDK demo', level: 1 })).toBeVisible();
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

test('an administrator adds a catalog in the Store and installs an app from it', async ({
  page,
}) => {
  const url = createTestCatalog();
  await openFromStart(page, 'Store');
  const store = appWindow(page, 'Store');
  const navigation = store.getByRole('navigation', { name: 'Store' });
  for (const place of ['Discover', 'All apps', 'Installed', 'Updates', 'Catalogs']) {
    await expect(navigation.getByRole('button', { name: new RegExp(`^${place}`) })).toBeVisible();
  }

  await test.step('Updates holds the policy and the check time, Catalogs the channel and the catalogs', async () => {
    await show(store, 'Updates');
    await expect(store.getByText('Every installed app is up to date.')).toBeVisible();
    await expect(store.getByRole('radio', { name: /Tell administrators/ })).toBeChecked();
    await expect(store.getByRole('textbox', { name: 'Daily check time' })).toHaveValue('03:00');
    await show(store, 'Catalogs');
    await expect(store.getByRole('heading', { name: 'Catalogs', level: 1 })).toBeVisible();
    await expect(store.getByRole('radio', { name: /Stable/ })).toBeChecked();
    // Both project catalogs are on (the community one since D343) and have not been fetched in the test.
    await expect(store.getByRole('list', { name: 'Catalogs' }).getByRole('listitem')).toHaveCount(
      2,
    );
    await store.getByRole('button', { name: 'Add catalog' }).click();
    const dialog = page.getByRole('dialog', { name: 'Add a catalog' });
    await dialog.getByRole('textbox', { name: 'Repository URL' }).fill(url);
    await dialog.getByRole('button', { name: 'Add', exact: true }).click();
    await expect(dialog.getByText('The catalog is being fetched.')).toBeVisible();
    await dialog.getByRole('button', { name: 'Close' }).click();
    await expect(store.getByRole('heading', { name: /^E2E catalog/ })).toBeVisible();
    await expect(store.getByText('Up to date', { exact: true })).toBeVisible();
  });

  await test.step('Discover features the catalog app, and its page installs it', async () => {
    await show(store, 'Discover');
    const featured = store.getByRole('article', { name: 'SDK demo' }).first();
    await expect(featured.getByText('New in E2E catalog')).toBeVisible();
    await featured.getByRole('button', { name: 'SDK demo' }).click();
    await expect(store.getByRole('heading', { name: 'SDK demo', level: 1 })).toBeVisible();
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
    await show(store, 'Installed');
    await store
      .getByRole('article', { name: 'SDK demo' })
      .getByRole('button', { name: 'SDK demo' })
      .click();
    await expect(store.getByText('Installed from the catalog com.example.e2e')).toBeVisible();
    await expect(
      store.getByRole('combobox', { name: 'When a catalog has a newer version' }),
    ).toHaveValue('box');
    await expect(
      store.getByRole('link', { name: 'Source of this version in the catalog' }),
    ).toBeVisible();
  });

  await test.step('the search over every catalog opens the same page of the installed app', async () => {
    await show(store, 'All apps');
    await store.getByRole('searchbox', { name: 'Search apps' }).fill('demo');
    await expect(store.getByRole('heading', { name: 'Results for “demo”' })).toBeVisible();
    const card = store.getByRole('article', { name: 'SDK demo' });
    await expect(card.getByText('Installed', { exact: true })).toBeVisible();
    await card.getByRole('button', { name: 'SDK demo' }).click();
    await expect(store.getByText('Installed from the catalog com.example.e2e')).toBeVisible();
    await expect(store.getByRole('button', { name: 'Uninstall' })).toBeVisible();
    await expect(
      store.getByRole('combobox', { name: 'When a catalog has a newer version' }),
    ).toBeVisible();
    await expect(store.getByRole('button', { name: 'All apps', exact: true })).toHaveCount(2);
  });
});

test('root access for an app takes a red step of its own with the typed app name (D388)', async ({
  page,
}) => {
  await openFromStart(page, 'Store');
  const store = appWindow(page, 'Store');
  await show(store, 'Installed');
  await store.getByRole('button', { name: 'Install from file' }).click();
  const dialog = page.getByRole('dialog', { name: 'Install an app' });
  await dialog.locator('input[type="file"]').setInputFiles(rootProbePackage());

  await test.step("the right shows its tier and the author's reason", async () => {
    await expect(dialog.getByRole('heading', { name: 'Root probe' })).toBeVisible();
    await expect(dialog.getByText('Root access', { exact: true })).toBeVisible();
    await expect(
      dialog.getByText('Why the app needs it: Manages every container of the box.'),
    ).toBeVisible();
    await expect(dialog.getByRole('radio', { name: 'Grant these rights' })).toBeChecked();
    await expect(dialog.getByRole('button', { name: 'Install', exact: true })).toHaveCount(0);
    await dialog.getByRole('button', { name: 'Continue' }).click();
  });

  await test.step('the critical step wants the name and the password, and a paste gets a reminder', async () => {
    await expect(
      dialog.getByRole('heading', { name: 'Root probe gets root access to the box' }),
    ).toBeVisible();
    const confirm = dialog.getByRole('button', { name: 'Install with root access' });
    const name = dialog.getByRole('textbox', { name: /Type the app's name/ });
    await expect(confirm).toBeDisabled();
    await name.fill('Root');
    await name.dispatchEvent('paste');
    await expect(dialog.getByRole('status')).toContainText(
      'Pasting is fine, but this is not a joke',
    );
    await name.fill('root probe');
    await dialog.getByLabel('Your password').fill(admin.password);
    await expect(confirm).toBeEnabled();
    await dialog.getByRole('button', { name: 'Back' }).click();
    await expect(dialog.getByRole('button', { name: 'Continue' })).toBeVisible();
    await dialog.getByRole('button', { name: 'Cancel' }).click();
  });
});
