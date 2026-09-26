import { describe, expect, it } from 'vitest';
import { formatBytes } from './format';

describe('formatBytes', () => {
  it.each([
    [10_737_418_240, 'en', '10.7 GB'],
    [68_719_476_736, 'en', '68.7 GB'],
    [256_060_514_304, 'en', '256 GB'],
    [4_000_787_030_016, 'en', '4 TB'],
    [512, 'en', '512 byte'],
    // German separates number and unit with a no-break space.
    [10_737_418_240, 'de', '10,7\u00a0GB'],
  ])('formats %d in %s as %s', (bytes, locale, expected) => {
    expect(formatBytes(bytes, locale)).toBe(expected);
  });
});
