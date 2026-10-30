import { useTranslation } from 'react-i18next';
import { Button, cx } from '@pithyx/ui';
import { coreAppIds } from '../apps/ids';
import { openApp } from '../wm/store';
import desktopStyles from '../desktop/Desktop.module.css';
import { useMessages } from './messages';
import { alertMessage, type StorageAlert } from './storageAlerts';

// Loaded when an administrator has a critical alert or opens the bell, not with the desktop.

/** A critical storage alert at the top of the desktop, with a way to the Storage page. */
export function AlertBanner({ alert }: { alert: StorageAlert }) {
  const { t } = useTranslation();
  const text = useMessages('notifications');
  return (
    <div
      className={cx(desktopStyles.banner, desktopStyles.danger, desktopStyles.alertBanner)}
      role="alert"
    >
      <span>{text(alertMessage(alert), 'title')}</span>
      <Button
        size="sm"
        onPress={() => {
          openApp(coreAppIds.settings, { args: alertPage(alert) });
        }}
      >
        {t('storageAlert.open')}
      </Button>
    </div>
  );
}

/** Where Settings opens for an alert: SMART alerts at the disks, every other one at the pools. */
export function alertPage(alert: Pick<StorageAlert, 'key'>): { page: 'disks' | 'pools' } {
  return { page: alert.key.startsWith('disk.') ? 'disks' : 'pools' };
}
