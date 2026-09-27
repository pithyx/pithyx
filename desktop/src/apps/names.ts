import { useTranslation } from 'react-i18next';

/** The translated name of an app (`apps.<id>`). i18n.test.ts checks that every core app has one. */
export function useAppName(): (id: string) => string {
  const { t } = useTranslation();
  // App ids are data, so the key cannot be typed as a literal.
  const translate = t as unknown as (key: string) => string;
  return (id) => translate(`apps.${id}`);
}
