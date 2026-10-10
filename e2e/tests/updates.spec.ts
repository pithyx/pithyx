import { copyFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from './fixtures';
import { appWindow, openFromStart, signInAsAdmin } from './session';

// Settings > Updates against the simulated box (D338): the channel and the file are the corpus of tools/release, signed
// with its beta test key, which simulation trusts. An install walks through its steps and succeeds without a restart.
const corpus = join(dirname(fileURLToPath(import.meta.url)), '../../tools/release/fixtures');
const updateFile = join(corpus, 'pithyx-0.2.0-beta.3-amd64.pithyxupdate');
const sources = join(corpus, 'source');

function publishBetaChannel() {
  const folder = join(process.env.PITHYX_E2E_DATA ?? '', 'update-channels', 'channel-beta');
  mkdirSync(folder, { recursive: true });
  copyFileSync(join(corpus, 'channel-beta.json'), join(folder, 'channel.json'));
  copyFileSync(join(corpus, 'channel-beta.sig'), join(folder, 'channel.json.sig'));
}

test.describe.configure({ mode: 'serial' });

test('the page checks the channel, refuses an untrusted file, installs a verified one and rolls back', async ({
  page,
}) => {
  await signInAsAdmin(page);
  await openFromStart(page, 'Settings');
  const settings = appWindow(page, 'Settings');
  await settings.getByRole('button', { name: /^Updates/ }).click();
  await expect(settings.getByRole('heading', { name: 'Updates', level: 2 })).toBeVisible();
  const channel = settings.getByRole('combobox', { name: 'Channel' });
  await channel.selectOption('stable');
  const uploadInput = settings
    .getByRole('region', { name: 'Install from a file' })
    .locator('input[type="file"]');

  // Nothing published for the stable channel: a clear text instead of a raw error.
  await settings.getByRole('button', { name: 'Check now' }).click();
  await expect(
    settings.getByText(/The update source cannot be reached \(the channel has no release yet\)/),
  ).toBeVisible();

  // A beta file on a stable box: the signature's key is not trusted there, and nothing is kept.
  await uploadInput.setInputFiles(updateFile);
  await expect(settings.getByText(/from a key this box does not trust/)).toBeVisible();

  publishBetaChannel();
  await channel.selectOption('beta');
  await settings.getByRole('button', { name: 'Check now' }).click();
  await expect(settings.getByText('Pithyx 0.2.0-beta.3 is available')).toBeVisible();

  await uploadInput.setInputFiles(updateFile);
  await expect(settings.getByText('Update file for Pithyx 0.2.0-beta.3 checked')).toBeVisible();
  await expect(settings.getByText('Signed by a key this box trusts.')).toBeVisible();

  await settings
    .getByRole('region', { name: 'Install from a file' })
    .getByRole('button', { name: 'Install 0.2.0-beta.3' })
    .click();
  const install = page.getByRole('dialog', { name: 'Install Pithyx 0.2.0-beta.3?' });
  await expect(install).toContainText('First the box takes a snapshot of the system');
  await install.getByRole('button', { name: 'Install and restart' }).click();
  const progress = settings.getByRole('region', { name: 'Update in progress' });
  await expect(progress.getByText('Snapshot')).toBeVisible();
  await expect(progress.getByText('Done.')).toBeVisible();

  const history = settings.getByRole('region', { name: 'History' });
  await expect(
    history.getByRole('cell', { name: /Update .* → 0\.2\.0-beta\.3 \(from a file\)/ }),
  ).toBeVisible();
  await expect(history.getByRole('cell', { name: 'Succeeded' })).toBeVisible();

  const snapshots = settings.getByRole('region', { name: 'Snapshots' });
  await snapshots.getByRole('button', { name: 'Back to this version' }).first().click();
  const rollback = page.getByRole('dialog', { name: /^Go back to Pithyx / });
  const confirm = rollback.getByRole('button', { name: 'Go back and restart' });
  await expect(confirm).toBeDisabled();
  const version =
    /Go back to Pithyx (.+)\?/.exec(
      (await rollback.getByRole('heading').first().textContent()) ?? '',
    )?.[1] ?? '';
  await rollback.getByLabel(`Type ${version} to confirm`).fill(version);
  await confirm.click();
  await expect(history.getByRole('cell', { name: /^Back from / })).toBeVisible();

  await channel.selectOption('stable');
});

test('the page shows the update source and saves a checked source file for the recovery menu', async ({
  page,
}) => {
  await signInAsAdmin(page);
  await openFromStart(page, 'Settings');
  const settings = appWindow(page, 'Settings');
  await settings.getByRole('button', { name: /^Updates/ }).click();
  const source = settings.getByRole('region', { name: 'Update source' });
  await expect(source.getByText('Pithyx (the project)')).toBeVisible();
  const input = source.locator('input[type="file"]');

  // Only https with {channel}: refused, and nothing is kept.
  await input.setInputFiles(join(sources, 'http.json'));
  await expect(source.getByText(/the URL must use https and contain \{channel\}/)).toBeVisible();

  await input.setInputFiles(join(sources, 'valid.json'));
  await expect(source.getByText('Example fork is saved for the recovery menu')).toBeVisible();
  await expect(source.getByText(/Nothing changes until it is chosen at the console/)).toBeVisible();
  // Saving changes nothing the box trusts.
  await expect(source.getByText('Pithyx (the project)')).toBeVisible();

  await source.getByRole('button', { name: 'Remove' }).click();
  await expect(source.getByText('Example fork is saved for the recovery menu')).toHaveCount(0);
});
