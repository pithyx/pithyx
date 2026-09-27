import { describe, expect, it } from 'vitest';
import { themeTokenNames } from './theme';

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
