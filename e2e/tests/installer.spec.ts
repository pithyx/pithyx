import { expect, test } from './fixtures';

// The installer of the live system against a simulated daemon (PITHYX_INSTALLER=1): the steps, the mirror, the
// summary, the recovery key and the restart. The daemon only walks through the installation steps.
test('the first browser installs Pithyx on a mirror and restarts into it', async ({
  page,
  browser,
}) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Welcome to Pithyx' })).toBeVisible();
  await expect(page.getByText('Started with UEFI')).toBeVisible();
  await page.getByRole('button', { name: 'Begin' }).click();

  await expect(page.getByRole('heading', { name: 'Name and region' })).toBeVisible();
  await page.getByLabel('Name of the NAS').fill('NAS E2E');
  await page.getByRole('button', { name: 'Next' }).click();
  await expect(page.getByText('Use lowercase letters, digits and hyphens')).toBeVisible();
  await page.getByLabel('Name of the NAS').fill('nas-e2e');
  await page.getByLabel('Time zone').selectOption('Europe/Berlin');
  await page.getByRole('button', { name: 'Next' }).click();

  await expect(page.getByRole('heading', { name: 'Network' })).toBeVisible();
  await page.getByRole('button', { name: 'Next' }).click();

  await expect(page.getByRole('heading', { name: 'System disk' })).toBeVisible();
  await expect(page.getByText('Not offered: smaller than 40 GB')).toBeVisible();
  await page.getByRole('button', { name: 'Next' }).click();
  await expect(page.getByRole('alert')).toHaveText('Choose a disk');
  const first = page.getByRole('group', { name: 'Disk', exact: true });
  await first.getByRole('radio').and(page.locator(':enabled')).first().check();
  await page.getByText('Mirror on two disks').click();
  await page.getByRole('button', { name: 'Next' }).click();
  await expect(page.getByRole('alert')).toHaveText(
    'Choose a second disk or switch the mirror off.',
  );
  const second = page.getByRole('group', { name: 'Second disk' });
  await second.getByRole('radio').and(page.locator(':enabled')).first().check();
  await page.getByRole('button', { name: 'Next' }).click();

  await expect(page.getByRole('heading', { name: 'Administrator' })).toBeVisible();
  await page.getByLabel('User name').fill('admin');
  await page.getByLabel('Password', { exact: true }).fill('correct horse battery staple');
  await page.getByLabel('Repeat password').fill('correct horse battery staple');
  await page.getByRole('button', { name: 'Next' }).click();

  await expect(page.getByRole('heading', { name: 'Summary' })).toBeVisible();
  await expect(page.getByText('nas-e2e')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'These disks will be erased' })).toBeVisible();
  await expect(page.locator('ul li').filter({ hasText: 'serial' })).toHaveCount(2);
  await page.getByText('All data on these disks will be erased').click();

  // Someone else who opens the page now only learns that the installation is taken.
  const other = await browser.newPage();
  await other.goto('/');
  await expect(other.getByRole('heading', { name: 'Installation in use' })).toBeVisible();
  await other.close();

  await page.getByRole('button', { name: 'Install Pithyx' }).click();
  await expect(page.getByRole('heading', { name: 'Pithyx is installed' })).toBeVisible({
    timeout: 15_000,
  });
  await expect(page.getByTestId('recovery-key')).toHaveText(/^PTHX-([0-9A-Z]{4}-){6}[0-9A-Z]{2}$/);
  await page.getByText('I have stored the recovery key').click();
  await page.getByRole('button', { name: 'Continue' }).click();

  await expect(page.getByRole('heading', { name: 'Remove the USB stick' })).toBeVisible();
  await page.getByRole('button', { name: 'Restart now' }).click();
  await expect(page.getByRole('heading', { name: 'Pithyx is starting' })).toBeVisible();
});
