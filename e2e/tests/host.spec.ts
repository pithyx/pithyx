import AxeBuilder from '@axe-core/playwright';
import type { Page, WebSocketRoute } from '@playwright/test';
import { expect, test } from './fixtures';
import { appWindow, openFromDesktop, openFromStart, signInAsAdmin } from './session';

// What the desktop does for app windows (A7): launch arguments, visibility and connection, app menus, badges and
// attention, unsaved changes and apps that do not answer, notification buttons, the clipboard, new tabs, the console
// mirror of app logs and the default icon. The SDK demo is the probe app.

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];

async function violations(page: Page) {
  const result = await new AxeBuilder({ page })
    .withTags(TAGS)
    .exclude('[data-live-announcer]')
    .analyze();
  return result.violations.map(
    (violation) =>
      `${violation.id}: ${violation.help} (${violation.nodes.map((node) => node.target.join(' ')).join(', ')})`,
  );
}

const FRAME = 'iframe[src="/apps/demo/index.html"]';

async function openDemo(page: Page) {
  await openFromStart(page, 'SDK demo');
  const window = appWindow(page, 'SDK demo');
  await expect(window).toBeVisible();
  const app = page.frameLocator(FRAME);
  await expect(app.getByRole('heading', { name: 'SDK demo' })).toBeVisible();
  return { window, app };
}

function taskbarEntry(page: Page) {
  return page
    .getByRole('navigation', { name: 'Taskbar' })
    .getByRole('button', { name: /^SDK demo/ });
}

test('a single app gets new launch arguments in its open window instead of a second one', async ({
  page,
}) => {
  await signInAsAdmin(page);
  const { app } = await openDemo(page);
  await expect(app.locator('[data-fact="launch"]')).toHaveText('null');
  await openFromDesktop(page, 'Storage');
  await expect(appWindow(page, 'Storage')).toBeVisible();

  await openFromStart(page, 'SDK demo');
  await expect(app.getByRole('list', { name: 'Events' })).toContainText('launch');
  await expect(page.locator(FRAME)).toHaveCount(1);
  await expect(taskbarEntry(page)).toHaveAccessibleName(/active/);
  await expect(
    taskbarEntry(page).locator('[data-default-icon]'),
    'an app without an icon gets the default icon',
  ).toHaveCount(1);
});

test('the app learns when its window is hidden and when the box cannot be reached', async ({
  page,
}) => {
  let blocked = false;
  const sockets: WebSocketRoute[] = [];
  // While blocked, every new connection of the desktop closes at once, like a box that restarts.
  await page.routeWebSocket('**/ws', (socket) => {
    if (blocked) {
      void socket.close({ code: 1012 });
      return;
    }
    socket.connectToServer();
    sockets.push(socket);
  });
  await signInAsAdmin(page);
  const { app } = await openDemo(page);
  const events = app.getByRole('list', { name: 'Events' });
  await expect(app.locator('[data-fact="visible"]')).toHaveText('yes');
  await expect(app.locator('[data-fact="online"]')).toHaveText('yes');

  await taskbarEntry(page).click();
  await expect(appWindow(page, 'SDK demo')).toBeHidden();
  await taskbarEntry(page).click();
  await expect(events).toContainText('visibilityChange');
  await expect(app.locator('[data-fact="visible"]')).toHaveText('yes');

  blocked = true;
  for (const socket of sockets.splice(0)) {
    await socket.close({ code: 1012 });
  }
  await expect(app.locator('[data-fact="online"]')).toHaveText('no');
  await expect(events).toContainText('connectionChange');
  blocked = false;
  await expect(app.locator('[data-fact="online"]')).toHaveText('yes', { timeout: 20_000 });
});

test('the desktop draws app menus like its own, for the pointer and the keyboard', async ({
  page,
}) => {
  await signInAsAdmin(page);
  const { app } = await openDemo(page);
  const menu = page.getByRole('menu', { name: 'Menu of SDK demo' });

  await app.getByRole('button', { name: 'Open menu' }).click();
  await expect(menu).toBeVisible();
  await expect(menu.getByRole('menuitemcheckbox', { name: 'Pinned' })).toHaveAttribute(
    'aria-checked',
    'true',
  );
  await expect(menu.getByRole('menuitemcheckbox', { name: 'Not available' })).toBeDisabled();
  // A menu with checked entries starts on the checked one, like every selectable menu of React Aria.
  await expect(menu.getByRole('menuitemcheckbox', { name: 'Pinned' })).toBeFocused();
  expect(await violations(page)).toEqual([]);
  await page.keyboard.press('ArrowUp');
  await expect(menu.getByRole('menuitemcheckbox', { name: /^Copy/ })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(menu).toBeHidden();
  await expect(app.locator('[data-fact="picked"]')).toHaveText('copy');

  await app.getByRole('button', { name: 'Open menu' }).click({ button: 'right' });
  await expect(menu).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(menu).toBeHidden();
  await expect(app.locator('[data-fact="picked"]')).toHaveText('dismissed');

  await app.getByRole('button', { name: 'Open menu' }).click();
  await menu.getByRole('menuitemcheckbox', { name: 'Delete' }).click();
  await expect(app.locator('[data-fact="picked"]')).toHaveText('delete');
});

test('a badge and a request for attention show on the taskbar and never take the focus', async ({
  page,
}) => {
  await signInAsAdmin(page);
  const { app } = await openDemo(page);
  for (let i = 0; i < 3; i++) {
    await app.getByRole('button', { name: 'Count', exact: true }).click();
  }
  await app.getByRole('button', { name: 'Show as badge' }).click();
  await expect(taskbarEntry(page)).toHaveAccessibleName(/3 new/);

  await app.getByRole('checkbox', { name: 'Ask for attention when I leave the window' }).check();
  await openFromDesktop(page, 'Storage');
  await expect(appWindow(page, 'Storage')).toBeVisible();
  await expect(taskbarEntry(page)).toHaveAccessibleName(/needs attention/);
  await expect(taskbarEntry(page)).toHaveAttribute('data-attention', 'true');
  await expect(
    page.getByRole('navigation', { name: 'Taskbar' }).getByRole('button', { name: /^Storage/ }),
  ).toHaveAccessibleName(/active/);
  expect(await violations(page)).toEqual([]);

  await taskbarEntry(page).click();
  await expect(taskbarEntry(page)).not.toHaveAttribute('data-attention');
  await expect(taskbarEntry(page)).toHaveAccessibleName(/active, 3 new/);

  await appWindow(page, 'SDK demo').getByRole('button', { name: 'Close' }).click();
  await expect(appWindow(page, 'SDK demo')).toBeHidden();
  await openDemo(page);
  await expect(taskbarEntry(page)).not.toHaveAccessibleName(/new/);
});

test('unsaved changes keep a window, and an app that does not answer can be closed anyway', async ({
  page,
}) => {
  await signInAsAdmin(page);
  const { window, app } = await openDemo(page);
  const keep = app.getByRole('checkbox', { name: 'Keep the window open when it is closed' });
  const dirty = app.getByRole('checkbox', { name: 'Unsaved changes' });

  await test.step('a window marked clean closes without asking the app', async () => {
    await keep.check();
    await dirty.check();
    await window.getByRole('button', { name: 'Close' }).click();
    await page.waitForTimeout(300);
    await expect(window).toBeVisible();
    await dirty.uncheck();
    await window.getByRole('button', { name: 'Close' }).click();
    await expect(window).toBeHidden();
  });

  await test.step('an app that does not answer gets the question, and Wait keeps it', async () => {
    const again = await openDemo(page);
    await again.app.getByRole('checkbox', { name: 'Do not answer when the window closes' }).check();
    await again.window.getByRole('button', { name: 'Close' }).click();
    const dialog = page.getByRole('alertdialog', { name: 'SDK demo is not responding' });
    await expect(dialog).toBeVisible({ timeout: 10_000 });
    expect(await violations(page)).toEqual([]);
    await dialog.getByRole('button', { name: 'Wait' }).click();
    await expect(dialog).toBeHidden();
    await expect(again.window).toBeVisible();

    await again.window.getByRole('button', { name: 'Close' }).click();
    await dialog.getByRole('button', { name: 'Close anyway' }).click({ timeout: 10_000 });
    await expect(again.window).toBeHidden();
  });
});

test('notification buttons reach the open app, or start it with the activation', async ({
  page,
}) => {
  await signInAsAdmin(page);
  await page.request.delete('/api/notifications', { headers: { 'X-Pithyx-Request': '1' } });
  const { window, app } = await openDemo(page);
  await app.getByRole('button', { name: 'Count', exact: true }).click();
  await app.getByRole('button', { name: 'Notify with buttons' }).click();

  const toast = page
    .getByRole('region', { name: 'New notifications' })
    .getByRole('article')
    .filter({ hasText: 'Hello from the SDK demo' });
  await expect(toast.getByRole('button', { name: 'Open' })).toBeVisible();
  expect(await violations(page)).toEqual([]);
  await toast.getByRole('button', { name: 'Reply' }).click();
  await expect(toast).toBeHidden();
  await expect(app.locator('[data-fact="activation"]')).toHaveText('reply {"count":1}');
  await expect(app.getByRole('list', { name: 'Events' })).toContainText('notificationAction');

  await window.getByRole('button', { name: 'Close' }).click();
  await expect(window).toBeHidden();
  await page
    .getByRole('navigation', { name: 'Taskbar' })
    .getByRole('button', { name: /^Notifications/ })
    .click();
  const panel = page.getByRole('dialog', { name: 'Notifications' });
  await panel.getByRole('button', { name: 'Later' }).click();
  const reopened = page.frameLocator(FRAME);
  await expect(reopened.locator('[data-fact="activation"]')).toHaveText('later {"count":1}');
});

test.describe('clipboard and new tabs', () => {
  test.use({ permissions: ['clipboard-read', 'clipboard-write'] });

  test('an app copies text and opens a tab without a way back to the desktop', async ({ page }) => {
    await signInAsAdmin(page);
    const { app } = await openDemo(page);
    await app.getByRole('button', { name: 'Copy text' }).click();
    await expect
      .poll(() => page.evaluate(() => navigator.clipboard.readText()))
      .toBe('Counter of the SDK demo: 0');

    const popup = page.waitForEvent('popup');
    await app.getByRole('button', { name: 'Open the box in a tab' }).click();
    const tab = await popup;
    await tab.waitForLoadState();
    expect(new URL(tab.url()).pathname).toBe('/');
    expect(await tab.evaluate(() => window.opener === null)).toBe(true);
    await tab.close();
  });
});

test('every app log entry also reaches the browser console', async ({ page }) => {
  const messages: string[] = [];
  page.on('console', (message) => {
    messages.push(`${message.type()} ${message.text()}`);
  });
  await signInAsAdmin(page);
  const { app } = await openDemo(page);
  await app.getByRole('button', { name: 'Write log entries' }).click();
  await expect
    .poll(() =>
      messages.filter((message) => message.includes('demo ') && message.includes('entry')),
    )
    .toEqual(
      expect.arrayContaining([
        expect.stringMatching(/^debug demo debug entry/),
        expect.stringMatching(/^info demo info entry/),
        expect.stringMatching(/^warning demo warn entry/),
        expect.stringMatching(/^error demo error entry/),
      ]),
    );
});

test.describe('phone', () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });

  test('Close all in the app switcher asks the apps and keeps a window with unsaved work', async ({
    page,
  }) => {
    await signInAsAdmin(page);
    await page.getByRole('option', { name: 'Storage, shortcut' }).tap();
    const { app } = await openDemo(page);
    await app.getByRole('checkbox', { name: 'Keep the window open when it is closed' }).check();

    await page
      .getByRole('navigation', { name: 'Taskbar' })
      .getByRole('button', { name: /Switch apps/ })
      .tap();
    const switcher = page.getByRole('dialog', { name: 'Open apps' });
    await expect(switcher).toBeVisible();
    await switcher.getByRole('button', { name: 'Close all' }).tap();
    await expect(page.locator(FRAME)).toHaveCount(1);
    await expect(appWindow(page, 'Storage')).toHaveCount(0);
    expect(await violations(page)).toEqual([]);
  });
});
