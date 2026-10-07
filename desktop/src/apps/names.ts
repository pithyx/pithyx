import { useTranslation } from 'react-i18next';
import { manifestText, useInstalledApps } from './installed';

/**
 * The name of an app in the user's language: a core app's from `apps.<id>` (i18n.test.ts checks that every core app
 * has one), an installed app's from its manifest. Components that use it follow the topic `apps`.
 */
export function useAppName(): (id: string) => string {
  const { t, i18n } = useTranslation();
  const installed = useInstalledApps((state) => state.apps);
  // App ids are data, so the key cannot be typed as a literal.
  const translate = t as unknown as (key: string) => string;
  return (id) => {
    const app = installed?.find((entry) => entry.id === id);
    return app === undefined ? translate(`apps.${id}`) : manifestText(app.name, i18n.language);
  };
}
