import de from '../../../i18n/de/ui.json';
import en from '../../../i18n/en/ui.json';

/**
 * The texts the components show or announce on their own. They live with every other translation, in
 * `i18n/<lang>/ui.json` of the repository, and the build bundles them, so the package needs no i18n library.
 */
export interface UiTexts {
  /** The reveal button of `PasswordField`. */
  password: {
    /** Label while the password is hidden. */
    show: string;
    /** Label while the password is shown. */
    hide: string;
  };
  /** The clear button of `SearchField`. */
  search: {
    /** Label of the clear button. */
    clear: string;
  };
  /** `AppFrame`. */
  frame: {
    /** The button that leads from a page back to the navigation in the compact size class. */
    back: string;
  };
  /** `NavList`. */
  nav: {
    /** Label and placeholder of the search field. */
    search: string;
    /** Shown when no item matches the search. */
    noResults: string;
  };
  /** `Select`. */
  select: {
    /** Shown while nothing is chosen. */
    placeholder: string;
  };
  /** `ActionBar`. */
  actionBar: {
    /** The button that applies the changes. */
    apply: string;
    /** The button that drops the changes. */
    discard: string;
  };
  /** `Card`. */
  card: {
    /** The expand button, announced together with the card's title. */
    details: string;
  };
  /** `AttentionList`. */
  attention: {
    /** The default heading. */
    title: string;
    /** Announced for a row with the tone `warning`. */
    warning: string;
    /** Announced for a row with the tone `danger`. */
    danger: string;
    /** Announced for a row with the tone `info`. */
    info: string;
  };
}

const texts: Record<string, UiTexts> = { en, de };

/**
 * The texts for a locale such as `de-AT`, by its language; English for a language without texts.
 *
 * @param locale - A BCP 47 tag.
 * @returns The texts in that language, or in English.
 */
export function uiTexts(locale: string): UiTexts {
  return texts[locale.split('-')[0]?.toLowerCase() ?? ''] ?? en;
}
