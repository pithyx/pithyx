import i18next from 'i18next';
import ICU from 'i18next-icu';
import { initReactI18next } from 'react-i18next';
import deAuth from '../../i18n/de/auth.json';
import deDesktop from '../../i18n/de/desktop.json';
import deDisks from '../../i18n/de/disks.json';
import deErrors from '../../i18n/de/errors.json';
import enAuth from '../../i18n/en/auth.json';
import enDesktop from '../../i18n/en/desktop.json';
import enDisks from '../../i18n/en/disks.json';
import enErrors from '../../i18n/en/errors.json';

export const resources = {
  en: { desktop: enDesktop, auth: enAuth, disks: enDisks, errors: enErrors },
  de: { desktop: deDesktop, auth: deAuth, disks: deDisks, errors: deErrors },
} as const;

export type Language = keyof typeof resources;
export const languages = Object.keys(resources) as Language[];

// A per-device convenience; it moves to the user profile once profiles exist.
const STORAGE_KEY = 'vyxis.language';

declare module 'i18next' {
  interface CustomTypeOptions {
    defaultNS: 'desktop';
    resources: (typeof resources)['en'];
  }
}

export function detectLanguage(preferred: readonly string[]): Language {
  for (const tag of preferred) {
    const base = tag.toLowerCase().split('-')[0];
    if (base === 'en' || base === 'de') {
      return base;
    }
  }
  return 'en';
}

/** The language the user picked on this device, or null for the browser language. */
export function storedLanguage(): Language | null {
  try {
    const value = localStorage.getItem(STORAGE_KEY);
    return value === 'en' || value === 'de' ? value : null;
  } catch {
    return null;
  }
}

export async function setLanguage(language: Language | null): Promise<void> {
  try {
    if (language === null) {
      localStorage.removeItem(STORAGE_KEY);
    } else {
      localStorage.setItem(STORAGE_KEY, language);
    }
  } catch {
    // Storage can be unavailable (private mode); the choice then lasts for this page only.
  }
  const effective = language ?? detectLanguage(navigator.languages);
  document.documentElement.lang = effective;
  await i18next.changeLanguage(effective);
}

export async function initI18n(): Promise<void> {
  const language = storedLanguage() ?? detectLanguage(navigator.languages);
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
