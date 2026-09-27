import { IntlMessageFormat } from 'intl-messageformat';
import { describe, expect, it } from 'vitest';
import { errorCodes } from '@pithyx/api-client';
import { sdkErrorCodes } from '@pithyx/sdk';
import { coreApps } from './apps/registry';
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

  it('load every translation file', () => {
    const files = Object.keys(import.meta.glob('../../i18n/*/*.json')).map((path) =>
      path.replace('../../i18n/', '').replace('.json', ''),
    );
    const loaded = Object.entries(resources).flatMap(([language, namespaces]) =>
      Object.keys(namespaces).map((ns) => `${language}/${ns}`),
    );
    expect(loaded.sort()).toEqual(files.sort());
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
