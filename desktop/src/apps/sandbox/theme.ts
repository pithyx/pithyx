import type { Theme, ThemeTokenName } from '@pithyx/sdk';
import { onAppearanceChange } from '../../theme/appearance';
import { onThemeChoice } from '../../theme/preference';

// The public tokens apps get, from the desktop's own. Internal names may change; these may not.
const SOURCES: Record<ThemeTokenName, string> = {
  '--pithyx-color-background': '--color-bg',
  '--pithyx-color-surface': '--color-surface-1',
  '--pithyx-color-surface-raised': '--color-menu-solid',
  '--pithyx-color-hover': '--color-surface-hover',
  '--pithyx-color-selected': '--color-selection',
  '--pithyx-color-text': '--color-text',
  '--pithyx-color-text-muted': '--color-text-muted',
  '--pithyx-color-border': '--color-border',
  '--pithyx-color-accent': '--color-accent',
  '--pithyx-color-accent-text': '--color-accent-contrast',
  '--pithyx-color-focus': '--color-focus',
  '--pithyx-color-success': '--color-success',
  '--pithyx-color-warning': '--color-warning',
  '--pithyx-color-danger': '--color-danger',
  '--pithyx-font-ui': '--font-sans',
  '--pithyx-font-mono': '--font-mono',
  '--pithyx-font-size': '--font-size-md',
  '--pithyx-radius': '--radius-md',
  '--pithyx-space': '--space-2',
  '--pithyx-control-height': '--control-height',
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
  // The desktop has one density so far; a density setting changes the spacing tokens and this value together.
  return { scheme: scheme(), density: 'comfortable', tokens };
}

/** Calls back when the color scheme changes (the system's, or the user's own choice) or the accent color. */
export function onThemeChange(callback: () => void): () => void {
  const query = matchMedia(dark);
  query.addEventListener('change', callback);
  const stopChoice = onThemeChoice(callback);
  const stopAppearance = onAppearanceChange(callback);
  return () => {
    query.removeEventListener('change', callback);
    stopChoice();
    stopAppearance();
  };
}
