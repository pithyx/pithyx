import AxeBuilder from '@axe-core/playwright';
import type { Page } from '@playwright/test';
import { expect, test } from './fixtures';
import { appWindow, openFromDesktop, signInAsAdmin } from './session';

// WCAG 2.2 AA (desktop/CLAUDE.md). Contrast of the tokens is checked in desktop/src/theme/contrast.test.ts as well.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];

async function violations(page: Page) {
  const result = await new AxeBuilder({ page })
    .withTags(TAGS)
    // React Aria's own announcer keeps a message for a few seconds that points at the label of a menu that may
    // already be closed; it is transient and not part of the page.
    .exclude('[data-live-announcer]')
    .analyze();
  return result.violations.map(
    (violation) =>
      `${violation.id}: ${violation.help} (${violation.nodes.map((node) => node.target.join(' ')).join(', ')})`,
  );
}

test('the sign-in screen has no accessibility violations', async ({ browser }) => {
  // A fresh browser without a session always shows the sign-in card once the administrator exists.
  const context = await browser.newContext();
  const page = await context.newPage();
  await signInAsAdmin(page);
  await context.clearCookies();
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Welcome back' })).toBeVisible();
  expect(await violations(page)).toEqual([]);

  await page.getByRole('button', { name: 'Forgot password?' }).click();
  await expect(page.getByRole('heading', { name: 'Reset your password' })).toBeVisible();
  expect(await violations(page)).toEqual([]);
  await context.close();
});

test('the desktop with windows, menus and the start menu has no accessibility violations', async ({
  page,
}) => {
  await signInAsAdmin(page);
  await openFromDesktop(page, 'Disks');
  await page.getByRole('button', { name: 'Start' }).click();
  await page
    .getByRole('dialog', { name: 'Start menu' })
    .getByRole('row', { name: 'About Vyxis' })
    .click();
  await expect(appWindow(page, 'About Vyxis')).toBeVisible();
  await expect(appWindow(page, 'Disks').getByRole('row').nth(3)).toBeVisible();
  expect(await violations(page)).toEqual([]);

  await page.getByRole('button', { name: 'Start' }).click();
  await expect(page.getByRole('dialog', { name: 'Start menu' })).toBeVisible();
  expect(await violations(page)).toEqual([]);
  await page.keyboard.press('Escape');

  await appWindow(page, 'About Vyxis')
    .getByRole('button', { name: 'Window menu for About Vyxis' })
    .click();
  await expect(page.getByRole('menu', { name: 'Window menu for About Vyxis' })).toBeVisible();
  expect(await violations(page)).toEqual([]);
  await page.keyboard.press('Escape');

  await page.getByRole('button', { name: 'Account menu for Ada Admin' }).click();
  await expect(page.getByRole('menu', { name: 'Account menu for Ada Admin' })).toBeVisible();
  expect(await violations(page)).toEqual([]);
});

test.describe('phone', () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });

  test('the app switcher has no accessibility violations', async ({ page }) => {
    await signInAsAdmin(page);
    await page.getByRole('option', { name: 'Disks, shortcut' }).tap();
    await page
      .getByRole('navigation', { name: 'Taskbar' })
      .getByRole('button', { name: /Switch apps/ })
      .tap();
    await expect(page.getByRole('dialog', { name: 'Open apps' })).toBeVisible();
    expect(await violations(page)).toEqual([]);
  });
});
