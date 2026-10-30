/// <reference lib="dom" />
import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';
import { contextOptions, openScreen, screenIds, themes } from './matrix.ts';

// WCAG 2.2 AA (desktop/CLAUDE.md) on every screen of the gallery, in light and dark: the E2E suite checks the flows of
// a real box, this checks every pattern and app screen with its sample data, including the contrast of what the tokens
// end up as. Screens of the shell and the design tokens are part of it, so a token change shows up here.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];

for (const theme of themes) {
  test(`no screen has accessibility violations in the ${theme} theme`, async ({
    browser,
    baseURL,
  }) => {
    test.setTimeout(600_000);
    const base = baseURL ?? '';
    const context = await browser.newContext(contextOptions('laptop', theme));
    let page = await context.newPage();
    for (const screen of await screenIds(page, base)) {
      await page.close();
      page = await context.newPage();
      await openScreen(page, base, screen, theme, 'en');
      const result = await new AxeBuilder({ page })
        .withTags(TAGS)
        // React Aria's announcer keeps messages for a few seconds; it is transient and not part of the page.
        .exclude('[data-live-announcer]')
        .analyze();
      const found = result.violations.map(
        (violation) =>
          `${violation.id}: ${violation.help} (${violation.nodes
            .slice(0, 3)
            .map((node) => node.target.join(' '))
            .join(', ')})`,
      );
      expect.soft(found, `${screen} in ${theme}`).toEqual([]);
    }
    await context.close();
  });
}
