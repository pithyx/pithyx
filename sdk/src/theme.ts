/**
 * The desktop hands its look to apps as CSS custom properties. Only the `--pithyx-*` names below are public; the
 * desktop maps its internal tokens onto them, so internal renames never break an app. The SDK sets them on the
 * app document's root element and sets `color-scheme`, so plain CSS like `color: var(--pithyx-color-text)` follows
 * light, dark, the user's accent color and density. `themeChange` tells the app when any of them changed.
 *
 * The SDK brings no components. An optional `@pithyx/ui` package with components in the desktop's look may follow;
 * apps may use it, any framework, or plain HTML and CSS.
 */

/**
 * Public token names. New names are a minor change; removing or renaming one is a major change. Every name says what
 * the value is for, never what it looks like: no raw colors such as petrol or amber.
 */
export const themeTokenNames = [
  '--pithyx-color-background',
  '--pithyx-color-surface',
  /** Menus, popovers and other layers above the surface. */
  '--pithyx-color-surface-raised',
  /** Background of a row or control under the pointer. */
  '--pithyx-color-hover',
  /** Background of selected rows and items, lighter than the full accent. */
  '--pithyx-color-selected',
  '--pithyx-color-text',
  '--pithyx-color-text-muted',
  '--pithyx-color-border',
  '--pithyx-color-accent',
  '--pithyx-color-accent-text',
  '--pithyx-color-focus',
  '--pithyx-color-success',
  '--pithyx-color-warning',
  '--pithyx-color-danger',
  '--pithyx-font-ui',
  '--pithyx-font-mono',
  '--pithyx-font-size',
  '--pithyx-radius',
  '--pithyx-space',
  '--pithyx-control-height',
] as const;

/** One of the public token names in {@link themeTokenNames}. */
export type ThemeTokenName = (typeof themeTokenNames)[number];

/** The desktop's look as an app gets it, in the context and with `themeChange`. */
export interface Theme {
  scheme: 'light' | 'dark';
  /** `compact` shrinks `--pithyx-space` and `--pithyx-control-height`; the values already reflect it. */
  density: 'comfortable' | 'compact';
  /** Every public token with its current value. */
  tokens: Record<ThemeTokenName, string>;
}

/** Applies a theme to a document (the SDK does this on connect and on every `themeChange`). */
export function applyTheme(theme: Theme, root: HTMLElement): void {
  root.style.colorScheme = theme.scheme;
  root.dataset.pithyxTheme = theme.scheme;
  root.dataset.pithyxDensity = theme.density;
  for (const name of themeTokenNames) {
    const value = theme.tokens[name];
    // A value is set as a custom property only; nothing from the host can inject other CSS.
    if (typeof value === 'string' && !/[;{}<>]/.test(value)) {
      root.style.setProperty(name, value);
    }
  }
}
