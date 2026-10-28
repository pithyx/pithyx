import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { type Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { installProbe } from './probe';
import {
  appWindow,
  choosePersonalSetting,
  openFilesInShared,
  openFromDesktop,
  openFromStart,
  signInAsAdmin,
} from './session';

async function openDemo(page: Page) {
  // The demo has no desktop shortcut; new users only get the default ones.
  await openFromStart(page, 'SDK demo');
  const window = appWindow(page, 'SDK demo');
  await expect(window).toBeVisible();
  const app = page.frameLocator('iframe[src="/apps/com.example.sdk-probe/index.html"]');
  await expect(app.getByRole('heading', { name: 'SDK demo' })).toBeVisible();
  return { window, app };
}

test.beforeAll(async ({ baseURL }) => {
  await installProbe(baseURL ?? '');
});

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

  // The counter lives in the daemon's app storage, so it starts where earlier specs left it.
  const start = Number(await app.locator('output').textContent());
  const count = String(start + 2);
  await app.getByRole('button', { name: 'Count' }).click();
  await app.getByRole('button', { name: 'Count' }).click();
  await expect(app.locator('output')).toHaveText(count);

  await app.getByRole('button', { name: 'Set window title' }).click();
  await expect(appWindow(page, `SDK demo: ${count}`)).toBeVisible();
  await expect(
    page
      .getByRole('navigation', { name: 'Taskbar' })
      .getByRole('button', { name: new RegExp(`SDK demo: ${count}`) }),
  ).toBeVisible();

  await app.getByRole('button', { name: 'Send notification' }).click();
  await expect(page.getByText('Hello from the SDK demo').first()).toBeVisible();
  await expect(page.getByText(`The counter is at ${count}.`)).toBeVisible();

  await test.step('stored values come back in a new window', async () => {
    await appWindow(page, `SDK demo: ${count}`).getByRole('button', { name: 'Close' }).click();
    const again = await openDemo(page);
    await expect(again.app.locator('output')).toHaveText(count);
  });
});

test('lifecycle events reach the app: focus, blur, minimize, restore, theme and language', async ({
  page,
}) => {
  const { app } = await openDemo(page);
  const events = app.getByRole('list', { name: 'Events' });

  await openFromDesktop(page, 'Storage');
  await expect(appWindow(page, 'Storage')).toBeVisible();
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

  await choosePersonalSetting(page, 'Language', 'Deutsch');
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
  await expect(
    page.locator('iframe[src="/apps/com.example.sdk-probe/index.html"]'),
  ).toHaveAttribute('sandbox', 'allow-scripts allow-forms');

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

test('the probe opened directly in a tab runs sandboxed and cannot use the session', async ({
  page,
}) => {
  const response = await page.goto('/apps/com.example.sdk-probe/index.html');
  expect(response?.headers()['content-security-policy']).toMatch(
    /^sandbox allow-scripts allow-forms; default-src 'self';/,
  );
  expect(response?.headers()['x-content-type-options']).toBe('nosniff');
  // An opaque origin: no cookie and no answer from the API to read.
  expect(await page.evaluate(() => window.origin)).toBe('null');
  const status = await page.evaluate(() =>
    fetch('/api/auth/session').then(
      (answer) => answer.status,
      () => 'blocked',
    ),
  );
  expect(status).not.toBe(200);
});

test("an app's file action takes the selection in Files", async ({ page }) => {
  const folder = join(process.env.PITHYX_E2E_DATA ?? '', 'shares', 'shared', 'e2e-action');
  mkdirSync(join(folder, 'docs'), { recursive: true });
  writeFileSync(join(folder, 'bundle.zip'), 'zip');
  writeFileSync(join(folder, 'notes.txt'), 'text');
  await openFilesInShared(page);
  const files = page.getByRole('region', { name: /Files$/ });
  const grid = files.getByRole('grid', { name: 'Folder contents' });
  await grid.getByRole('row', { name: /^e2e-action/ }).dblclick();

  // A text file matches no action of the probe.
  await grid.getByRole('row', { name: /^notes\.txt/ }).click({ button: 'right' });
  await expect(page.getByRole('menu')).toBeVisible();
  await expect(page.getByRole('menuitem', { name: 'Inspect with the probe' })).toHaveCount(0);
  await page.keyboard.press('Escape');

  await grid.getByRole('row', { name: /^bundle\.zip/ }).click();
  await grid.getByRole('row', { name: /^docs/ }).click({ modifiers: ['ControlOrMeta'] });
  await grid.getByRole('row', { name: /^docs/ }).click({ button: 'right' });
  await page.getByRole('menuitem', { name: 'Inspect with the probe' }).click();

  const app = page.frameLocator('iframe[src="/apps/com.example.sdk-probe/index.html"]');
  const fact = app.locator('[data-fact="fileAction"]');
  await expect(fact).toHaveText(/^inspect e2e-action\//);
  await expect(fact).toContainText('bundle.zip');
  await expect(fact).toContainText('docs');
});
