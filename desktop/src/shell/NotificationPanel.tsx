import type { ReactNode, Ref } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '../ui/Button';
import { NotificationCard, type NotificationItem } from './Notifications';
import styles from './Notifications.module.css';
import type { TaskbarPosition } from './types';

/** The panel behind the bell: running jobs first (a JobList), then notifications. */
export function NotificationPanel({
  jobs,
  items,
  locale,
  position,
  footer,
  panelRef,
}: {
  jobs?: ReactNode;
  items: NotificationItem[];
  locale: string;
  position: TaskbarPosition;
  footer?: ReactNode;
  panelRef?: Ref<HTMLElement>;
}) {
  const { t } = useTranslation();
  return (
    <section
      ref={panelRef}
      className={styles.panel}
      data-position={position}
      role="dialog"
      aria-label={t('notifications.title')}
    >
      <header className={styles.panelHeader}>
        <h2 className={styles.panelTitle}>{t('notifications.title')}</h2>
        {items.some((item) => item.persistent !== true) && (
          <Button variant="quiet" size="sm">
            {t('notifications.clearAll')}
          </Button>
        )}
      </header>
      <div className={styles.panelBody}>
        {jobs}
        <section className={styles.group} aria-labelledby="panel-items">
          <h3 id="panel-items" className={styles.groupTitle}>
            {t('notifications.recent')}
          </h3>
          {items.length === 0 && <p className={styles.text}>{t('notifications.empty')}</p>}
          {items.map((item) => (
            <NotificationCard key={item.id} item={item} locale={locale} />
          ))}
        </section>
      </div>
      {footer}
    </section>
  );
}
