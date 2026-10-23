import { useQueryClient } from '@tanstack/react-query';
import type { components } from '@pithyx/api-client';
import { useTranslation } from 'react-i18next';
import { Badge, Button, Checkbox, Dialog } from '@pithyx/ui';
import { queryKeys } from '../api/queries';
import { isAppAllowed } from '../apps/access';
import { useInstalledApps } from '../apps/installed';
import { useAppName } from '../apps/names';
import { appsFor } from '../apps/registry';
import { showError } from '../errors/errorStore';
import { AppIcon } from '../shell/AppIcon';
import { MAX_STARTUP_APPS, saveStartupApps, useStartupApps } from '../wm/startup';
import styles from './Desktop.module.css';

/** Whether an installed app's manifest suggests opening it after sign-in (`window.suggestAtSignIn`). */
function suggested(window: unknown): boolean {
  return (window as { suggestAtSignIn?: unknown } | null)?.suggestAtSignIn === true;
}

/**
 * The apps that open after signing in (D373): every app the user may open, ticked ones open in the order they were
 * ticked. An app may suggest itself; it is never ticked by itself.
 */
export function StartupAppsDialog({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const appName = useAppName();
  const chosen = useStartupApps((state) => state.apps);
  // Read so the list follows installs and removals while the dialog is open.
  const installed = useInstalledApps((state) => state.apps);
  const user = useQueryClient().getQueryData<components['schemas']['UserInfo']>(queryKeys.session);
  const apps = appsFor((id) => isAppAllowed(id, user?.permissions)).map((app) => ({
    ...app,
    suggested:
      app.thirdParty === true &&
      suggested(installed?.find((candidate) => candidate.id === app.id)?.window),
  }));
  const full = chosen.length >= MAX_STARTUP_APPS;
  const toggle = (id: string, on: boolean) => {
    const next = on ? [...chosen, id] : chosen.filter((other) => other !== id);
    saveStartupApps(next).catch(showError);
  };
  return (
    <Dialog
      isOpen
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('startupApps.title')}
      actions={<Button onPress={onClose}>{t('startupApps.close')}</Button>}
    >
      <p>{t('startupApps.intro', { max: MAX_STARTUP_APPS })}</p>
      <ul className={styles.startupApps}>
        {apps.map((app) => {
          const on = chosen.includes(app.id);
          return (
            <li key={app.id} className={styles.startupApp}>
              <AppIcon app={app} size={24} />
              <Checkbox
                isSelected={on}
                isDisabled={!on && full}
                onChange={(next) => {
                  toggle(app.id, next);
                }}
              >
                {appName(app.id)}
              </Checkbox>
              {app.suggested && <Badge tone="neutral">{t('startupApps.suggested')}</Badge>}
            </li>
          );
        })}
      </ul>
    </Dialog>
  );
}
