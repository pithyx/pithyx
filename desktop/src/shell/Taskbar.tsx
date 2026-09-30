import { IconBell, IconLayoutGrid, IconLoader2 } from '../ui/icons';
import type { Ref } from 'react';
import { Button as AriaButton, Tooltip, TooltipTrigger } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { Badge } from '../ui/Controls';
import { cx } from '../ui/cx';
import { BrandText, LogoMark } from '../ui/Logo';
import tooltipStyles from '../ui/Tooltip.module.css';
import { AppIcon } from './AppIcon';
import { TransfersButton } from './TransfersButton';
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
  switcherOpen?: boolean;
  onStart?: () => void;
  onNotifications?: () => void;
  onSwitcher?: () => void;
  onApp?: (id: string) => void;
  startRef?: Ref<HTMLButtonElement>;
  switcherRef?: Ref<HTMLButtonElement>;
  notificationsRef?: Ref<HTMLButtonElement>;
  /** A horizontal swipe along the bar: 1 for the next app, -1 for the previous one. */
  onSwipe?: (direction: 1 | -1) => void;
}

// Creating Intl formatters is slow; the clock needs the same three for every render.
const clockFormatCache = new Map<string, Record<'time' | 'date' | 'long', Intl.DateTimeFormat>>();

function clockFormats(locale: string) {
  let formats = clockFormatCache.get(locale);
  if (formats === undefined) {
    formats = {
      time: new Intl.DateTimeFormat(locale, { hour: '2-digit', minute: '2-digit' }),
      date: new Intl.DateTimeFormat(locale, { day: '2-digit', month: '2-digit', year: 'numeric' }),
      long: new Intl.DateTimeFormat(locale, { dateStyle: 'full', timeStyle: 'short' }),
    };
    clockFormatCache.set(locale, formats);
  }
  return formats;
}

/** A swipe must cover this distance and stay mostly horizontal. */
const SWIPE_DISTANCE = 60;

/** Start button, running apps and the status area (jobs, notifications, clock). The profile is in the start menu. */
export function Taskbar({
  position,
  apps,
  startOpen = false,
  jobs,
  unread,
  notificationsOpen = false,
  now,
  locale,
  switcherOpen = false,
  onStart,
  onNotifications,
  onSwitcher,
  onApp,
  startRef,
  switcherRef,
  notificationsRef,
  onSwipe,
}: TaskbarProps) {
  const { t } = useTranslation();
  const formats = clockFormats(locale);
  const time = formats.time.format(now);
  const date = formats.date.format(now);
  const longDate = formats.long.format(now);

  return (
    <nav
      className={styles.taskbar}
      data-position={position}
      aria-label={t('taskbar.label')}
      // Capture phase: React Aria buttons stop the propagation of their pointer events.
      onPointerDownCapture={(event) => {
        if (onSwipe === undefined || !event.isPrimary) {
          return;
        }
        const start = { x: event.clientX, y: event.clientY };
        const pointerId = event.pointerId;
        // Listening on the document instead of capturing keeps the buttons in the bar pressable.
        const listeners = new AbortController();
        document.addEventListener(
          'pointerup',
          (up) => {
            if (up.pointerId !== pointerId) {
              return;
            }
            listeners.abort();
            const dx = up.clientX - start.x;
            if (
              Math.abs(dx) >= SWIPE_DISTANCE &&
              Math.abs(up.clientY - start.y) < Math.abs(dx) / 2
            ) {
              onSwipe(dx < 0 ? 1 : -1);
            }
          },
          { capture: true, signal: listeners.signal },
        );
        document.addEventListener(
          'pointercancel',
          (cancel) => {
            if (cancel.pointerId === pointerId) {
              listeners.abort();
            }
          },
          { capture: true, signal: listeners.signal },
        );
      }}
    >
      <TooltipTrigger delay={600} closeDelay={0}>
        <AriaButton
          ref={startRef}
          className={cx(styles.button, styles.start)}
          aria-label={t('taskbar.start')}
          aria-expanded={startOpen}
          data-active={startOpen || undefined}
          onPress={() => onStart?.()}
        >
          <LogoMark size={38} motion="hover" />
        </AriaButton>
        <Tooltip className={cx(tooltipStyles.tooltip, styles.brandTooltip)} offset={6}>
          <span role="img" aria-label={t('brand')}>
            <BrandText text={t('brandMark')} />
          </span>
        </Tooltip>
      </TooltipTrigger>

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
        data-active={switcherOpen || undefined}
      >
        <IconLayoutGrid size={22} stroke={1.75} />
        {apps.length > 0 && (
          <span className={styles.switcherCount} aria-hidden>
            {apps.length}
          </span>
        )}
      </AriaButton>

      <div className={styles.status}>
        <TransfersButton position={position} />
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
          ref={notificationsRef}
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
      </div>
    </nav>
  );
}
