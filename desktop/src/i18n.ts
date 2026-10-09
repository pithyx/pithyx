import i18next from 'i18next';
import { useSyncExternalStore } from 'react';
import { initReactI18next, useTranslation } from 'react-i18next';
import { icuFormat } from './icuFormat';
import { desktopNamespaces, signInNamespaces } from './i18nNamespaces';
import type auth from '../../i18n/en/auth.json';
import type desktop from '../../i18n/en/desktop.json';
import type errors from '../../i18n/en/errors.json';
import type files from '../../i18n/en/files.json';
import type installer from '../../i18n/en/installer.json';
import type jobs from '../../i18n/en/jobs.json';
import type logs from '../../i18n/en/logs.json';
import type monitor from '../../i18n/en/monitor.json';
import type network from '../../i18n/en/network.json';
import type notifications from '../../i18n/en/notifications.json';
import type settings from '../../i18n/en/settings.json';
import type storage from '../../i18n/en/storage.json';
import type store from '../../i18n/en/store.json';
import type users from '../../i18n/en/users.json';
import type viewer from '../../i18n/en/viewer.json';

/** The English texts define the keys; every other language has the same (checked by i18n.test.ts). */
export interface Resources {
  desktop: typeof desktop;
  auth: typeof auth;
  errors: typeof errors;
  files: typeof files;
  installer: typeof installer;
  storage: typeof storage;
  store: typeof store;
  settings: typeof settings;
  notifications: typeof notifications;
  jobs: typeof jobs;
  logs: typeof logs;
  monitor: typeof monitor;
  network: typeof network;
  viewer: typeof viewer;
  users: typeof users;
}
type Namespace = keyof Resources;

export const languages = ['en', 'de'] as const;
export type Language = (typeof languages)[number];

// Only the active language is loaded, each namespace when a chunk needs it (i18nNamespaces.ts).
// Compiled at build time (scripts/compile-messages.ts), formatted by icuFormat.
// The texts of @pithyx/ui (ui.json) come with its components, those of the mock desktop of `pithyx dev` (cli.json)
// with @pithyx/cli.
const translationFiles = import.meta.glob<object>(
  ['../../i18n/*/*.json', '!../../i18n/*/ui.json', '!../../i18n/*/cli.json'],
  {
    query: '?compiled',
    import: 'default',
  },
);
// Namespaces asked for so far; a language switch loads the same ones in the new language.
const requested = new Set<Namespace>();
let markReady: () => void = () => undefined;
const ready = new Promise<void>((resolve) => {
  markReady = resolve;
});

function fetchNamespaces(
  language: Language,
  namespaces: readonly Namespace[],
): Promise<(readonly [Namespace, object])[]> {
  namespaces.forEach((namespace) => requested.add(namespace));
  return Promise.all(
    namespaces.map(async (namespace) => {
      const load = translationFiles[`../../i18n/${language}/${namespace}.json`];
      if (load === undefined) {
        throw new Error(`Missing translation file ${language}/${namespace}.json`);
      }
      return [namespace, await load()] as const;
    }),
  );
}

async function loadNamespaces(language: Language, namespaces: readonly Namespace[]): Promise<void> {
  for (const [namespace, bundle] of await fetchNamespaces(language, namespaces)) {
    i18next.addResourceBundle(language, namespace, bundle, true, true);
  }
}

/** Loads namespaces in the active language, for a chunk that needs them. May run before initI18n. */
export async function loadTranslations(...namespaces: Namespace[]): Promise<void> {
  await ready;
  const language = languages.find((code) => code === i18next.language) ?? 'en';
  await loadNamespaces(language, namespaces);
}

/** Loads every namespace, for the design gallery, which shows every app at once. */
export function loadAllTranslations(): Promise<void> {
  const namespaces = Object.keys(translationFiles)
    .filter((path) => path.startsWith('../../i18n/en/'))
    .map((path) => path.replace('../../i18n/en/', '').replace('.json', '') as Namespace);
  return loadTranslations(...namespaces);
}

/** Loads the texts of the desktop shell; the desktop chunk waits for them. */
export function loadDesktopTranslations(): Promise<void> {
  return loadTranslations(...desktopNamespaces);
}

// A per-device convenience; it moves to the user profile once profiles exist.
const STORAGE_KEY = 'pithyx.language';

declare module 'i18next' {
  interface CustomTypeOptions {
    defaultNS: 'desktop';
    resources: Resources;
  }
}

/** A language's name in that language, so everyone finds theirs. */
export function languageName(code: string): string {
  return new Intl.DisplayNames([code], { type: 'language' }).of(code) ?? code;
}

// The box's regional settings (D282) from GET /api/setup: the language for browsers that ask for one Pithyx does not
// have, and the region whose formats everyone sees (null: the formats of the language).
let systemLanguage: Language = 'en';
let systemFormats: string | null = null;

export function detectLanguage(
  preferred: readonly string[],
  fallback: Language = systemLanguage,
): Language {
  for (const tag of preferred) {
    const base = tag.toLowerCase().split('-')[0];
    if (base === 'en' || base === 'de') {
      return base;
    }
  }
  return fallback;
}

const formatListeners = new Set<() => void>();

export function subscribeFormats(listener: () => void): () => void {
  formatListeners.add(listener);
  return () => {
    formatListeners.delete(listener);
  };
}

/**
 * The locale for dates, times and numbers: the language with the system's region, for example `de-AT` or `en-DE`.
 * Texts and plural rules stay with the language alone.
 */
export function formatLocale(language: string = i18next.language): string {
  return systemFormats === null ? language : `${language}-${systemFormats}`;
}

/** {@link formatLocale} for components: renders again when the language or the system region changes. */
export function useFormatLocale(): string {
  const { i18n } = useTranslation();
  const formats = useSyncExternalStore(subscribeFormats, () => systemFormats);
  return formats === null ? i18n.language : `${i18n.language}-${formats}`;
}

/**
 * Takes the system language and region from the daemon. A user on "Automatic" whose browser language Pithyx does not
 * have switches to the new system language; every open window formats with the new region.
 */
export async function applySystemRegion(region: {
  language: string;
  region?: string | null;
}): Promise<void> {
  const language = languages.find((code) => code === region.language) ?? 'en';
  const formats = region.region ?? null;
  if (language === systemLanguage && formats === systemFormats) {
    return;
  }
  systemLanguage = language;
  systemFormats = formats;
  formatListeners.forEach((listener) => {
    listener();
  });
  if (!i18next.isInitialized) {
    return;
  }
  if (storedLanguage() === null) {
    await setLanguage(null);
  }
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
  await loadNamespaces(effective, [...requested]);
  document.documentElement.lang = effective;
  await i18next.changeLanguage(effective);
}

/** Starts i18next with the sign-in texts of the device's language, or of `language` when given. */
export async function initI18n(language?: Language): Promise<void> {
  const effective = language ?? storedLanguage() ?? detectLanguage(navigator.languages);
  document.documentElement.lang = effective;
  const bundles = await fetchNamespaces(effective, signInNamespaces);
  await i18next
    .use(icuFormat)
    .use(initReactI18next)
    .init({
      resources: { [effective]: Object.fromEntries(bundles) },
      partialBundledLanguages: true,
      lng: effective,
      // Every language has every key, so no fallback language is loaded.
      fallbackLng: false,
      ns: [...signInNamespaces],
      defaultNS: 'desktop',
      interpolation: { escapeValue: false },
    });
  markReady();
}
