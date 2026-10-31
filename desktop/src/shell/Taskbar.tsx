import {
  IconBell,
  IconLayoutGrid,
  IconLoader2,
  IconMinus,
  IconPlus,
  IconSquare,
  IconX,
} from '../ui/icons';
import { useEffect, useRef, useState, type ReactNode, type Ref } from 'react';
import { Button as AriaButton } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import {
  Badge,
  ContextMenuPopover,
  cx,
  Menu,
  MenuItem,
  MenuSeparator,
  Tooltip,
  TooltipTrigger,
} from '@pithyx/ui';
import { BrandText, LogoMark } from '../ui/Logo';
import { badgeText } from '../apps/host/rules';
import { AppIcon } from './AppIcon';
import { TransfersButton } from './TransfersButton';
import styles from './Taskbar.module.css';
import type { RunningApp, TaskbarPosition } from './types';

export type TaskbarAppAction = 'minimize' | 'restore' | 'addToDesktop' | 'close';

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
  /** The choice in a running app's context menu; without it the bar has no menu. */
  onAppAction?: (id: string, action: TaskbarAppAction) => void;
  /** Offers "Add to desktop" in the context menu (once the desktop items have loaded). */
  canAddToDesktop?: boolean;
  startRef?: Ref<HTMLButtonElement>;
  switcherRef?: Ref<HTMLButtonElement>;
  notificationsRef?: Ref<HTMLButtonElement>;
  /** A horizontal swipe along the bar: 1 for the next app, -1 for the previous one. */
  onSwipe?: (direction: 1 | -1) => void;
  /** The box's state at the start of the status area, for administrators only (D394). */
  boxStatus?: ReactNode;
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
  onAppAction,
  canAddToDesktop = false,
  startRef,
  switcherRef,
  notificationsRef,
  onSwipe,
  boxStatus,
}: TaskbarProps) {
  const { t } = useTranslation();
  const formats = clockFormats(locale);
  const time = formats.time.format(now);
  const date = formats.date.format(now);
  const longDate = formats.long.format(now);
  const [menu, setMenu] = useState<{ id: string; x: number; y: number } | null>(null);
  const menuApp = menu === null ? undefined : apps.find((app) => app.id === menu.id);
  const icon = { size: 18, stroke: 1.75 };
  const list = useRef<HTMLUListElement>(null);
  const focusedApp = apps.find((app) => app.state === 'focused')?.id;
  const vertical = position === 'left' || position === 'right';

  // More apps than fit scroll inside their list; the focused one (also a newly opened one) is brought into view.
  useEffect(() => {
    if (focusedApp !== undefined) {
      list.current
        ?.querySelector(`[data-app="${CSS.escape(focusedApp)}"]`)
        ?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    }
  }, [focusedApp]);

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
        <Tooltip className={styles.brandTooltip}>
          <span role="img" aria-label={t('brand')}>
            <BrandText text={t('brandMark')} />
          </span>
        </Tooltip>
      </TooltipTrigger>

      <ul
        ref={list}
        className={styles.apps}
        aria-label={t('taskbar.runningApps')}
        // A mouse wheel scrolls up and down; along a horizontal bar it moves the apps sideways.
        onWheel={(event) => {
          if (!vertical && Math.abs(event.deltaY) > Math.abs(event.deltaX)) {
            event.currentTarget.scrollLeft += event.deltaY;
          }
        }}
      >
        {apps.map((app) => (
          <li key={app.id} data-app={app.id}>
            <AriaButton
              className={cx(styles.button, styles.app)}
              data-state={app.state}
              data-attention={app.attention === true || undefined}
              aria-label={t('taskbar.app', {
                name: app.name,
                state: app.state,
                count: app.badge ?? 0,
                attention: app.attention === true ? 'yes' : 'no',
              })}
              onPress={() => onApp?.(app.id)}
              onContextMenu={(event) => {
                if (onAppAction !== undefined) {
                  event.preventDefault();
                  setMenu({ id: app.id, x: event.clientX, y: event.clientY });
                }
              }}
              onKeyDown={(event) => {
                if (
                  onAppAction !== undefined &&
                  event.target instanceof Element &&
                  (event.key === 'ContextMenu' || (event.key === 'F10' && event.shiftKey))
                ) {
                  event.preventDefault();
                  const rect = event.target.getBoundingClientRect();
                  setMenu({ id: app.id, x: rect.left, y: rect.top });
                } else {
                  event.continuePropagation();
                }
              }}
            >
              <AppIcon app={app} size={26} />
              {badgeText(app.badge, locale) !== null && (
                <span className={styles.appBadge} aria-hidden>
                  <Badge>{badgeText(app.badge, locale)}</Badge>
                </span>
              )}
              <span className={styles.appName}>{app.name}</span>
              <span className={styles.indicator} aria-hidden />
            </AriaButton>
          </li>
        ))}
      </ul>

      {menu !== null && menuApp !== undefined && onAppAction !== undefined && (
        <ContextMenuPopover
          x={menu.x}
          y={menu.y}
          onClose={() => {
            setMenu(null);
          }}
        >
          <Menu
            aria-label={t('taskbar.appMenu', { name: menuApp.name })}
            autoFocus="first"
            onAction={(key) => {
              setMenu(null);
              onAppAction(menuApp.id, String(key) as TaskbarAppAction);
            }}
          >
            {menuApp.state === 'minimized' ? (
              <MenuItem id="restore" icon={<IconSquare {...icon} />}>
                {t('window.restore')}
              </MenuItem>
            ) : (
              <MenuItem id="minimize" icon={<IconMinus {...icon} />}>
                {t('window.minimize')}
              </MenuItem>
            )}
            {canAddToDesktop && (
              <MenuItem id="addToDesktop" icon={<IconPlus {...icon} />}>
                {t('taskbar.addToDesktop')}
              </MenuItem>
            )}
            <MenuSeparator />
            <MenuItem id="close" icon={<IconX {...icon} />}>
              {t('window.close')}
            </MenuItem>
          </Menu>
        </ContextMenuPopover>
      )}

      <AriaButton
        ref={switcherRef}
        className={cx(styles.button, styles.switcher)}
        aria-label={t('taskbar.switcher', {
          count: apps.length,
          attention: apps.some((app) => app.attention === true) ? 'yes' : 'no',
        })}
        aria-expanded={switcherOpen}
        onPress={() => onSwitcher?.()}
        data-active={switcherOpen || undefined}
        data-attention={apps.some((app) => app.attention === true) || undefined}
      >
        <IconLayoutGrid size={22} stroke={1.75} />
        {apps.length > 0 && (
          <span className={styles.switcherCount} aria-hidden>
            {apps.length}
          </span>
        )}
      </AriaButton>

      <div className={styles.status}>
        {boxStatus !== undefined && <div className={styles.boxStatus}>{boxStatus}</div>}
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
        {/* Not a button: the clock opens nothing, so it neither reacts to the pointer nor takes the focus. */}
        <time
          className={styles.clock}
          dateTime={now.toISOString()}
          title={longDate}
          aria-label={longDate}
        >
          <span className={styles.time}>{time}</span>
          <span className={styles.date}>{date}</span>
        </time>
      </div>
    </nav>
  );
}
