import de from '../../../i18n/de/errors.json';
import en from '../../../i18n/en/errors.json';
import type { Locale } from './i18n.ts';

export interface ErrorCode {
  code: string;
  message: string;
}

interface Tree {
  [key: string]: string | Tree;
}

function flatten(tree: Tree, prefix: string[] = []): ErrorCode[] {
  return Object.entries(tree).flatMap(([key, value]) =>
    typeof value === 'string'
      ? [{ code: [...prefix, key].join('.'), message: value }]
      : flatten(value, [...prefix, key]),
  );
}

/** Every error code with its message, from the product's translation files (i18n/<locale>/errors.json). */
export function errorCodes(locale: Locale): ErrorCode[] {
  return flatten(locale === 'de' ? de : en).sort((a, b) => a.code.localeCompare(b.code));
}

export function errorAreas(locale: Locale): [string, ErrorCode[]][] {
  const areas = new Map<string, ErrorCode[]>();
  for (const error of errorCodes(locale)) {
    const area = error.code.split('.')[0] ?? error.code;
    areas.set(area, [...(areas.get(area) ?? []), error]);
  }
  return [...areas];
}

export function errorCodesMarkdown(locale: Locale): string {
  return errorAreas(locale)
    .map(
      ([area, codes]) =>
        `## ${area}\n\n${codes.map((error) => `- \`${error.code}\`: ${error.message.replace(/\s+/g, ' ')}`).join('\n')}\n`,
    )
    .join('\n');
}
