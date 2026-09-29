/// <reference lib="dom" />
import { expect, test } from '@playwright/test';
import {
  contextOptions,
  languages,
  openScreen,
  screenIds,
  viewports,
  type Viewport,
} from './matrix.ts';

interface Overflow {
  scrollWidth: number;
  /** Shown elements whose visible part reaches past the right edge of the viewport. */
  outside: string[];
  /** Containers that scroll sideways, which cut content off just the same. */
  scrolling: string[];
}

// The document scroll width alone misses overflow inside scroll containers such as the sign-in layout, so the test
// also checks every visible element and every sideways scrolling container. Desktop items such as sticky notes stay
// below windows, so no point on a shown title bar may hit an element of the workspace outside every window.
for (const viewport of Object.keys(viewports) as Viewport[]) {
  for (const lang of languages) {
    test(`no screen overflows or covers a title bar on ${viewport} in ${lang}`, async ({
      browser,
      baseURL,
    }) => {
      test.setTimeout(180_000);
      const base = baseURL ?? '';
      const context = await browser.newContext(contextOptions(viewport, 'light'));
      let page = await context.newPage();
      for (const screen of await screenIds(page, base)) {
        // A fresh page per screen: dozens of screens with the development server's module graph in one page ran out
        // of browser resources.
        await page.close();
        page = await context.newPage();
        await openScreen(page, base, screen, 'light', lang);
        const overflow: Overflow = await page.evaluate(() => {
          const describe = (element: Element): string => {
            // CSS Module classes look like _name_hash_line; keep only the name.
            const name = /^_(\w+?)_/.exec(element.getAttribute('class') ?? '')?.[1];
            const text = element.textContent.trim().slice(0, 30);
            return `${element.tagName.toLowerCase()}${name === undefined ? '' : `.${name}`} "${text}"`;
          };
          const width = document.documentElement.clientWidth;
          const visibleRight = (element: Element): number => {
            let right = element.getBoundingClientRect().right;
            for (
              let parent = element.parentElement;
              parent !== null;
              parent = parent.parentElement
            ) {
              if (getComputedStyle(parent).overflowX !== 'visible') {
                right = Math.min(right, parent.getBoundingClientRect().right);
              }
            }
            return right;
          };
          const shown = [...document.querySelectorAll('body *')].filter(
            (element) =>
              element.closest('[aria-hidden="true"], [inert]') === null &&
              element.getClientRects().length > 0,
          );
          return {
            scrollWidth: document.documentElement.scrollWidth,
            outside: shown
              .filter((element) => visibleRight(element) > width + 0.5)
              .slice(0, 3)
              .map(describe),
            scrolling: shown
              .filter((element) => {
                const style = getComputedStyle(element);
                return (
                  (style.overflowX === 'auto' || style.overflowX === 'scroll') &&
                  element.scrollWidth > element.clientWidth + 1
                );
              })
              .slice(0, 3)
              .map(describe),
          };
        });
        const covered: string[] = await page.evaluate(() => {
          const found: string[] = [];
          for (const frame of document.querySelectorAll('section[data-medium]')) {
            const titlebar = frame.querySelector(':scope > header');
            const workspace = frame.closest('main');
            if (titlebar === null || workspace === null) {
              continue;
            }
            const rect = titlebar.getBoundingClientRect();
            for (let step = 1; step < 20; step++) {
              const hit = document.elementFromPoint(
                rect.left + (rect.width * step) / 20,
                rect.top + rect.height / 2,
              );
              if (
                hit !== null &&
                workspace.contains(hit) &&
                hit.closest('section[data-medium]') === null
              ) {
                found.push(`${titlebar.textContent.trim()} under ${hit.tagName.toLowerCase()}`);
                break;
              }
            }
          }
          return found;
        });
        expect.soft(covered, `${screen}: title bars covered by desktop items`).toEqual([]);
        expect
          .soft(overflow.scrollWidth, `${screen}: document scroll width`)
          .toBeLessThanOrEqual(viewports[viewport].width);
        expect.soft(overflow.outside, `${screen}: elements past the right edge`).toEqual([]);
        expect.soft(overflow.scrolling, `${screen}: containers that scroll sideways`).toEqual([]);
      }
      await context.close();
    });
  }
}
