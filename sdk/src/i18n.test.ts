import { describe, expect, it } from 'vitest';
import { createFormatters } from './format';
import { createI18n, pickLanguage } from './i18n';

describe('pickLanguage', () => {
  it('prefers the exact tag, then the base language, then English', () => {
    expect(pickLanguage('de-AT', ['en', 'de', 'de-AT'])).toBe('de-AT');
    expect(pickLanguage('de-AT', ['en', 'de'])).toBe('de');
    expect(pickLanguage('fr-FR', ['en', 'de'])).toBe('en');
  });
});

describe('createI18n', () => {
  const messages = {
    en: { files: { count: '{count, plural, one {# file} other {# files}}' }, only: 'English only' },
    de: { files: { count: '{count, plural, one {# Datei} other {# Dateien}}' } },
  };

  it('formats ICU messages in the current language and follows locale changes', () => {
    const app = { context: { locale: 'de-DE' } };
    const i18n = createI18n(app as never, messages);
    expect(i18n.t('files.count', { count: 1 })).toBe('1 Datei');
    expect(i18n.t('files.count', { count: 2000 })).toBe('2.000 Dateien');
    app.context.locale = 'en-US';
    expect(i18n.language).toBe('en');
    expect(i18n.t('files.count', { count: 2000 })).toBe('2,000 files');
  });

  it('falls back to English, then to the key', () => {
    const i18n = createI18n({ context: { locale: 'de' } } as never, messages);
    expect(i18n.t('only')).toBe('English only');
    expect(i18n.t('missing.key')).toBe('missing.key');
  });
});

describe('createFormatters', () => {
  it('uses the user locale and time zone at every call', () => {
    const current = { locale: 'de-DE', timeZone: 'Europe/Berlin' };
    const format = createFormatters(() => current);
    expect(format.time('2026-09-27T22:30:00Z')).toBe('00:30');
    expect(format.bytes(1_500_000)).toBe('1,5 MB');
    current.locale = 'en-US';
    current.timeZone = 'UTC';
    expect(format.time('2026-09-27T22:30:00Z')).toBe('10:30 PM');
    expect(format.relative('2026-09-27T22:25:00Z', new Date('2026-09-27T22:30:00Z'))).toBe(
      '5 minutes ago',
    );
    expect(format.list(['Anna', 'Ben', 'Chris'])).toBe('Anna, Ben, and Chris');
  });
});
