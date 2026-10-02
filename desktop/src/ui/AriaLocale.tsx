import type { ReactNode } from 'react';
import { I18nProvider } from 'react-aria-components';
import { useFormatLocale } from '../i18n';

/** React Aria formats and labels in the language the user picked, not the browser language. */
export function AriaLocale({ children }: { children: ReactNode }) {
  const locale = useFormatLocale();
  return <I18nProvider locale={locale}>{children}</I18nProvider>;
}
