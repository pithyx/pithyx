import { type Page } from '@playwright/test';
import { expect, test } from './fixtures';
import {
  appWindow,
  openAccountMenu,
  openFromDesktop,
  openFromStart,
  signInAsAdmin,
} from './session';

async function openDemo(page: Page) {
  // The demo has no desktop shortcut; new users only get the default ones.
  await openFromStart(page, 'SDK demo');
  const window = appWindow(page, 'SDK demo');
  await expect(window).toBeVisible();
  const app = page.frameLocator('iframe[src="/apps/demo/index.html"]');
  await expect(app.getByRole('heading', { name: 'SDK demo' })).toBeVisible();
  return { window, app };
}

test.beforeEach(async ({ page }) => {
  await signInAsAdmin(page);
});

test('a sandboxed app gets its context and uses the window, notification and storage calls', async ({
  page,
}) => {
  const { app } = await openDemo(page);
  await expect(app.locator('[data-fact="user"]')).toHaveText('Ada Admin');
  await expect(app.locator('[data-fact="locale"]')).toHaveText(/^en/);
  await expect(app.locator('[data-fact="sizeClass"]')).toHaveText('expanded');

  await app.getByRole('button', { name: 'Count' }).click();
  await app.getByRole('button', { name: 'Count' }).click();
  await expect(app.locator('output')).toHaveText('2');

  await app.getByRole('button', { name: 'Set window title' }).click();
  await expect(appWindow(page, 'SDK demo: 2')).toBeVisible();
  await expect(
    page.getByRole('navigation', { name: 'Taskbar' }).getByRole('button', { name: /SDK demo: 2/ }),
  ).toBeVisible();

  await app.getByRole('button', { name: 'Send notification' }).click();
  await expect(page.getByText('Hello from the SDK demo')).toBeVisible();
  await expect(page.getByText('The counter is at 2.')).toBeVisible();

  await test.step('stored values come back in a new window', async () => {
    await appWindow(page, 'SDK demo: 2').getByRole('button', { name: 'Close' }).click();
    const again = await openDemo(page);
    await expect(again.app.locator('output')).toHaveText('2');
  });
});

test('lifecycle events reach the app: focus, blur, minimize, restore, theme and language', async ({
  page,
}) => {
  const { app } = await openDemo(page);
  const events = app.getByRole('list', { name: 'Events' });

  await openFromDesktop(page, 'Disks');
  await expect(appWindow(page, 'Disks')).toBeVisible();
  await expect(events).toContainText('blur');
  await page
    .getByRole('navigation', { name: 'Taskbar' })
    .getByRole('button', { name: /^SDK demo/ })
    .click();
  await expect(events).toContainText('focus');
  await page
    .getByRole('navigation', { name: 'Taskbar' })
    .getByRole('button', { name: /^SDK demo/ })
    .click();
  await page
    .getByRole('navigation', { name: 'Taskbar' })
    .getByRole('button', { name: /^SDK demo/ })
    .click();
  // A minimized window is hidden, so the log is read once it is back.
  await expect(events).toContainText('minimize');
  await expect(events).toContainText('restore');

  await page.emulateMedia({ colorScheme: 'dark' });
  await expect(app.locator('[data-fact="theme"]')).toHaveText('dark');
  await expect(events).toContainText('themeChange');
  await expect(app.locator('html')).toHaveAttribute('data-pithyx-theme', 'dark');

  await openAccountMenu(page);
  await page.getByRole('menuitem', { name: 'Language' }).click();
  await page.getByRole('menuitemradio', { name: 'Deutsch' }).click();
  await expect(app.getByRole('heading', { name: 'SDK-Demo' })).toBeVisible();
  await expect(app.locator('[data-fact="locale"]')).toHaveText(/^de/);
});

test('an app can keep its window open when the user closes it', async ({ page }) => {
  const { window, app } = await openDemo(page);
  await app.getByRole('checkbox', { name: 'Keep the window open when it is closed' }).check();
  await window.getByRole('button', { name: 'Close' }).click();
  await page.waitForTimeout(300);
  await expect(window).toBeVisible();
  await app.getByRole('checkbox', { name: 'Keep the window open when it is closed' }).uncheck();
  await window.getByRole('button', { name: 'Close' }).click();
  await expect(window).toBeHidden();
});

test('the sandbox keeps the app away from cookies, the desktop page, browser storage and the session', async ({
  page,
}) => {
  const { app } = await openDemo(page);
  for (const probe of ['cookie', 'parent', 'storage', 'api']) {
    await expect(app.locator(`[data-probe="${probe}"]`), probe).toHaveText('blocked');
  }
  await expect(page.locator('iframe[src="/apps/demo/index.html"]')).toHaveAttribute(
    'sandbox',
    'allow-scripts allow-forms',
  );

  await test.step('a request the app posts to the desktop window instead of its port is ignored', async () => {
    // The demo posts a forged window.setTitle('forged') to window.parent at start; the title must not change.
    await expect(appWindow(page, 'forged')).toHaveCount(0);
    await expect(appWindow(page, 'SDK demo')).toBeVisible();
  });

  await test.step('a port offered by another frame does not reach the app', async () => {
    // A second frame on the desktop page posts its own connect message with a port into the app.
    const answered = await page.evaluate(async () => {
      const stranger = document.createElement('iframe');
      // Same origin as the desktop page, so it can find the app's frame and post into it.
      stranger.srcdoc = `<script>
        const target = parent.document.querySelector('iframe[title="SDK demo"]').contentWindow;
        const channel = new MessageChannel();
        channel.port1.onmessage = () => { parent.postMessage('stranger-heard', '*'); };
        target.postMessage({ type: 'pithyx:connect' }, '*', [channel.port2]);
      </script>`;
      let heard = false;
      const listener = (event: MessageEvent) => {
        if (event.data === 'stranger-heard') {
          heard = true;
        }
      };
      window.addEventListener('message', listener);
      document.body.append(stranger);
      await new Promise((resolve) => setTimeout(resolve, 800));
      window.removeEventListener('message', listener);
      stranger.remove();
      return heard;
    });
    expect(answered).toBe(false);
  });
});
