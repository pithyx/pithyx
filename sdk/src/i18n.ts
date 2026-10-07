/**
 * `@pithyx/sdk/i18n`: an optional helper for apps that keep their texts as ICU MessageFormat JSON, the format the
 * desktop uses. Apps may use any other i18n library instead; the SDK only hands them the locale.
 *
 * ```ts
 * import { createI18n } from '@pithyx/sdk/i18n';
 * import en from './i18n/en.json';
 * import de from './i18n/de.json';
 *
 * const i18n = createI18n(app, { en, de });
 * i18n.t('files.count', { count: 3 }); // "{count, plural, one {# file} other {# files}}"
 * ```
 *
 * @packageDocumentation
 */
import { IntlMessageFormat } from 'intl-messageformat';
import type { PithyxApp } from './app';

/** Texts of one language: nested objects of ICU messages. */
export interface Messages {
  [key: string]: string | Messages;
}

/** Texts in the app's current language, from {@link createI18n}. */
export interface I18n {
  /** The language whose texts are in use: the user's, or its base language, or `en`. */
  readonly language: string;
  /** The text for a dotted key with its parameters; the key itself when no language has it. */
  t(key: string, params?: Record<string, unknown>): string;
}

function lookup(messages: Messages | undefined, key: string): string | undefined {
  let node: string | Messages | undefined = messages;
  for (const part of key.split('.')) {
    if (typeof node !== 'object') {
      return undefined;
    }
    node = node[part];
  }
  return typeof node === 'string' ? node : undefined;
}

/** Picks the user's language, then its base language (`de` for `de-AT`), then `en`. */
export function pickLanguage(locale: string, available: readonly string[]): string {
  const lower = locale.toLowerCase();
  const exact = available.find((language) => language.toLowerCase() === lower);
  const base = available.find((language) => language.toLowerCase() === lower.split('-')[0]);
  return exact ?? base ?? 'en';
}

/**
 * Texts for the app's current locale, following `localeChange`. `en` is the fallback for keys a language lacks.
 * Messages are compiled once per language and key.
 */
export function createI18n(
  app: Pick<PithyxApp, 'context'>,
  messages: { en: Messages } & Partial<Record<string, Messages>>,
): I18n {
  const compiled = new Map<string, IntlMessageFormat>();
  const language = () => pickLanguage(app.context.locale, Object.keys(messages));
  return {
    get language() {
      return language();
    },
    t(key, params) {
      const current = language();
      const cacheKey = `${app.context.locale}\u0000${key}`;
      let format = compiled.get(cacheKey);
      if (format === undefined) {
        const own = lookup(messages[current], key);
        const text = own ?? lookup(messages.en, key);
        if (text === undefined) {
          return key;
        }
        format = new IntlMessageFormat(text, own === undefined ? 'en' : app.context.locale);
        compiled.set(cacheKey, format);
      }
      return String(format.format(params as Record<string, string>));
    },
  };
}
