// The daemon renders the same messages with IcuMessageFormatter (IcuGoldenTests.cs). Both must match
// tools/i18n/icu-golden.json exactly. The desktop side goes the production way: compiled at build time, formatted by
// icuFormat.
import i18next from 'i18next';
import { describe, expect, it } from 'vitest';
import golden from '../../tools/i18n/icu-golden.json';
import enErrors from '../../i18n/en/errors.json';
import { compileMessages } from '../scripts/compile-messages';
import { icuFormat } from './icuFormat';

interface GoldenEntry {
  params: Record<string, string | number | boolean>;
  expected: string;
}

const errors = golden.errors as Record<string, GoldenEntry>;
const cases = golden.cases as (GoldenEntry & { message: string })[];

const i18n = i18next.createInstance();
await i18n.use(icuFormat).init({
  lng: 'en',
  resources: {
    en: compileMessages(
      {
        errors: enErrors,
        golden: Object.fromEntries(
          cases.map((entry, index) => [`case${String(index)}`, entry.message]),
        ),
      },
      'en',
    ) as Record<string, object>,
  },
  interpolation: { escapeValue: false },
});

// Keys come from the golden file at runtime, so the typed key union of t() does not apply here.
const translate = i18n.t.bind(i18n) as unknown as (
  key: string,
  options: Record<string, unknown>,
) => string;

function flatten(value: object, prefix = ''): string[] {
  return Object.entries(value).flatMap(([key, child]) => {
    const path = prefix === '' ? key : `${prefix}.${key}`;
    return typeof child === 'object' && child !== null ? flatten(child as object, path) : [path];
  });
}

describe('ICU golden file', () => {
  it('has an entry for every English error message and no stale ones', () => {
    expect(Object.keys(errors).sort()).toEqual(flatten(enErrors).sort());
  });

  it.each(Object.entries(errors))('renders errors.%s', (code, entry) => {
    expect(translate(code, { ns: 'errors', ...entry.params })).toBe(entry.expected);
  });

  it.each(cases.map((entry, index) => [index, entry] as const))(
    'renders case %i',
    (index, entry) => {
      expect(translate(`case${String(index)}`, { ns: 'golden', ...entry.params })).toBe(
        entry.expected,
      );
    },
  );
});
