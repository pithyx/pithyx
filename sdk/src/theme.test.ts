import { describe, expect, it } from 'vitest';
import { applyTheme, themeTokenNames, type Theme } from './theme';

// Raw color and palette words that must never name a public token: tokens say what a value is for.
const RAW_COLORS =
  /(petrol|amber|teal|blue|red|green|orange|yellow|purple|violet|plum|pink|gray|grey|black|white|neutral|\d{2,3}$)/;

describe('public theme tokens', () => {
  it('are semantic, prefixed and unique', () => {
    for (const name of themeTokenNames) {
      expect(name).toMatch(/^--pithyx-[a-z]+(-[a-z]+)*$/);
      expect(name).not.toMatch(RAW_COLORS);
    }
    expect(new Set(themeTokenNames).size).toBe(themeTokenNames.length);
  });
});

describe('applyTheme', () => {
  it('sets data-theme next to color-scheme, so compiled light-dark() follows the desktop', () => {
    const properties = new Map<string, string>();
    const root = {
      style: {
        colorScheme: '',
        setProperty: (name: string, value: string) => properties.set(name, value),
      },
      dataset: {} as Record<string, string>,
    };
    const theme = {
      scheme: 'dark',
      density: 'comfortable',
      tokens: { '--pithyx-color-text': '#e6f0f2', '--pithyx-color-accent': 'red;} body{color:red' },
    } as unknown as Theme;
    applyTheme(theme, root as unknown as HTMLElement);
    expect(root.style.colorScheme).toBe('dark');
    expect(root.dataset).toMatchObject({
      theme: 'dark',
      pithyxTheme: 'dark',
      pithyxDensity: 'comfortable',
    });
    expect(properties.get('--pithyx-color-text')).toBe('#e6f0f2');
    expect(properties.has('--pithyx-color-accent')).toBe(false);
  });
});
