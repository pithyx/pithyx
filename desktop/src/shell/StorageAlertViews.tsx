import { IconDeviceDesktop } from '../ui/icons';
import type { Ref } from 'react';
import { useTranslation } from 'react-i18next';
import { findApp } from '../apps/registry';
import { Button } from '../ui/Button';
import { cx } from '../ui/cx';
import { openApp } from '../wm/store';
import desktopStyles from '../desktop/Desktop.module.css';
import { NotificationPanel } from './NotificationPanel';
import type { NotificationItem } from './Notifications';
import { useMessages } from './messages';
import { alertMessage, setAlertMuted, type StorageAlert } from './storageAlerts';
import type { TaskbarPosition } from './types';

// Loaded when an administrator has an alert or opens the bell, not with the desktop.

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
          openApp('storage', alertSection(alert));
        }}
      >
        {t('storageAlert.open')}
      </Button>
    </div>
  );
}

/** The notification panel with the storage alerts as persistent items: they can be muted, not dismissed. */
export function AlertPanel({
  alerts,
  now,
  locale,
  position,
  panelRef,
}: {
  alerts: StorageAlert[];
  now: Date;
  locale: string;
  position: TaskbarPosition;
  panelRef: Ref<HTMLElement>;
}) {
  const { t } = useTranslation();
  const storage = findApp('storage');
  const items: NotificationItem[] = alerts.map((alert) => ({
    id: alert.key,
    app: {
      icon: storage?.icon ?? IconDeviceDesktop,
      tone: storage?.tone ?? 'graphite',
      name: t('notifications.storage'),
    },
    message: alertMessage(alert),
    minutesAgo: Math.max(0, Math.round((now.getTime() - new Date(alert.since).getTime()) / 60_000)),
    unread: !alert.muted,
    tone: alert.severity === 'critical' ? 'danger' : 'warning',
    persistent: true,
    onAction: () => {
      openApp('storage', alertSection(alert));
    },
    extraActions: [
      {
        id: 'mute',
        label: alert.muted ? t('notifications.unmute') : t('notifications.mute'),
        onPress: () => {
          void setAlertMuted(alert.key, !alert.muted);
        },
      },
    ],
  }));
  return (
    <NotificationPanel panelRef={panelRef} items={items} locale={locale} position={position} />
  );
}

/** Where Storage opens for an alert: SMART alerts at the disks, every other one at the pools. */
function alertSection(alert: StorageAlert): { section: 'disks' | 'pools' } {
  return { section: alert.key.startsWith('disk.') ? 'disks' : 'pools' };
}
