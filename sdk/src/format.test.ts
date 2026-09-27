import { describe, expect, it } from 'vitest';
import { createFormatters } from './format';

describe('formatters', () => {
  it('follow a change of language and time zone although they reuse Intl objects', () => {
    let current = { locale: 'en-US', timeZone: 'UTC' };
    const format = createFormatters(() => current);
    const noon = '2026-09-27T12:00:00Z';
    expect(format.time(noon)).toBe('12:00 PM');
    expect(format.bytes(1500)).toBe('1.5 kB');
    current = { locale: 'de-DE', timeZone: 'Europe/Berlin' };
    expect(format.time(noon)).toBe('14:00');
    expect(format.bytes(1500)).toBe('1,5 kB');
    current = { locale: 'en-US', timeZone: 'UTC' };
    expect(format.time(noon)).toBe('12:00 PM');
  });
});
