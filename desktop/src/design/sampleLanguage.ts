import i18next from 'i18next';

/**
 * A sample text in the gallery's language, so the German screenshots show German names and notes. The sample modules
 * load after i18n (main.tsx imports the screens dynamically), so this is read once, when they are evaluated.
 */
export function local(text: { en: string; de: string }): string {
  return i18next.language.startsWith('de') ? text.de : text.en;
}
