/**
 * The desktop hands its look to apps as CSS custom properties. Only the `--vyxis-*` names below are public; the
 * desktop maps its internal tokens onto them, so internal renames never break an app. The SDK sets them on the
 * app document's root element and sets `color-scheme`, so plain CSS like `color: var(--vyxis-color-text)` follows
 * light, dark and the user's accent color.
 */

/** Public token names. New names are a minor change; removing or renaming one is a major change. */
export const themeTokenNames = [
  '--vyxis-color-background',
  '--vyxis-color-surface',
  '--vyxis-color-text',
  '--vyxis-color-text-muted',
  '--vyxis-color-border',
  '--vyxis-color-accent',
  '--vyxis-color-accent-text',
  '--vyxis-color-focus',
  '--vyxis-color-success',
  '--vyxis-color-warning',
  '--vyxis-color-danger',
  '--vyxis-font-ui',
  '--vyxis-font-mono',
  '--vyxis-font-size',
  '--vyxis-radius',
  '--vyxis-space',
  '--vyxis-control-height',
] as const;

export type ThemeTokenName = (typeof themeTokenNames)[number];

export interface Theme {
  scheme: 'light' | 'dark';
  /** Every public token with its current value. */
  tokens: Record<ThemeTokenName, string>;
}

/** Applies a theme to a document (the SDK does this on connect and on every `themeChange`). */
export function applyTheme(theme: Theme, root: HTMLElement): void {
  root.style.colorScheme = theme.scheme;
  root.dataset.vyxisTheme = theme.scheme;
  for (const name of themeTokenNames) {
    const value = theme.tokens[name];
    // A value is set as a custom property only; nothing from the host can inject other CSS.
    if (typeof value === 'string' && !/[;{}<>]/.test(value)) {
      root.style.setProperty(name, value);
    }
  }
}
