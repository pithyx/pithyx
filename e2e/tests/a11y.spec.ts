import AxeBuilder from '@axe-core/playwright';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import type { Page } from '@playwright/test';
import { expect, test } from './fixtures';
import {
  appWindow,
  openAccountMenu,
  openFromDesktop,
  openFromStart,
  signInAsAdmin,
} from './session';

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
  await page.getByRole('button', { name: 'Pithyx menu' }).click();
  await page
    .getByRole('dialog', { name: 'Pithyx menu' })
    .getByRole('row', { name: 'About Pithyx' })
    .click();
  await expect(appWindow(page, 'About Pithyx')).toBeVisible();
  await expect(appWindow(page, 'Disks').getByRole('row').nth(3)).toBeVisible();
  expect(await violations(page)).toEqual([]);

  await page.getByRole('button', { name: 'Pithyx menu' }).click();
  await expect(page.getByRole('dialog', { name: 'Pithyx menu' })).toBeVisible();
  expect(await violations(page)).toEqual([]);
  await page.keyboard.press('Escape');

  await appWindow(page, 'About Pithyx')
    .getByRole('button', { name: 'Window menu for About Pithyx' })
    .click();
  await expect(page.getByRole('menu', { name: 'Window menu for About Pithyx' })).toBeVisible();
  expect(await violations(page)).toEqual([]);
  await page.keyboard.press('Escape');

  await openAccountMenu(page);
  await expect(page.getByRole('menu', { name: 'Account menu for Ada Admin' })).toBeVisible();
  expect(await violations(page)).toEqual([]);
});

test('the Files app, its menu and its recycle bin have no accessibility violations', async ({
  page,
}) => {
  const folder = join(process.env.PITHYX_E2E_DATA ?? '', 'shares', 'shared', 'a11y');
  mkdirSync(join(folder, 'Photos'), { recursive: true });
  writeFileSync(join(folder, 'notes.txt'), 'n');
  writeFileSync(join(folder, 'old.txt'), 'o');
  await signInAsAdmin(page);
  await openFromStart(page, 'Files');
  const files = page.getByRole('region', { name: /Files$/ });
  await files.getByRole('row', { name: /^a11y/ }).dblclick();
  await files.getByRole('row', { name: /^notes\.txt/ }).click();
  expect(await violations(page)).toEqual([]);

  await files.getByRole('row', { name: /^notes\.txt/ }).click({ button: 'right' });
  await expect(page.getByRole('menu', { name: 'Actions for 1 item' })).toBeVisible();
  expect(await violations(page)).toEqual([]);
  await page.keyboard.press('Escape');

  await files.getByRole('row', { name: /^old\.txt/ }).click();
  await page.keyboard.press('Delete');
  await files.getByRole('button', { name: 'Recycle bin of Shared' }).click();
  await expect(files.getByRole('option', { name: 'old.txt' })).toBeVisible();
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

  test('the Files app has no accessibility violations', async ({ page }) => {
    await signInAsAdmin(page);
    await openFromStart(page, 'Files');
    const files = page.getByRole('region', { name: /Files$/ });
    await expect(files.getByRole('grid', { name: 'Folder contents' })).toBeVisible();
    expect(await violations(page)).toEqual([]);
    await files.getByRole('button', { name: 'Shares and recycle bin' }).tap();
    await expect(files.getByRole('navigation', { name: 'Shares and recycle bin' })).toBeVisible();
    expect(await violations(page)).toEqual([]);
  });

  test('the account menu opens from the start menu by touch', async ({ page }) => {
    await signInAsAdmin(page);
    await page.getByRole('button', { name: 'Pithyx menu' }).tap();
    await page
      .getByRole('dialog', { name: 'Pithyx menu' })
      .getByRole('button', { name: 'Account menu for Ada Admin' })
      .tap();
    await expect(page.getByRole('menu', { name: 'Account menu for Ada Admin' })).toBeVisible();
    await expect(page.getByRole('menuitem', { name: 'Two-factor authentication' })).toBeVisible();
    expect(await violations(page)).toEqual([]);
  });
});
