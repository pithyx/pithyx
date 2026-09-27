import { IconBell, IconLayoutGrid, IconLoader2 } from '@tabler/icons-react';
import type { ReactNode, Ref } from 'react';
import { Button as AriaButton } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { Badge } from '../ui/Controls';
import { cx } from '../ui/cx';
import { LogoMark } from '../ui/Logo';
import { AppIcon } from './AppIcon';
import styles from './Taskbar.module.css';
import type { RunningApp, TaskbarPosition } from './types';

export interface TaskbarProps {
  position: TaskbarPosition;
  apps: RunningApp[];
  startOpen?: boolean;
  jobs: number;
  unread: number;
  notificationsOpen?: boolean;
  now: Date;
  locale: string;
  user: { name: string; initials: string };
  switcherOpen?: boolean;
  onStart?: () => void;
  onNotifications?: () => void;
  onSwitcher?: () => void;
  onApp?: (id: string) => void;
  userMenu?: (trigger: ReactNode) => ReactNode;
  startRef?: Ref<HTMLButtonElement>;
  switcherRef?: Ref<HTMLButtonElement>;
}

/** Start button, running apps and the status area (jobs, notifications, clock, user). There is no tray. */
export function Taskbar({
  position,
  apps,
  startOpen = false,
  jobs,
  unread,
  notificationsOpen = false,
  now,
  locale,
  user,
  switcherOpen = false,
  onStart,
  onNotifications,
  onSwitcher,
  onApp,
  userMenu,
  startRef,
  switcherRef,
}: TaskbarProps) {
  const { t } = useTranslation();
  const time = new Intl.DateTimeFormat(locale, { hour: '2-digit', minute: '2-digit' }).format(now);
  const date = new Intl.DateTimeFormat(locale, {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
  }).format(now);
  const longDate = new Intl.DateTimeFormat(locale, {
    dateStyle: 'full',
    timeStyle: 'short',
  }).format(now);
  const userButton = (
    <AriaButton
      className={cx(styles.button, styles.user)}
      aria-label={t('taskbar.userMenu', { name: user.name })}
    >
      <span className={styles.avatar} aria-hidden>
        {user.initials}
      </span>
    </AriaButton>
  );

  return (
    <nav className={styles.taskbar} data-position={position} aria-label={t('taskbar.label')}>
      <AriaButton
        ref={startRef}
        className={cx(styles.button, styles.start)}
        aria-label={t('taskbar.start')}
        aria-expanded={startOpen}
        data-active={startOpen || undefined}
        onPress={() => onStart?.()}
      >
        <LogoMark size={26} />
      </AriaButton>

      <ul className={styles.apps} aria-label={t('taskbar.runningApps')}>
        {apps.map((app) => (
          <li key={app.id}>
            <AriaButton
              className={cx(styles.button, styles.app)}
              data-state={app.state}
              aria-label={t('taskbar.app', { name: app.name, state: app.state })}
              onPress={() => onApp?.(app.id)}
            >
              <AppIcon app={app} size={26} />
              <span className={styles.appName}>{app.name}</span>
              <span className={styles.indicator} aria-hidden />
            </AriaButton>
          </li>
        ))}
      </ul>

      <AriaButton
        ref={switcherRef}
        className={cx(styles.button, styles.switcher)}
        aria-label={t('taskbar.switcher', { count: apps.length })}
        aria-expanded={switcherOpen}
        onPress={() => onSwitcher?.()}
      >
        <IconLayoutGrid size={22} stroke={1.75} />
        {apps.length > 0 && (
          <span className={styles.switcherCount} aria-hidden>
            {apps.length}
          </span>
        )}
      </AriaButton>

      <div className={styles.status}>
        {jobs > 0 && (
          <AriaButton
            className={cx(styles.button, styles.jobs)}
            aria-label={t('taskbar.jobs', { count: jobs })}
            onPress={() => onNotifications?.()}
          >
            <IconLoader2 className={styles.spinner} size={20} stroke={1.75} />
            <span className={styles.jobCount} aria-hidden>
              {jobs}
            </span>
          </AriaButton>
        )}
        <AriaButton
          className={styles.button}
          aria-label={t('taskbar.notifications', { count: unread })}
          aria-expanded={notificationsOpen}
          data-active={notificationsOpen || undefined}
          onPress={() => onNotifications?.()}
        >
          <IconBell size={21} stroke={1.75} />
          {unread > 0 && (
            <span className={styles.badge} aria-hidden>
              <Badge>{unread}</Badge>
            </span>
          )}
        </AriaButton>
        <AriaButton className={cx(styles.button, styles.clock)} aria-label={longDate}>
          <span className={styles.time}>{time}</span>
          <span className={styles.date}>{date}</span>
        </AriaButton>
        {userMenu === undefined ? userButton : userMenu(userButton)}
      </div>
    </nav>
  );
}
