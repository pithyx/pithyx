/// <reference lib="dom" />
// The screen matrix shared by the screenshot renderer and the layout test.
import type { Page } from '@playwright/test';

// Sizes and input types match the Playwright devices in desktop/CLAUDE.md.
export const viewports = {
  phone: { width: 390, height: 844, touch: true },
  tablet: { width: 820, height: 1180, touch: true },
  laptop: { width: 1440, height: 900, touch: false },
  ultrawide: { width: 3440, height: 1440, touch: false },
} as const;
export const themes = ['light', 'dark'] as const;
export const languages = ['en', 'de'] as const;

export type Viewport = keyof typeof viewports;
export type Theme = (typeof themes)[number];
export type Language = (typeof languages)[number];

export function contextOptions(viewport: Viewport, theme: Theme) {
  const size = viewports[viewport];
  return {
    viewport: { width: size.width, height: size.height },
    hasTouch: size.touch,
    isMobile: viewport === 'phone',
    colorScheme: theme,
    // Entrance animations finish at once, so every run sees the same frame.
    reducedMotion: 'reduce',
    deviceScaleFactor: 1,
  } as const;
}

export function screenUrl(screen: string, theme: Theme, lang: Language): string {
  return `/design.html?${new URLSearchParams({ screen, theme, lang }).toString()}`;
}

/** Reads the screen ids from the gallery itself, so the list never drifts from the app. */
export async function screenIds(page: Page, baseUrl: string): Promise<string[]> {
  // Any single screen will do; the gallery itself would load dozens of iframes.
  await page.goto(`${baseUrl}${screenUrl('tokens', 'light', 'en')}`);
  return page.evaluate(async () => {
    const path = '/src/design/screens.tsx';
    const module = (await import(path)) as { screens: { id: string }[] };
    return module.screens.map((screen) => screen.id);
  });
}

/** Loads a screen and waits until fonts and the delayed loading hint of the welcome screen are shown. */
export async function openScreen(
  page: Page,
  baseUrl: string,
  screen: string,
  theme: Theme,
  lang: Language,
) {
  await page.goto(`${baseUrl}${screenUrl(screen, theme, lang)}`);
  await page.evaluate(() => document.fonts.ready.then(() => undefined));
  // Screens of real apps run a short script (open a dialog, fill it) and mark the page busy until it is done. The
  // script gives up on its own (3 s per step), so this only has to outlast it while the dev server loads the app.
  await page.waitForFunction(
    () => document.querySelector('[data-design-busy]') === null,
    undefined,
    {
      timeout: 30_000,
    },
  );
  await page.waitForTimeout(800);
}
