import deStrings from '../content/i18n/de.json';
import enStrings from '../content/i18n/en.json';

export type Locale = 'en' | 'de';
export type Key = keyof typeof enStrings;

export const locales: Locale[] = ['en', 'de'];

// The German file must have every English key; the type check fails otherwise.
const strings: Record<Locale, Record<Key, string>> = {
  en: enStrings,
  de: deStrings satisfies Record<Key, string>,
};

/** A site string with {name} placeholders filled in. Strings of the docs pages live in their Markdown instead. */
export function t(locale: Locale, key: Key, params: Record<string, string | number> = {}): string {
  return strings[locale][key].replace(/\{(\w+)\}/g, (match, name: string) =>
    String(params[name] ?? match),
  );
}

/** Locale of a Starlight lang attribute or entry id. */
export function toLocale(value: string | undefined): Locale {
  return value === 'de' || value?.startsWith('de/') || value?.startsWith('de-') ? 'de' : 'en';
}
