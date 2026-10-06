import de from '../../../i18n/de/ui.json';
import en from '../../../i18n/en/ui.json';

/**
 * The texts the components show or announce on their own. They live with every other translation, in
 * `i18n/<lang>/ui.json` of the repository, and the build bundles them, so the package needs no i18n library.
 */
export interface UiTexts {
  password: { show: string; hide: string };
  search: { clear: string };
}

const texts: Record<string, UiTexts> = { en, de };

/** The texts for a locale such as `de-AT`, by its language; English for a language without texts. */
export function uiTexts(locale: string): UiTexts {
  return texts[locale.split('-')[0]?.toLowerCase() ?? ''] ?? en;
}
