import { IntlMessageFormat } from 'intl-messageformat';
import { describe, expect, it } from 'vitest';
import { errorCodes } from '@pithyx/api-client';
import { sdkErrorCodes } from '@pithyx/sdk';
import { coreApps } from './apps/registry';
import { detectLanguage, languages } from './i18n';
import { desktopNamespaces, signInNamespaces } from './i18nNamespaces';

const files = import.meta.glob<object>('../../i18n/*/*.json', { eager: true, import: 'default' });

function textsOf(language: string): Record<string, object> {
  return Object.fromEntries(
    Object.entries(files)
      .filter(([path]) => path.startsWith(`../../i18n/${language}/`))
      .map(([path, texts]) => [
        path.replace(`../../i18n/${language}/`, '').replace('.json', ''),
        texts,
      ]),
  );
}

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
  const en = flatten(textsOf('en'));
  const de = flatten(textsOf('de'));

  it('have the same keys in en and de', () => {
    expect([...de.keys()].sort()).toEqual([...en.keys()].sort());
  });

  it('have the same files in every language, with the sign-in and desktop namespaces among them', () => {
    const namespaces = Object.keys(textsOf('en')).sort();
    for (const language of languages) {
      expect(Object.keys(textsOf(language)).sort(), language).toEqual(namespaces);
    }
    expect(namespaces).toEqual(expect.arrayContaining([...signInNamespaces, ...desktopNamespaces]));
  });

  it('explain every SDK transport code in en and de', () => {
    for (const code of sdkErrorCodes) {
      expect(en.get(`errors.${code}`), code).toBeDefined();
      expect(de.get(`errors.${code}`), code).toBeDefined();
    }
  });

  it('name every core app', () => {
    for (const app of coreApps) {
      expect(en.has(`desktop.apps.${app.id}`), app.id).toBe(true);
    }
  });

  it('have a text for every error code', () => {
    for (const code of errorCodes) {
      expect(en.has(`errors.${code}`), `en errors.${code}`).toBe(true);
      expect(de.has(`errors.${code}`), `de errors.${code}`).toBe(true);
    }
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
