import { IntlMessageFormat } from 'intl-messageformat';
import { describe, expect, it } from 'vitest';
import { detectLanguage, resources } from './i18n';

function flatten(value: object, prefix = ''): Map<string, string> {
  const entries = new Map<string, string>();
  for (const [key, child] of Object.entries(value)) {
    const path = prefix === '' ? key : `${prefix}.${key}`;
    if (typeof child === 'string') {
      entries.set(path, child);
    } else if (typeof child === 'object' && child !== null) {
      for (const [nestedKey, text] of flatten(child as object, path)) {
        entries.set(nestedKey, text);
      }
    }
  }
  return entries;
}

describe('translations', () => {
  const en = flatten(resources.en);
  const de = flatten(resources.de);

  it('have the same keys in en and de', () => {
    expect([...de.keys()].sort()).toEqual([...en.keys()].sort());
  });

  it('are valid ICU messages', () => {
    for (const [language, messages] of [
      ['en', en],
      ['de', de],
    ] as const) {
      for (const text of messages.values()) {
        expect(() => new IntlMessageFormat(text, language)).not.toThrow();
      }
    }
  });
});

describe('detectLanguage', () => {
  it('picks the first supported browser language', () => {
    expect(detectLanguage(['fr-FR', 'de-AT', 'en-US'])).toBe('de');
  });

  it('falls back to English', () => {
    expect(detectLanguage(['fr-FR'])).toBe('en');
  });
});
