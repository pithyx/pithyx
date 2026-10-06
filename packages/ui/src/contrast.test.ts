import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

type Theme = 'light' | 'dark';
interface Rgba {
  r: number;
  g: number;
  b: number;
  a: number;
}

const tokensCss = readFileSync(new URL('../tokens.css', import.meta.url), 'utf8');

/** Custom properties per selector, ignoring @media overrides (those only swap in the solid fallbacks). */
function declarations(css: string): Map<string, Map<string, string>> {
  const withoutComments = css.replace(/\/\*[\s\S]*?\*\//g, '');
  const withoutMedia = withoutComments.replace(/@media[^{]*\{(?:[^{}]*\{[^{}]*\})*[^{}]*\}/g, '');
  const blocks = new Map<string, Map<string, string>>();
  for (const [, selector = '', body = ''] of withoutMedia.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    for (const name of selector.split(',').map((part) => part.trim())) {
      const block = blocks.get(name) ?? new Map<string, string>();
      for (const [, property = '', value = ''] of body.matchAll(/(--[\w-]+)\s*:\s*([^;]+);/g)) {
        block.set(property, value.trim());
      }
      blocks.set(name, block);
    }
  }
  return blocks;
}

const tokenBlocks = declarations(tokensCss);
const accents = [...tokenBlocks.keys()]
  .map((selector) => /^\[data-accent='([\w-]+)'\]$/.exec(selector)?.[1])
  .filter((name) => name !== undefined);

function variables(accent: string): Map<string, string> {
  return new Map([
    ...(tokenBlocks.get(':root') ?? []),
    ...(tokenBlocks.get(`[data-accent='${accent}']`) ?? []),
  ]);
}

function splitArguments(text: string): string[] {
  const parts: string[] = [];
  let depth = 0;
  let current = '';
  for (const char of text) {
    if (char === ',' && depth === 0) {
      parts.push(current.trim());
      current = '';
      continue;
    }
    depth += char === '(' ? 1 : char === ')' ? -1 : 0;
    current += char;
  }
  parts.push(current.trim());
  return parts;
}

function resolve(value: string, theme: Theme, vars: Map<string, string>): Rgba {
  const variable = /^var\((--[\w-]+)\)$/.exec(value);
  if (variable?.[1] !== undefined) {
    const next = vars.get(variable[1]);
    if (next === undefined) {
      throw new Error(`Unknown variable ${variable[1]}`);
    }
    return resolve(next, theme, vars);
  }
  const lightDark = /^light-dark\((.*)\)$/s.exec(value);
  if (lightDark?.[1] !== undefined) {
    const [light = '', dark = ''] = splitArguments(lightDark[1]);
    return resolve(theme === 'light' ? light : dark, theme, vars);
  }
  const hex = /^#([0-9a-f]{6})$/i.exec(value);
  if (hex?.[1] !== undefined) {
    const n = Number.parseInt(hex[1], 16);
    return { r: (n >> 16) & 255, g: (n >> 8) & 255, b: n & 255, a: 1 };
  }
  const rgb = /^rgb\((\d+) (\d+) (\d+)(?: \/ (\d+)%)?\)$/.exec(value);
  if (rgb !== null) {
    return {
      r: Number(rgb[1]),
      g: Number(rgb[2]),
      b: Number(rgb[3]),
      a: rgb[4] === undefined ? 1 : Number(rgb[4]) / 100,
    };
  }
  throw new Error(`Cannot evaluate color ${value}`);
}

function over(top: Rgba, bottom: Rgba): Rgba {
  const mix = (a: number, b: number) => a * top.a + b * (1 - top.a);
  return { r: mix(top.r, bottom.r), g: mix(top.g, bottom.g), b: mix(top.b, bottom.b), a: 1 };
}

function luminance({ r, g, b }: Rgba): number {
  const channel = (value: number) => {
    const c = value / 255;
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
}

function contrast(a: Rgba, b: Rgba): number {
  const [high, low] = [luminance(a), luminance(b)].sort((x, y) => y - x) as [number, number];
  return (high + 0.05) / (low + 0.05);
}

const WHITE: Rgba = { r: 255, g: 255, b: 255, a: 1 };
const BLACK: Rgba = { r: 0, g: 0, b: 0, a: 1 };

// [foreground, background, minimum ratio]. Backgrounds with alpha are composited over surface-1 unless the
// background is given as "a over b".
const TEXT = 4.5;
const GRAPHIC = 3;
const pairs: [string, string, number][] = [
  ...[
    '--color-surface-1',
    '--color-surface-2',
    '--color-surface-3',
    '--color-surface-hover',
    '--color-bg',
  ].flatMap((surface): [string, string, number][] => [
    ['--color-text', surface, TEXT],
    ['--color-text-muted', surface, TEXT],
  ]),
  ['--color-text', '--color-surface-pressed', TEXT],
  ['--color-text', '--color-accent-subtle', TEXT],
  ['--color-text', '--color-selection', TEXT],
  ['--color-text-muted', '--color-accent-subtle', TEXT],
  ['--color-text', '--color-taskbar-solid', TEXT],
  ['--color-text-muted', '--color-taskbar-solid', TEXT],
  ['--color-text', '--color-menu-solid', TEXT],
  ['--color-accent-contrast', '--color-accent', TEXT],
  ['--color-accent-contrast', '--color-accent-hover', TEXT],
  ['--color-accent-contrast', '--color-accent-pressed', TEXT],
  ['--color-accent', '--color-surface-1', TEXT],
  ['--color-accent', '--color-surface-2', TEXT],
  ['--color-accent', '--color-surface-3', TEXT],
  ['--color-accent', '--color-accent-subtle', TEXT],
  ['--color-focus', '--color-surface-1', GRAPHIC],
  ['--color-focus', '--color-surface-2', GRAPHIC],
  ['--color-focus', '--color-bg', GRAPHIC],
  ['--color-border-strong', '--color-surface-1', GRAPHIC],
  ['--color-border-strong', '--color-surface-3', GRAPHIC],
  ['--color-success', '--color-surface-1', TEXT],
  ['--color-success', '--color-success-surface', TEXT],
  ['--color-warning', '--color-surface-1', TEXT],
  ['--color-warning', '--color-warning-surface', TEXT],
  ['--color-danger', '--color-surface-1', TEXT],
  ['--color-danger', '--color-danger-surface', TEXT],
  ['--color-danger-contrast', '--color-danger', TEXT],
  ['--color-text', '--color-danger-surface', TEXT],
  ['--color-note-text', '--color-note', TEXT],
  ['--color-note-text', '--color-note-bar', TEXT],
  ['--color-highlight-contrast', '--color-highlight', TEXT],
];

describe.each(accents)('accent %s', (accent) => {
  const vars = variables(accent);
  describe.each(['light', 'dark'] as const)('%s theme', (theme) => {
    const surface = resolve('var(--color-surface-1)', theme, vars);
    it.each(pairs)('%s on %s', (foreground, background, minimum) => {
      const back = over(resolve(`var(${background})`, theme, vars), surface);
      const front = over(resolve(`var(${foreground})`, theme, vars), back);
      expect(contrast(front, back)).toBeGreaterThanOrEqual(minimum);
    });

    // Desktop labels sit on user wallpapers; their backdrop must work over both extremes.
    it.each([
      ['white', WHITE],
      ['black', BLACK],
    ] as const)('desktop label over a %s wallpaper', (_, wallpaper) => {
      const back = over(resolve('var(--color-desktop-label-bg)', theme, vars), wallpaper);
      const front = resolve('var(--color-desktop-label)', theme, vars);
      expect(contrast(front, back)).toBeGreaterThanOrEqual(TEXT);
    });
  });
});

it('finds every accent preset', () => {
  expect(accents).toEqual(['petrol', 'ocean', 'indigo', 'plum', 'moss', 'graphite']);
});
