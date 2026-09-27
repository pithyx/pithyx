import type { ReactNode } from 'react';
import { I18nProvider } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

/** React Aria formats and labels in the language the user picked, not the browser language. */
export function AriaLocale({ children }: { children: ReactNode }) {
  const { i18n } = useTranslation();
  return <I18nProvider locale={i18n.language}>{children}</I18nProvider>;
}
