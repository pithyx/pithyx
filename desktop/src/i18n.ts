import i18next from 'i18next';
import ICU from 'i18next-icu';
import { initReactI18next } from 'react-i18next';
import deDesktop from '../../i18n/de/desktop.json';
import enDesktop from '../../i18n/en/desktop.json';

export const resources = {
  en: { desktop: enDesktop },
  de: { desktop: deDesktop },
} as const;

export type Language = keyof typeof resources;

export function detectLanguage(preferred: readonly string[]): Language {
  for (const tag of preferred) {
    const base = tag.toLowerCase().split('-')[0];
    if (base === 'en' || base === 'de') {
      return base;
    }
  }
  return 'en';
}

declare module 'i18next' {
  interface CustomTypeOptions {
    defaultNS: 'desktop';
    resources: (typeof resources)['en'];
  }
}

export async function initI18n(): Promise<void> {
  const language = detectLanguage(navigator.languages);
  document.documentElement.lang = language;
  await i18next
    .use(ICU)
    .use(initReactI18next)
    .init({
      resources,
      lng: language,
      fallbackLng: 'en',
      defaultNS: 'desktop',
      interpolation: { escapeValue: false },
    });
}
