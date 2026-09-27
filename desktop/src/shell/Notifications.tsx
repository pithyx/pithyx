import { IconX } from '../ui/icons';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, IconButton } from '../ui/Button';
import { ProgressBar } from '../ui/Controls';
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
}

/** A running job; title and detail come from the jobs namespace. */
export interface JobItem {
  id: string;
  message: Message;
  progress: number;
}

function useRelativeTime(locale: string) {
  const format = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' });
  return (minutes: number) =>
    minutes < 60
      ? format.format(-minutes, 'minute')
      : format.format(-Math.round(minutes / 60), 'hour');
}

function NotificationCard({
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
        {action !== undefined && (
          <div className={styles.actions}>
            <Button size="sm">{action}</Button>
          </div>
        )}
      </div>
      <IconButton
        label={t('notifications.dismiss')}
        size="sm"
        className={styles.dismiss}
        onPress={() => onDismiss?.(item.id)}
      >
        <IconX size={15} stroke={2} />
      </IconButton>
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

/** The panel behind the bell: running jobs first, then notifications. */
export function NotificationPanel({
  jobs,
  items,
  locale,
  position,
  footer,
}: {
  jobs: JobItem[];
  items: NotificationItem[];
  locale: string;
  position: TaskbarPosition;
  footer?: ReactNode;
}) {
  const { t } = useTranslation();
  const jobText = useMessages('jobs');
  return (
    <section
      className={styles.panel}
      data-position={position}
      role="dialog"
      aria-label={t('notifications.title')}
    >
      <header className={styles.panelHeader}>
        <h2 className={styles.panelTitle}>{t('notifications.title')}</h2>
        <Button variant="quiet" size="sm">
          {t('notifications.clearAll')}
        </Button>
      </header>
      <div className={styles.panelBody}>
        {jobs.length > 0 && (
          <section className={styles.group} aria-labelledby="panel-jobs">
            <h3 id="panel-jobs" className={styles.groupTitle}>
              {t('notifications.jobs')}
            </h3>
            {jobs.map((entry) => (
              <div key={entry.id} className={styles.job}>
                <ProgressBar label={jobText(entry.message, 'title')} value={entry.progress} />
                <p className={styles.jobDetail}>{jobText(entry.message, 'detail')}</p>
              </div>
            ))}
          </section>
        )}
        <section className={styles.group} aria-labelledby="panel-items">
          <h3 id="panel-items" className={styles.groupTitle}>
            {t('notifications.recent')}
          </h3>
          {items.map((item) => (
            <NotificationCard key={item.id} item={item} locale={locale} />
          ))}
        </section>
      </div>
      {footer}
    </section>
  );
}
