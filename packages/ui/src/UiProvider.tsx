import type { ReactNode } from 'react';
import { I18nProvider, useLocale } from 'react-aria-components';
import { uiTexts, type UiTexts } from './texts';

export interface UiProviderProps {
  /** A BCP 47 tag such as `de-AT`. Dates and numbers follow it; the texts follow its language. */
  locale: string;
  children: ReactNode;
}

/**
 * Sets the language of the components below it: React Aria's formatting and announcements, and the package's own
 * texts (English and German; English for any other language). Without it, both follow the browser.
 */
export function UiProvider({ locale, children }: UiProviderProps) {
  return <I18nProvider locale={locale}>{children}</I18nProvider>;
}

/** The package's texts in the language of the nearest {@link UiProvider}. */
export function useUiTexts(): UiTexts {
  return uiTexts(useLocale().locale);
}
