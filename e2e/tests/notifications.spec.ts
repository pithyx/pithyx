import AxeBuilder from '@axe-core/playwright';
import type { Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { installProbe } from './probe';
import { desktopReady, openFromStart, signInAsAdmin } from './session';

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];
const MESSAGE = 'Hello from the SDK demo';

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

function bell(page: Page) {
  return page
    .getByRole('navigation', { name: 'Taskbar' })
    .getByRole('button', { name: /^Notifications/ });
}

test.beforeAll(async ({ baseURL }) => {
  await installProbe(baseURL ?? '');
});

test.beforeEach(async ({ page }) => {
  await signInAsAdmin(page);
  // Every spec signs in as the same administrator; this one starts from an empty history.
  await page.request.delete('/api/notifications', { headers: { 'X-Pithyx-Request': '1' } });
});

test('an app notification is a toast with the app, stays in the panel after a reload and goes with Clear all', async ({
  page,
}) => {
  await openFromStart(page, 'SDK demo');
  const app = page.frameLocator('iframe[src="/apps/com.example.sdk-probe/index.html"]');
  await app.getByRole('button', { name: 'Send notification' }).click();

  const toasts = page.getByRole('region', { name: 'New notifications' });
  const toast = toasts.getByRole('article').filter({ hasText: MESSAGE });
  await expect(toast).toBeVisible();
  await expect(toast).toContainText('SDK demo');
  await expect(bell(page)).toHaveAccessibleName(/unread/);

  await test.step('the history survives a reload, without a toast for what was there', async () => {
    await page.reload();
    await desktopReady(page);
    await expect(toasts.getByText(MESSAGE)).toHaveCount(0);
    await bell(page).click();
    const panel = page.getByRole('dialog', { name: 'Notifications' });
    await expect(panel.getByText(MESSAGE)).toBeVisible();
    expect(await violations(page)).toEqual([]);
    await page.emulateMedia({ colorScheme: 'dark' });
    expect(await violations(page)).toEqual([]);
    await page.emulateMedia({ colorScheme: 'light' });
  });

  await test.step('opening the panel marked it read, and Clear all deletes it for good', async () => {
    const panel = page.getByRole('dialog', { name: 'Notifications' });
    await expect
      .poll(async () => {
        const list = (await (await page.request.get('/api/notifications')).json()) as {
          items: { readAt: string | null }[];
        };
        return list.items.every((item) => item.readAt !== null);
      })
      .toBe(true);
    await panel.getByRole('button', { name: 'Clear all' }).click();
    await expect(panel.getByText(MESSAGE)).toHaveCount(0);
    await page.keyboard.press('Escape');
    await expect(panel).toBeHidden();
    await page.reload();
    await desktopReady(page);
    await bell(page).click();
    await expect(page.getByRole('dialog', { name: 'Notifications' })).toBeVisible();
    await expect(
      page.getByRole('dialog', { name: 'Notifications' }).getByText(MESSAGE),
    ).toHaveCount(0);
  });
});

test('a dismissed notification leaves the panel and the keyboard reaches its buttons', async ({
  page,
}) => {
  await openFromStart(page, 'SDK demo');
  const app = page.frameLocator('iframe[src="/apps/com.example.sdk-probe/index.html"]');
  await app.getByRole('button', { name: 'Send notification' }).click();
  await expect(
    page.getByRole('region', { name: 'New notifications' }).getByText(MESSAGE),
  ).toBeVisible();

  await bell(page).focus();
  await page.keyboard.press('Enter');
  const panel = page.getByRole('dialog', { name: 'Notifications' });
  const card = panel.getByRole('article').filter({ hasText: MESSAGE });
  await expect(card).toBeVisible();
  await card.getByRole('button', { name: 'Dismiss' }).focus();
  await page.keyboard.press('Enter');

  await expect(card).toHaveCount(0);
  const list = (await (await page.request.get('/api/notifications')).json()) as {
    items: unknown[];
  };
  expect(list.items).toEqual([]);
});
