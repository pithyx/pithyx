import { IconX } from '../ui/icons';
import { useTranslation } from 'react-i18next';
import { Button, IconButton } from '../ui/Button';
import { cx } from '../ui/cx';
import { AppIcon } from './AppIcon';
import styles from './Notifications.module.css';
import { useMessages, type Message } from './messages';
import type { AppInfo, TaskbarPosition } from './types';

/** A notification as code and params; title, body and an optional action come from the notifications namespace. */
export interface NotificationItem {
  id: string;
  app: Pick<AppInfo, 'icon' | 'tone' | 'name'>;
  message: Message;
  /** Minutes ago, formatted with Intl.RelativeTimeFormat. */
  minutesAgo: number;
  unread?: boolean;
  tone?: 'default' | 'success' | 'warning' | 'danger';
  /** Stays until its cause is gone: no dismiss button (storage alerts). */
  persistent?: boolean;
  /** Runs the message's action (`<code>.action`). */
  onAction?: () => void;
  /** Further buttons after the message's action, for example Mute. */
  extraActions?: { id: string; label: string; onPress: () => void }[];
}

/** A running job; title and detail come from the jobs namespace. */
export interface JobItem {
  id: string;
  message: Message;
  /** Percent done; null while the total is unknown. */
  progress: number | null;
  /** Asks the daemon to stop the job. */
  onCancel?: () => void;
}

function useRelativeTime(locale: string) {
  const format = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' });
  return (minutes: number) =>
    minutes < 60
      ? format.format(-minutes, 'minute')
      : minutes < 48 * 60
        ? format.format(-Math.round(minutes / 60), 'hour')
        : format.format(-Math.round(minutes / (24 * 60)), 'day');
}

export function NotificationCard({
  item,
  locale,
  toast = false,
  onDismiss,
}: {
  item: NotificationItem;
  locale: string;
  toast?: boolean;
  onDismiss?: (id: string) => void;
}) {
  const { t } = useTranslation();
  const relative = useRelativeTime(locale);
  const text = useMessages('notifications');
  const action = text(item.message, 'action');
  return (
    <article
      className={cx(styles.card, toast && styles.toast)}
      data-tone={item.tone ?? 'default'}
      data-unread={item.unread === true ? true : undefined}
    >
      <AppIcon app={item.app} size={32} />
      <div className={styles.cardBody}>
        <p className={styles.meta}>
          <span>{item.app.name}</span>
          <span aria-hidden>·</span>
          <span>{relative(item.minutesAgo)}</span>
        </p>
        <h3 className={styles.cardTitle}>{text(item.message, 'title')}</h3>
        <p className={styles.text}>{text(item.message, 'body')}</p>
        {(action !== undefined || item.extraActions !== undefined) && (
          <div className={styles.actions}>
            {action !== undefined && (
              <Button size="sm" onPress={() => item.onAction?.()}>
                {action}
              </Button>
            )}
            {item.extraActions?.map((extra) => (
              <Button key={extra.id} size="sm" variant="quiet" onPress={extra.onPress}>
                {extra.label}
              </Button>
            ))}
          </div>
        )}
      </div>
      {item.persistent !== true && (
        <IconButton
          label={t('notifications.dismiss')}
          size="sm"
          className={styles.dismiss}
          onPress={() => onDismiss?.(item.id)}
        >
          <IconX size={15} stroke={2} />
        </IconButton>
      )}
    </article>
  );
}

/** Toasts appear next to the status area: above a bottom taskbar, below a top one. */
export function ToastStack({
  items,
  locale,
  position,
  onDismiss,
}: {
  items: NotificationItem[];
  locale: string;
  position: TaskbarPosition;
  onDismiss?: (id: string) => void;
}) {
  const { t } = useTranslation();
  return (
    <section
      className={styles.stack}
      data-position={position}
      aria-label={t('notifications.toasts')}
      aria-live="polite"
    >
      {items.map((item) => (
        <NotificationCard
          key={item.id}
          item={item}
          locale={locale}
          toast
          {...(onDismiss === undefined ? {} : { onDismiss })}
        />
      ))}
    </section>
  );
}
