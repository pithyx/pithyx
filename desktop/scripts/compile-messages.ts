// Compiles ICU messages at build time, so the browser never needs the ICU parser (about 8 KB gzip on the sign-in
// path). vite.config.ts runs this on every translation file imported with `?compiled`; src/icuFormat.ts formats the
// result.
import { isLiteralElement, parse } from '@formatjs/icu-messageformat-parser';
import { COMPILED } from '../src/icuFormat.ts';

/**
 * Replaces every message with ICU syntax by COMPILED plus its syntax tree as JSON. A message that is plain text after
 * parsing (quotes resolved) stays a plain string.
 */
export function compileMessages(texts: unknown, locale: string): unknown {
  if (typeof texts === 'string') {
    // Same options as intl-messageformat's own parse call; tags stay text, react-i18next's <Trans> replaces them.
    const ast = parse(texts, { ignoreTag: true, locale: new Intl.Locale(locale) });
    if (ast.length === 0) {
      return '';
    }
    const [first] = ast;
    if (ast.length === 1 && first !== undefined && isLiteralElement(first)) {
      return first.value;
    }
    return COMPILED + JSON.stringify(ast);
  }
  if (typeof texts === 'object' && texts !== null) {
    return Object.fromEntries(
      Object.entries(texts).map(([key, value]) => [key, compileMessages(value, locale)]),
    );
  }
  return texts;
}
