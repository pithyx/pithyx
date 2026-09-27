import {
  IconDeviceDesktop,
  IconDevices,
  IconLanguage,
  IconLayoutBottombar,
  IconLogout,
  IconShieldLock,
} from '../ui/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap, type components } from '@vyxis/api-client';
import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { Header, MenuSection, SubmenuTrigger } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import { queryKeys } from '../api/queries';
import { useAppName } from '../apps/names';
import { coreApps, findApp } from '../apps/registry';
import { initialsOf } from '../auth/parts';
import { clearSessionHint } from '../auth/sessionHint';
import { LanguageOptions } from './LanguageOptions';
import { TotpSetup } from '../auth/TotpSetup';
import { useDaemonStatus } from '../daemonStatus';
import { showError } from '../errors/errorStore';
import { useRealtime } from '../realtime/connection';
import { AppSwitcher } from '../shell/AppSwitcher';
import { DesktopIcon } from '../shell/DesktopIcon';
import { DesktopItems, DesktopShell } from '../shell/DesktopShell';
import { AriaLocale } from '../ui/AriaLocale';
import { ToastStack } from '../shell/Notifications';
import { StartMenu } from '../shell/StartMenu';
import { Taskbar } from '../shell/Taskbar';
import type { RunningApp, TaskbarPosition } from '../shell/types';
import { dismissToast, notify, useToasts } from '../shell/toasts';
import { useDismiss } from '../shell/useDismiss';
import { cx } from '../ui/cx';
import {
  Menu,
  MenuItem,
  MenuPopover,
  MenuSeparator,
  MenuTrigger,
  useDeliberatePress,
} from '../ui/Menu';
import { useBackGesture } from '../wm/backGesture';
import { applySavedLayout, saveLayoutBeforeSignOut, useLayoutPersistence } from '../wm/persistence';
import { useSizeClass } from '../wm/sizeClass';
import { dispatch, openApp, useDesktop } from '../wm/store';
import { WindowLayer } from '../wm/WindowLayer';
import { useMinuteClock } from './clock';
import styles from './Desktop.module.css';

type User = components['schemas']['UserInfo'];

const TASKBAR_POSITIONS: readonly TaskbarPosition[] = ['bottom', 'top', 'left', 'right'];

/** Where a popover opens from the status area, so it never covers the taskbar. */
const POPOVER_PLACEMENT = {
  bottom: 'top end',
  top: 'bottom end',
  left: 'end bottom',
  right: 'start bottom',
} as const;

type DeviceLayout = components['schemas']['DeviceLayout'];

function useDeviceLabel(): (device: Pick<DeviceLayout, 'browser' | 'os'>) => string {
  const { t } = useTranslation();
  return (device) =>
    t('menu.device', {
      browser: device.browser ?? t('menu.unknownBrowser'),
      os: device.os ?? t('menu.unknownOs'),
    });
}

function ago(iso: string, locale: string): string {
  const format = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' });
  const minutes = Math.round((new Date(iso).getTime() - Date.now()) / 60_000);
  if (Math.abs(minutes) < 60) {
    return format.format(minutes, 'minute');
  }
  const hours = Math.round(minutes / 60);
  return Math.abs(hours) < 48
    ? format.format(hours, 'hour')
    : format.format(Math.round(hours / 24), 'day');
}

/** "Get desktop from ...": the user's other devices with a saved layout, loaded when the submenu opens. */
function DevicesMenu({ onPick }: { onPick: (device: DeviceLayout) => void }) {
  const { t, i18n } = useTranslation();
  const label = useDeviceLabel();
  const devices = useQuery({
    queryKey: ['desktop', 'devices'],
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/desktop/devices', { signal })),
    staleTime: 0,
  });
  return (
    <Menu
      aria-label={t('menu.getDesktop')}
      onAction={(key) => {
        const device = devices.data?.find((entry) => entry.deviceId === key);
        if (device !== undefined) {
          onPick(device);
        }
      }}
    >
      {devices.data?.length === 0 && (
        <MenuItem id="none" isDisabled>
          {t('menu.noDevices')}
        </MenuItem>
      )}
      {devices.data?.map((device) => (
        <MenuItem
          key={device.deviceId}
          id={device.deviceId}
          textValue={label(device)}
          icon={<IconDeviceDesktop size={18} stroke={1.75} />}
        >
          <span className={styles.deviceName}>{label(device)}</span>
          <span className={styles.deviceDetail}>
            {device.screenWidth === null || device.screenHeight === null
              ? t('menu.deviceDetail', {
                  count: device.windowCount,
                  time: ago(device.updatedAt, i18n.language),
                })
              : t('menu.deviceDetailScreen', {
                  // Strings, so the size is not formatted with thousands separators.
                  width: String(device.screenWidth),
                  height: String(device.screenHeight),
                  count: device.windowCount,
                  time: ago(device.updatedAt, i18n.language),
                })}
          </span>
        </MenuItem>
      ))}
    </Menu>
  );
}

function UserMenu({
  trigger,
  name,
  position,
  canMoveTaskbar,
  onTwoFactor,
  onSignOut,
}: {
  trigger: ReactNode;
  name: string;
  position: TaskbarPosition;
  canMoveTaskbar: boolean;
  onTwoFactor: () => void;
  onSignOut: () => void;
}) {
  const { t } = useTranslation();
  const status = useDaemonStatus();
  const label = useDeviceLabel();
  const signOut = useDeliberatePress();
  const copy = useMutation({
    mutationFn: async (device: DeviceLayout) =>
      unwrap(await api.POST('/api/desktop/layout/copy', { body: { deviceId: device.deviceId } })),
    onSuccess: (saved, device) => {
      applySavedLayout(saved);
      notify({ code: 'desktop.copied', params: { device: label(device) } });
    },
    onError: showError,
  });
  return (
    <MenuTrigger>
      {trigger}
      <MenuPopover placement={POPOVER_PLACEMENT[position]}>
        <Menu
          aria-label={t('taskbar.userMenu', { name })}
          onAction={(key) => {
            if (key === 'two-factor') {
              onTwoFactor();
            } else if (key === 'sign-out' && signOut.confirm()) {
              onSignOut();
            }
          }}
        >
          <MenuSection>
            <Header className={styles.menuHeader}>
              <span className={styles.menuName}>{t('menu.signedInAs', { name })}</span>
              <span>{status}</span>
            </Header>
            <MenuItem id="two-factor" icon={<IconShieldLock size={18} stroke={1.75} />}>
              {t('menu.twoFactor')}
            </MenuItem>
            <SubmenuTrigger>
              <MenuItem id="language" icon={<IconLanguage size={18} stroke={1.75} />}>
                {t('menu.language')}
              </MenuItem>
              <MenuPopover>
                <LanguageOptions />
              </MenuPopover>
            </SubmenuTrigger>
            <SubmenuTrigger>
              <MenuItem id="devices" icon={<IconDevices size={18} stroke={1.75} />}>
                {t('menu.getDesktop')}
              </MenuItem>
              <MenuPopover>
                <DevicesMenu
                  onPick={(device) => {
                    copy.mutate(device);
                  }}
                />
              </MenuPopover>
            </SubmenuTrigger>
            {canMoveTaskbar && (
              <SubmenuTrigger>
                <MenuItem id="taskbar" icon={<IconLayoutBottombar size={18} stroke={1.75} />}>
                  {t('menu.taskbar')}
                </MenuItem>
                <MenuPopover>
                  <Menu
                    aria-label={t('menu.taskbar')}
                    selectionMode="single"
                    selectedKeys={[position]}
                    onAction={(key) => {
                      const next = TASKBAR_POSITIONS.find((entry) => entry === key);
                      if (next !== undefined) {
                        dispatch({ type: 'setTaskbar', position: next });
                      }
                    }}
                  >
                    {TASKBAR_POSITIONS.map((entry) => (
                      <MenuItem key={entry} id={entry}>
                        {t(`taskbar.position.${entry}`)}
                      </MenuItem>
                    ))}
                  </Menu>
                </MenuPopover>
              </SubmenuTrigger>
            )}
          </MenuSection>
          <MenuSeparator />
          <MenuItem
            id="sign-out"
            icon={<IconLogout size={18} stroke={1.75} />}
            {...signOut.pressProps}
          >
            {t('menu.signOut')}
          </MenuItem>
        </Menu>
      </MenuPopover>
    </MenuTrigger>
  );
}

/** Keeps the window manager's workspace size in sync with the element windows are placed in. */
function useWorkspaceSize() {
  const workspace = useRef<HTMLElement>(null);
  useLayoutEffect(() => {
    const element = workspace.current;
    if (element === null) {
      return;
    }
    // Only the observer measures: it reports right after layout, while reading the size here would force a
    // layout of its own before the first frame.
    const observer = new ResizeObserver(() => {
      dispatch({
        type: 'setWorkspace',
        size: { width: element.clientWidth, height: element.clientHeight },
      });
    });
    observer.observe(element);
    return () => {
      observer.disconnect();
    };
  }, []);
  return workspace;
}

/** Running windows for the taskbar, in opening order. */
function useRunningApps(): RunningApp[] {
  const windows = useDesktop((state) => state.windows);
  const focused = useDesktop((state) => state.focused);
  const appName = useAppName();
  return Object.values(windows).flatMap((window) => {
    const app = findApp(window.app);
    if (app === undefined) {
      return [];
    }
    return [
      {
        id: window.id,
        name: window.title ?? appName(window.app),
        icon: app.icon,
        tone: app.tone,
        state: window.minimized ? 'minimized' : window.id === focused ? 'focused' : 'open',
      },
    ];
  });
}

/** The desktop after sign-in: wallpaper, app shortcuts, windows and the taskbar with start menu and status area. */
export function Desktop({ user, entering }: { user: User; entering: boolean }) {
  const { t, i18n } = useTranslation();
  const queryClient = useQueryClient();
  const status = useRealtime((state) => state.status);
  const now = useMinuteClock();
  const appName = useAppName();
  const [totpOpen, setTotpOpen] = useState(false);
  const [startOpen, setStartOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [switcherOpen, setSwitcherOpen] = useState(false);
  const startButton = useRef<HTMLButtonElement>(null);
  const startMenu = useRef<HTMLDivElement>(null);
  const switcherButton = useRef<HTMLButtonElement>(null);
  const switcher = useRef<HTMLElement>(null);
  const workspace = useWorkspaceSize();
  useLayoutPersistence();
  const toasts = useToasts((state) => state.items);
  const running = useRunningApps();
  const focused = useDesktop((state) => state.focused);
  const sizeClass = useSizeClass();
  // Phones always keep the taskbar at the bottom, next to the thumb.
  const savedPosition = useDesktop((state) => state.taskbar);
  const taskbarPosition = sizeClass === 'compact' ? 'bottom' : savedPosition;
  const activeApp = useDesktop(
    (state) => state.focused !== null && state.windows[state.focused]?.minimized === false,
  );
  useBackGesture(sizeClass === 'compact' && activeApp);
  const name = user.displayName ?? user.username;

  // When the last window closes or minimizes, keyboard focus would fall to the page body; the start button takes it.
  useEffect(() => {
    const active = document.activeElement;
    // A minimized window stays in the page, hidden; focus inside it is lost just the same.
    const lost =
      active === null || active === document.body || active.closest('[data-minimized]') !== null;
    if (focused === null && lost) {
      // After the frame: focusing recalculates styles, which would delay the first paint of the desktop.
      const frame = requestAnimationFrame(() => {
        startButton.current?.focus();
      });
      return () => {
        cancelAnimationFrame(frame);
      };
    }
  }, [focused]);

  const logout = useMutation({
    mutationFn: async () => {
      // After the sign-out a save is refused; one still waiting for its debounce goes out first.
      await saveLayoutBeforeSignOut();
      unwrap(await api.POST('/api/auth/logout'));
    },
    onSuccess: async () => {
      clearSessionHint();
      await queryClient.resetQueries({ queryKey: queryKeys.session });
    },
    onError: showError,
  });

  function closeStart(returnFocus: boolean) {
    setStartOpen(false);
    setQuery('');
    if (returnFocus) {
      startButton.current?.focus();
    }
  }
  useDismiss(startMenu, startButton, startOpen, (reason) => {
    closeStart(reason === 'escape');
  });
  useDismiss(switcher, switcherButton, switcherOpen, (reason) => {
    setSwitcherOpen(false);
    if (reason === 'escape') {
      switcherButton.current?.focus();
    }
  });

  function open(id: string) {
    closeStart(false);
    openApp(id);
  }

  const apps = coreApps.map((app) => ({
    id: app.id,
    name: appName(app.id),
    icon: app.icon,
    tone: app.tone,
  }));
  const needle = query.trim().toLocaleLowerCase(i18n.language);
  const matches = apps
    .filter((app) => needle !== '' && app.name.toLocaleLowerCase(i18n.language).includes(needle))
    .map((app) => ({ id: app.id, name: app.name, detail: t('start.appDetail'), app }));

  return (
    <AriaLocale>
      <DesktopShell
        entering={entering}
        taskbarPosition={taskbarPosition}
        workspaceRef={workspace}
        taskbar={
          <Taskbar
            position={taskbarPosition}
            apps={running}
            startOpen={startOpen}
            startRef={startButton}
            switcherOpen={switcherOpen}
            switcherRef={switcherButton}
            onSwitcher={() => {
              setSwitcherOpen(!switcherOpen);
            }}
            {...(sizeClass === 'compact'
              ? {
                  onSwipe: (direction: 1 | -1) => {
                    dispatch({ type: 'cycle', direction });
                  },
                }
              : {})}
            onStart={() => {
              if (startOpen) {
                closeStart(false);
              } else {
                setStartOpen(true);
              }
            }}
            onApp={(id) => {
              dispatch(id === focused ? { type: 'minimize', id } : { type: 'focus', id });
            }}
            jobs={0}
            unread={0}
            now={now}
            locale={i18n.language}
            user={{ name, initials: initialsOf(name) }}
            userMenu={(trigger) => (
              <UserMenu
                trigger={trigger}
                name={name}
                position={taskbarPosition}
                canMoveTaskbar={sizeClass !== 'compact'}
                onTwoFactor={() => {
                  setTotpOpen(true);
                }}
                onSignOut={() => {
                  logout.mutate();
                }}
              />
            )}
          />
        }
        overlays={
          <>
            {startOpen && (
              <StartMenu
                menuRef={startMenu}
                position={taskbarPosition}
                query={query}
                onQueryChange={setQuery}
                pinned={apps}
                recent={[]}
                results={{ apps: matches, settings: [], files: [] }}
                user={{
                  name,
                  initials: initialsOf(name),
                  role: user.isAdmin ? t('roles.administrator') : t('roles.user'),
                }}
                power={false}
                onOpen={open}
                onSignOut={() => {
                  closeStart(false);
                  logout.mutate();
                }}
              />
            )}
            <ToastStack
              position={taskbarPosition}
              locale={i18n.language}
              items={toasts.map((toast) => ({
                id: toast.id,
                app: { icon: IconDeviceDesktop, tone: 'petrol', name: t('brand') },
                message: toast.message,
                minutesAgo: 0,
              }))}
              onDismiss={dismissToast}
            />
            {switcherOpen && (
              <AppSwitcher
                switcherRef={switcher}
                position={taskbarPosition}
                cards={running.map((entry) => ({ id: entry.id, app: entry, title: entry.name }))}
                onOpen={(id) => {
                  setSwitcherOpen(false);
                  dispatch({ type: 'focus', id });
                }}
                onClose={(id) => {
                  dispatch({ type: 'close', id });
                }}
                onCloseAll={() => {
                  setSwitcherOpen(false);
                  dispatch({ type: 'closeAll' });
                }}
              />
            )}
            {status === 'reconnecting' && (
              <p className={styles.banner} role="status">
                {t('realtime.reconnecting')}
              </p>
            )}
            {status === 'stopped' && (
              <p className={cx(styles.banner, styles.danger)} role="alert">
                {t('realtime.stopped')}
              </p>
            )}
            <TotpSetup
              user={user}
              isOpen={totpOpen}
              onClose={() => {
                setTotpOpen(false);
              }}
            />
          </>
        }
      >
        <DesktopItems>
          {apps.map((app) => (
            <DesktopIcon
              key={app.id}
              name={app.name}
              item={{ kind: 'shortcut', app }}
              onOpen={() => {
                openApp(app.id);
              }}
            />
          ))}
        </DesktopItems>
        <WindowLayer />
      </DesktopShell>
    </AriaLocale>
  );
}
