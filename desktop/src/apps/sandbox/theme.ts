import type { Theme, ThemeTokenName } from '@vyxis/sdk';

// The public tokens apps get, from the desktop's own. Internal names may change; these may not.
const SOURCES: Record<ThemeTokenName, string> = {
  '--vyxis-color-background': '--color-bg',
  '--vyxis-color-surface': '--color-surface-1',
  '--vyxis-color-text': '--color-text',
  '--vyxis-color-text-muted': '--color-text-muted',
  '--vyxis-color-border': '--color-border',
  '--vyxis-color-accent': '--color-accent',
  '--vyxis-color-accent-text': '--color-accent-contrast',
  '--vyxis-color-focus': '--color-focus',
  '--vyxis-color-success': '--color-success',
  '--vyxis-color-warning': '--color-warning',
  '--vyxis-color-danger': '--color-danger',
  '--vyxis-font-ui': '--font-sans',
  '--vyxis-font-mono': '--font-mono',
  '--vyxis-font-size': '--font-size-md',
  '--vyxis-radius': '--radius-md',
  '--vyxis-space': '--space-2',
  '--vyxis-control-height': '--control-height',
};

const dark = '(prefers-color-scheme: dark)';

function scheme(): Theme['scheme'] {
  const forced = document.documentElement.dataset.theme;
  if (forced === 'light' || forced === 'dark') {
    return forced;
  }
  return matchMedia(dark).matches ? 'dark' : 'light';
}

/**
 * The desktop's current theme as apps see it. Colors keep their light-dark() form where the desktop uses it; the app
 * resolves them with the color scheme the SDK sets.
 */
export function currentTheme(): Theme {
  const style = getComputedStyle(document.documentElement);
  const tokens = Object.fromEntries(
    Object.entries(SOURCES).map(([name, source]) => [name, style.getPropertyValue(source).trim()]),
  ) as Theme['tokens'];
  return { scheme: scheme(), tokens };
}

/** Calls back when the color scheme changes. */
export function onThemeChange(callback: () => void): () => void {
  const query = matchMedia(dark);
  query.addEventListener('change', callback);
  return () => {
    query.removeEventListener('change', callback);
  };
}
