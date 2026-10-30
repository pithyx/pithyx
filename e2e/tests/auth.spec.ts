import { type Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { admin, openAccountMenu, openFromDesktop, openPersonalSettings } from './session';
import { totp } from './totp';

const password = 'correct horse battery';
const newPassword = admin.password;
const recoveryKeyFormat = /^PTHX-([0-9A-Z]{4}-){6}[0-9A-Z]{2}$/;

async function signIn(page: Page, secret: string) {
  await page.getByLabel('User name').fill('admin');
  await page.getByLabel('Password', { exact: true }).fill(secret);
  await page.getByRole('button', { name: 'Sign in' }).click();
}

/** What the clipboard holds, pasted with the keyboard into a temporary field (reading it needs a secure context). */
async function pasted(page: Page): Promise<string> {
  await page.evaluate(() => {
    const field = document.createElement('textarea');
    field.id = 'paste-probe';
    document.body.append(field);
    field.focus();
  });
  await page.keyboard.press('ControlOrMeta+v');
  return page.evaluate(() => {
    const field = document.getElementById('paste-probe') as HTMLTextAreaElement;
    field.remove();
    return field.value;
  });
}

async function signOut(page: Page) {
  await page.getByRole('button', { name: 'Pithyx menu' }).click();
  await page
    .getByRole('dialog', { name: 'Pithyx menu' })
    .getByRole('button', { name: 'Sign out' })
    .click();
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
  return page.getByRole('region', { name: 'Settings', exact: true });
}

async function openDisks(page: Page) {
  await openFromDesktop(page, 'Settings');
  await disks(page).getByRole('button', { name: 'Disks', exact: true }).click();
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
  // This project runs on http://pithyx.test (playwright.config.ts): not localhost, so not a secure context.
  expect(await page.evaluate(() => window.isSecureContext)).toBe(false);

  await test.step('set up the administrator', async () => {
    await expect(page.getByRole('heading', { name: 'Set up Pithyx' })).toBeVisible();
    await expect(page.getByText('Step 1 of 2')).toBeVisible();
    await page.getByLabel('User name').fill('Admin');
    await page.getByLabel('Display name (optional)').fill('Ada Admin');
    // A well-known password is refused with the meter's text; any other one is allowed.
    const passwordField = page.getByLabel('Password', { exact: true });
    await passwordField.fill('password1');
    await expect(page.getByText('Strength: Weak.')).toBeVisible();
    await page.getByLabel('Repeat password').fill('password1');
    await page.getByRole('button', { name: 'Create administrator' }).click();
    await expect(passwordField).toBeFocused();
    await expect(passwordField).toHaveAccessibleDescription(/^This password is too well known\./);
    await passwordField.fill(password);
    await expect(page.getByText('Strength: Strong.')).toBeVisible();
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
    await page.getByRole('button', { name: 'Copy key' }).click();
    await expect(page.getByRole('button', { name: 'Copied' })).toBeVisible();
    expect(await pasted(page)).toBe(recoveryKey);
    const next = page.getByRole('button', { name: 'Continue to the desktop' });
    await expect(next).toBeDisabled();
    await confirmStored(page);
    await next.click();
  });

  await test.step('the desktop shows the disk list live', async () => {
    await openDisks(page);
    await expect(disks(page).getByRole('row')).toHaveCount(8);
    await expect(disks(page).getByRole('row', { name: /sdg/ })).toContainText('VYXTEST06');
    await openAccountMenu(page);
    await expect(page.getByText('Signed in as Ada Admin')).toBeVisible();
    await expect(page.getByText('Connected to daemon (simulated)')).toBeVisible();
    await page.keyboard.press('Escape');
  });

  await test.step('the session survives a reload', async () => {
    await page.reload();
    await expect(page.getByRole('navigation', { name: 'Taskbar' })).toBeVisible();
  });

  await test.step('turn on two-factor authentication', async () => {
    const settings = await openPersonalSettings(page, 'Sign-in and security');
    await settings.getByRole('button', { name: 'Two-factor authentication', exact: true }).click();
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

  await test.step('a wrong password shows a translated error without a request id', async () => {
    await signIn(page, 'not the right one');
    const alert = page.getByRole('alert');
    await expect(alert).toContainText('The user name or password is incorrect.');
    await expect(alert.locator('code')).toHaveCount(0);
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
    // A browser that refuses to copy: the key is selected instead, and a hint says how to copy it.
    await page.evaluate(() => {
      // eslint-disable-next-line @typescript-eslint/no-deprecated -- replaced to simulate a refusing browser
      document.execCommand = () => false;
    });
    await page.getByRole('button', { name: 'Copy key' }).click();
    await expect(page.getByRole('status')).toContainText('The key is selected');
    const selected = await page.evaluate(() => window.getSelection()?.toString() ?? '');
    expect(selected.replace(/\s/g, '')).toBe((await key.textContent()) ?? '');
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

test('the language list switches the sign-in screen with the mouse and the keyboard', async ({
  page,
}) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Language: Automatic' }).click();
  const list = page.getByRole('listbox', { name: 'Language' });
  await expect(list).toBeFocused();
  await list.getByRole('option', { name: 'Deutsch' }).click();
  await expect(list).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Willkommen zurück' })).toBeVisible();

  const button = page.getByRole('button', { name: 'Sprache: Deutsch' });
  await expect(button).toBeFocused();
  await page.keyboard.press('ArrowDown');
  await expect(page.getByRole('option', { name: 'Deutsch' })).toHaveAttribute(
    'aria-selected',
    'true',
  );
  await page.keyboard.press('Escape');
  await expect(page.getByRole('listbox')).toHaveCount(0);
  await expect(button).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('listbox')).toBeFocused();
  await page.keyboard.press('Home');
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Welcome back' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Language: Automatic' })).toBeFocused();
});

test('a language list that opens under a resting pointer keeps the keyboard choice', async ({
  page,
}) => {
  await page.goto('/');
  const button = page.getByRole('button', { name: 'Language: Automatic' });
  await button.click();
  const box = await page.getByRole('option', { name: 'Deutsch' }).boundingBox();
  if (box === null) {
    throw new Error('no option');
  }
  const x = box.x + box.width / 2;
  const y = box.y + box.height / 2;
  await page.mouse.move(x, y);
  await page.keyboard.press('Escape');
  await expect(button).toBeFocused();

  // The list opens again under the resting pointer and Home moves to the first option. The browser reports the
  // pointer over the new list by itself, without a move, sometimes only after Home (the flake of #276); the test sends
  // that report after Home itself, and it must not take the option under the pointer.
  await page.keyboard.press('Enter');
  await expect(page.getByRole('listbox')).toBeFocused();
  await page.keyboard.press('Home');
  const deutsch = page.getByRole('option', { name: 'Deutsch' });
  for (const type of ['pointerover', 'pointerenter', 'pointermove']) {
    await deutsch.dispatchEvent(type, { clientX: x, clientY: y, bubbles: type !== 'pointerenter' });
  }
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Welcome back' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Language: Automatic' })).toBeFocused();
});

test('phones sign in and see the storage app full screen', async ({ browser }) => {
  const context = await browser.newContext({
    viewport: { width: 390, height: 844 },
    hasTouch: true,
    isMobile: true,
  });
  const page = await context.newPage();
  await page.goto('/');
  await signIn(page, newPassword);
  await page.getByRole('option', { name: 'Settings, shortcut' }).tap();
  const region = disks(page);
  await region.getByRole('button', { name: 'Disks', exact: true }).tap();
  await expect(region.getByRole('row', { name: /sdg/ })).toBeVisible();
  const box = await region.boundingBox();
  expect(box?.width).toBe(390);
  await context.close();
});
