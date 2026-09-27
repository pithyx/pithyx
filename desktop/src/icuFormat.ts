import type { I18nFormatModule } from 'i18next';
import { IntlMessageFormat } from 'intl-messageformat';

/** Marks a message the build compiled into its ICU syntax tree (scripts/compile-messages.ts). */
export const COMPILED = '\u0001';

type Ast = IntlMessageFormat['getAst'] extends () => infer T ? T : never;

const formats = new Map<string, IntlMessageFormat>();

/** Formats compiled ICU messages for i18next, like i18next-icu without the parser. Plain texts pass through. */
export const icuFormat = {
  type: 'i18nFormat',
  init() {
    // Nothing to set up; the module only formats.
  },
  parse(
    res: string,
    options: Record<string, unknown>,
    lng: string,
    _ns: string,
    key: string,
  ): string {
    if (!res.startsWith(COMPILED)) {
      return res;
    }
    const cacheKey = `${lng}\u0000${res}`;
    let format = formats.get(cacheKey);
    if (format === undefined) {
      format = new IntlMessageFormat(JSON.parse(res.slice(COMPILED.length)) as Ast, lng);
      formats.set(cacheKey, format);
    }
    try {
      return String(format.format(options as Record<string, string | number>));
    } catch {
      // A missing or wrong parameter; the key shows which message, where i18next-icu showed its ICU source.
      return key;
    }
  },
  addLookupKeys(finalKeys: string[]): string[] {
    return finalKeys;
  },
} satisfies I18nFormatModule & Record<string, unknown>;
