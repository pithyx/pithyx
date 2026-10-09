import type { Locator, Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { appWindow, openFromStart, signInAsAdmin } from './session';

// Settings > Network against the simulated box (three cards; a loopback browser counts as coming in on enp1s0). Every
// change waits for Keep or Undo; the tests leave the network as they found it.
async function openNetwork(page: Page) {
  await signInAsAdmin(page);
  await openFromStart(page, 'Settings');
  const settings = appWindow(page, 'Settings');
  await settings.getByRole('button', { name: /^Network/ }).click();
  await expect(settings.getByRole('heading', { name: 'Network', level: 2 })).toBeVisible();
  return settings;
}

function card(settings: Locator, name: string) {
  // The heading names the interface, then its badges: "bond0 Bond Connected" but not "bond0.20 VLAN".
  return settings.getByRole('region', { name: new RegExp(`^${name.replace('.', '\\.')}\\s`) });
}

async function apply(page: Page, settings: Locator) {
  await settings.getByRole('button', { name: 'Apply…' }).click();
  const dialog = page.getByRole('dialog', { name: 'Apply network settings' });
  await expect(dialog).toBeVisible();
  return dialog;
}

test('a static address waits for its confirmation and Undo brings DHCP back', async ({ page }) => {
  const settings = await openNetwork(page);
  await expect(card(settings, 'enp1s0').getByText('This connection')).toBeVisible();

  await settings.getByRole('button', { name: 'Edit enp1s0' }).click();
  const first = page.getByRole('dialog', { name: 'Edit enp1s0' });
  await expect(first.getByRole('note')).toContainText('This browser reaches the box');
  await first.getByRole('button', { name: 'Cancel' }).click();

  await settings.getByRole('button', { name: 'Edit enp2s0' }).click();
  const dialog = page.getByRole('dialog', { name: 'Edit enp2s0' });
  const ipv4 = dialog.getByRole('group', { name: 'IPv4' });
  await ipv4.getByRole('combobox', { name: 'Address' }).selectOption('static');
  await ipv4.getByLabel('Address with prefix length').fill('192.168.50.10/24');
  await ipv4.getByLabel('Gateway (optional)').fill('192.168.60.1');
  await dialog.getByRole('button', { name: 'Done' }).click();
  await expect(card(settings, 'enp2s0').getByText('Changed, not applied')).toBeVisible();

  // The daemon checks before anything changes: the gateway is outside the network.
  await settings.getByRole('button', { name: 'Apply…' }).click();
  await expect(
    settings.getByText('The IPv4 gateway of enp2s0 must be an address in its network.'),
  ).toBeVisible();
  await settings.getByRole('button', { name: 'Edit enp2s0' }).click();
  await dialog
    .getByRole('group', { name: 'IPv4' })
    .getByLabel('Gateway (optional)')
    .fill('192.168.50.1');
  await dialog.getByRole('button', { name: 'Done' }).click();

  const confirm = await apply(page, settings);
  await expect(confirm).toContainText('Changes: enp2s0.');
  await expect(confirm).not.toContainText('This browser reaches the box through');
  await confirm.getByRole('button', { name: 'Apply', exact: true }).click();

  const banner = settings.getByRole('alert', { name: 'Network change waiting for confirmation' });
  await expect(banner).toContainText(/\d+ seconds left to keep the new network settings/);
  await expect(banner.getByRole('link', { name: /192\.168\.50\.10/ })).toBeVisible();
  await expect(card(settings, 'enp2s0')).toContainText('192.168.50.10/24');
  await banner.getByRole('button', { name: 'Undo now' }).click();
  await expect(banner).toHaveCount(0);
  await expect(card(settings, 'enp2s0')).toContainText('Automatic (DHCP)');
  await expect(card(settings, 'enp2s0')).not.toContainText('192.168.50.10');
});

test('a bond with a VLAN on it is kept and removed again', async ({ page }) => {
  const settings = await openNetwork(page);

  await settings.getByRole('button', { name: 'Add bond' }).click();
  const bond = page.getByRole('dialog', { name: 'New bond' });
  await bond.getByText('enp2s0').click();
  await bond.getByText('enp3s0').click();
  await bond.getByRole('combobox', { name: 'Mode' }).selectOption('activeBackup');
  await bond.getByRole('combobox', { name: 'Primary card' }).selectOption('enp2s0');
  await bond.getByLabel('MTU').fill('9000');
  await bond.getByRole('button', { name: 'Done' }).click();
  await expect(card(settings, 'bond0').getByText('New, not applied')).toBeVisible();

  await settings.getByRole('button', { name: 'Add VLAN' }).click();
  const vlan = page.getByRole('dialog', { name: 'New VLAN' });
  await vlan.getByRole('combobox', { name: 'Parent' }).selectOption('bond0');
  await vlan.getByLabel('VLAN ID').fill('20');
  const ipv4 = vlan.getByRole('group', { name: 'IPv4' });
  await ipv4.getByRole('combobox', { name: 'Address' }).selectOption('static');
  await ipv4.getByLabel('Address with prefix length').fill('10.20.0.5/24');
  await vlan
    .getByRole('group', { name: 'IPv6' })
    .getByRole('combobox', { name: 'Address' })
    .selectOption('off');
  await vlan.getByRole('button', { name: 'Done' }).click();

  const confirm = await apply(page, settings);
  await expect(confirm).toContainText('bond0');
  await expect(confirm).toContainText('bond0.20');
  await confirm.getByRole('button', { name: 'Apply', exact: true }).click();
  const banner = settings.getByRole('alert', { name: 'Network change waiting for confirmation' });
  await banner.getByRole('button', { name: 'Keep' }).click();
  await expect(banner).toHaveCount(0);

  await expect(card(settings, 'bond0')).toContainText('9000');
  await expect(card(settings, 'enp2s0')).toContainText('In bond');
  await expect(card(settings, 'bond0.20')).toContainText('ID 20 on bond0');
  await expect(card(settings, 'bond0.20')).toContainText('10.20.0.5/24');

  await settings.getByRole('button', { name: 'Remove the settings of bond0.20' }).click();
  await settings.getByRole('button', { name: 'Remove the settings of bond0' }).click();
  const back = await apply(page, settings);
  await back.getByRole('button', { name: 'Apply', exact: true }).click();
  await banner.getByRole('button', { name: 'Keep' }).click();
  await expect(banner).toHaveCount(0);
  await expect(card(settings, 'bond0')).toHaveCount(0);
  await expect(card(settings, 'enp2s0')).toContainText('Automatic (DHCP)');
});
