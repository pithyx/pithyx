import {
  IconApps,
  IconColorSwatch,
  IconDeviceDesktop,
  IconDevices,
  IconLanguage,
  IconLayoutBottombar,
  IconPalette,
  IconShieldLock,
  IconWallpaper,
} from '../ui/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap, type components } from '@pithyx/api-client';
import {
  lazy,
  Suspense,
  useEffect,
  useEffectEvent,
  useLayoutEffect,
  useRef,
  useState,
  useSyncExternalStore,
  type ReactNode,
} from 'react';
import { Header, MenuSection, SubmenuTrigger } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import { queryKeys } from '../api/queries';
import { runBeforeUnload } from '../apps/lifecycle';
import { useAppName } from '../apps/names';
import { isAppAllowed, setAppPermissions } from '../apps/access';
import { setInstalledApps, useInstalledApps } from '../apps/installed';
import { AppHostLayer } from '../apps/host/AppHostLayer';
import { resetHost, useHost } from '../apps/host/store';
import { appsFor, findApp } from '../apps/registry';
import { initialsOf } from '../auth/parts';
import { clearSessionHint } from '../auth/sessionHint';
import { LanguageOptions } from './LanguageOptions';
import { ThemeOptions } from './ThemeOptions';
import { AccentOptions, WallpaperOptions } from './AppearanceOptions';
import { useDaemonStatus } from '../daemonStatus';
import { showError } from '../errors/errorStore';
import { useRealtime } from '../realtime/connection';
import { AppSwitcher } from '../shell/AppSwitcher';
import { DesktopShell } from '../shell/DesktopShell';
import { itemsQuery, settleItemWrites } from '../items/query';
import { listenForMetaTap } from '../shell/metaTap';
import { bannerAlert, useStorageAlerts } from '../shell/storageAlerts';
import { StartMenu } from '../shell/StartMenu';
import { Taskbar } from '../shell/Taskbar';
import type { RunningApp, TaskbarPosition } from '../shell/types';
import { notify, setNotificationPanelOpen } from '../shell/toasts';
import {
  followNotifications,
  markAllRead,
  useRunningJobs,
  useUnreadNotifications,
} from '../shell/notificationFeed';
import { useDismiss } from '../shell/useDismiss';
import { cx, Menu, MenuItem, MenuPopover, MenuTrigger, UiProvider } from '@pithyx/ui';
import { useBackGesture } from '../wm/backGesture';
import { applySavedLayout, saveLayoutBeforeSignOut, useLayoutPersistence } from '../wm/persistence';
import { useSizeClass } from '../wm/sizeClass';
import { dispatch, openApp, useDesktop } from '../wm/store';
import { useTopic } from '../realtime/useTopic';
import type { PowerAction } from './PowerDialog';
import { announce } from '../wm/announce';
import { requestClose, requestCloseAll } from '../wm/commands';
import { WindowLayer } from '../wm/WindowLayer';
import { useMinuteClock } from './clock';
import styles from './Desktop.module.css';
import { useFormatLocale } from '../i18n';

type User = components['schemas']['UserInfo'];

const TASKBAR_POSITIONS: readonly TaskbarPosition[] = ['bottom', 'top', 'left', 'right'];

/** Where a popover opens from the status area, so it never covers the taskbar. */
// The account menu opens from the profile in the start menu, which sits next to the taskbar.
const POPOVER_PLACEMENT = {
  bottom: 'top start',
  top: 'bottom start',
  left: 'top start',
  right: 'top start',
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
  const { t } = useTranslation();
  const locale = useFormatLocale();
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
                  time: ago(device.updatedAt, locale),
                })
              : t('menu.deviceDetailScreen', {
                  // Strings, so the size is not formatted with thousands separators.
                  width: String(device.screenWidth),
                  height: String(device.screenHeight),
                  count: device.windowCount,
                  time: ago(device.updatedAt, locale),
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
  onFileDefaults,
  onDone,
}: {
  trigger: ReactNode;
  name: string;
  position: TaskbarPosition;
  canMoveTaskbar: boolean;
  onTwoFactor: () => void;
  onFileDefaults: () => void;
  /** After a choice that changes the desktop (another layout, a taskbar position). */
  onDone: () => void;
}) {
  const { t } = useTranslation();
  const status = useDaemonStatus();
  const label = useDeviceLabel();
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
          aria-label={t('start.userMenu', { name })}
          onAction={(key) => {
            if (key === 'two-factor') {
              onTwoFactor();
            } else if (key === 'file-defaults') {
              onFileDefaults();
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
              <MenuItem id="theme" icon={<IconPalette size={18} stroke={1.75} />}>
                {t('menu.theme')}
              </MenuItem>
              <MenuPopover>
                <ThemeOptions />
              </MenuPopover>
            </SubmenuTrigger>
            <SubmenuTrigger>
              <MenuItem id="accent" icon={<IconColorSwatch size={18} stroke={1.75} />}>
                {t('menu.accent')}
              </MenuItem>
              <MenuPopover>
                <AccentOptions />
              </MenuPopover>
            </SubmenuTrigger>
            <SubmenuTrigger>
              <MenuItem id="wallpaper" icon={<IconWallpaper size={18} stroke={1.75} />}>
                {t('menu.wallpaper')}
              </MenuItem>
              <MenuPopover>
                <WallpaperOptions />
              </MenuPopover>
            </SubmenuTrigger>
            <MenuItem id="file-defaults" icon={<IconApps size={18} stroke={1.75} />}>
              {t('menu.fileDefaults')}
            </MenuItem>
            <SubmenuTrigger>
              <MenuItem id="devices" icon={<IconDevices size={18} stroke={1.75} />}>
                {t('menu.getDesktop')}
              </MenuItem>
              <MenuPopover>
                <DevicesMenu
                  onPick={(device) => {
                    copy.mutate(device);
                    onDone();
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
                        onDone();
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
        </Menu>
      </MenuPopover>
    </MenuTrigger>
  );
}

// The desktop items (icons, notes, their menus and gestures) are a chunk of their own that starts loading together with
// this one and shows once it and the items query are there; the taskbar and windows never wait for it.
// Setting up two-factor sign-in is rare; its dialog loads when it is opened, not with the desktop. So does the
// confirmation of a restart or shutdown.
// The storage alert banner loads when an administrator has a critical alert. The toasts and the notification panel
// are a chunk of their own that starts loading with the first render, so the toasts' live region is in the page
// before the first toast; the taskbar and windows never wait for it.
const AlertBanner = lazy(() =>
  import('../shell/StorageAlertViews').then(({ AlertBanner }) => ({ default: AlertBanner })),
);
const NotificationCenter = lazy(() =>
  import('../shell/NotificationCenter').then(({ NotificationCenter }) => ({
    default: NotificationCenter,
  })),
);
const NotificationToasts = lazy(() =>
  import('../shell/NotificationCenter').then(({ NotificationToasts }) => ({
    default: NotificationToasts,
  })),
);
const TotpSetup = lazy(() =>
  import('../auth/TotpSetup').then(({ TotpSetup }) => ({ default: TotpSetup })),
);
const FileDefaultsDialog = lazy(() =>
  import('./FileDefaultsDialog').then(({ FileDefaultsDialog }) => ({
    default: FileDefaultsDialog,
  })),
);
const PowerDialog = lazy(() =>
  import('./PowerDialog').then(({ PowerDialog }) => ({ default: PowerDialog })),
);

type ItemsModule = typeof import('../items/ItemsLayer');
let itemsModule: ItemsModule | undefined;
const itemsListeners = new Set<() => void>();
void import('../items/ItemsLayer').then((module) => {
  itemsModule = module;
  for (const listener of itemsListeners) {
    listener();
  }
});

function useItemsModule(): ItemsModule | undefined {
  return useSyncExternalStore(
    (listener) => {
      itemsListeners.add(listener);
      return () => {
        itemsListeners.delete(listener);
      };
    },
    () => itemsModule,
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

/** Running windows for the taskbar, in opening order, with the badges and attention of their apps. */
function useRunningApps(): RunningApp[] {
  const windows = useDesktop((state) => state.windows);
  const focused = useDesktop((state) => state.focused);
  const badges = useHost((state) => state.badges);
  const attention = useHost((state) => state.attention);
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
        badge: badges[window.app] ?? 0,
        attention: attention[window.id] === true,
      },
    ];
  });
}

/**
 * Puts the rights the daemon pushes on `session/permissions` into the session. Its own component, so the snapshot
 * every subscription starts with re-renders only this one when the rights are the same as at sign-in.
 */
function PermissionsSync() {
  const queryClient = useQueryClient();
  const permissions = useTopic('session/permissions');
  useLayoutEffect(() => {
    if (permissions === undefined) {
      return;
    }
    queryClient.setQueryData<User>(queryKeys.session, (current) =>
      current === undefined || JSON.stringify(current.permissions) === JSON.stringify(permissions)
        ? current
        : { ...current, isAdmin: permissions.admin, permissions },
    );
  }, [permissions, queryClient]);
  return null;
}

/**
 * Puts the apps the daemon lists for the user on `apps` into the registry's store, which the menu, the window manager
 * and the layout restore read outside React too. Its own component for the same reason as PermissionsSync.
 */
function InstalledAppsSync() {
  const apps = useTopic('apps');
  useLayoutEffect(() => {
    if (apps !== undefined) {
      setInstalledApps(apps.apps);
    }
  }, [apps]);
  useLayoutEffect(
    () => () => {
      setInstalledApps(undefined);
    },
    [],
  );
  return null;
}

/** The desktop after sign-in: wallpaper, app shortcuts, windows and the taskbar with start menu and status area. */
export function Desktop({ user, entering }: { user: User; entering: boolean }) {
  const { t, i18n } = useTranslation();
  const locale = useFormatLocale();
  const queryClient = useQueryClient();
  const permissions = user.permissions;
  const status = useRealtime((state) => state.status);
  const now = useMinuteClock();
  const appName = useAppName();
  const [totpOpen, setTotpOpen] = useState(false);
  const [fileDefaultsOpen, setFileDefaultsOpen] = useState(false);
  const [startOpen, setStartOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [switcherOpen, setSwitcherOpen] = useState(false);
  const [notificationsOpen, setNotificationsOpen] = useState(false);
  const [powerAction, setPowerAction] = useState<PowerAction | null>(null);
  // The toast store lives outside React; it hides notification toasts while the panel shows them.
  useEffect(() => {
    setNotificationPanelOpen(notificationsOpen);
    return () => {
      setNotificationPanelOpen(false);
    };
  }, [notificationsOpen]);
  const notificationsButton = useRef<HTMLButtonElement>(null);
  const notificationsPanel = useRef<HTMLElement>(null);
  const alerts = useStorageAlerts(permissions.admin);
  const banner = bannerAlert(alerts);
  const startButton = useRef<HTMLButtonElement>(null);
  const startMenu = useRef<HTMLDivElement>(null);
  const switcherButton = useRef<HTMLButtonElement>(null);
  const switcher = useRef<HTMLElement>(null);
  const workspace = useWorkspaceSize();
  // Before the layout is restored: windows of apps the user may not open are skipped, and a right taken away (or an
  // app uninstalled, disabled or no longer granted) closes the app's windows and leaves the start menu at once.
  const installed = useInstalledApps((state) => state.apps);
  useLayoutEffect(() => {
    setAppPermissions(permissions);
    for (const window of Object.values(useDesktop.getState().windows)) {
      // Until the topic's first snapshot, an installed app is not known yet rather than gone.
      const unknown = installed === undefined && findApp(window.app) === undefined;
      if (!unknown && !isAppAllowed(window.app, permissions)) {
        dispatch({ type: 'close', id: window.id });
      }
    }
  }, [permissions, installed]);
  useLayoutPersistence();
  useQuery(itemsQuery);
  const items = useItemsModule();
  const unreadNotifications = useUnreadNotifications();
  const runningJobs = useRunningJobs();
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
  // Only then: after loading, nothing has focus until the user acts (Tab reaches the start button first).
  const hadFocusedWindow = useRef(false);
  useEffect(() => {
    const closedLastWindow = hadFocusedWindow.current && focused === null;
    hadFocusedWindow.current = focused !== null;
    const active = document.activeElement;
    // A minimized window stays in the page, hidden; focus inside it is lost just the same.
    const lost =
      active === null || active === document.body || active.closest('[data-minimized]') !== null;
    if (closedLastWindow && lost) {
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
      // Apps may ask first (unsaved changes). Then the windows close, so the next sign-in starts with an empty
      // desktop; the layout without them is saved before the sign-out, after which a save is refused.
      await runBeforeUnload('logout');
      dispatch({ type: 'closeAll' });
      resetHost();
      await Promise.all([saveLayoutBeforeSignOut(), settleItemWrites()]);
      unwrap(await api.POST('/api/auth/logout'));
    },
    onSuccess: async () => {
      clearSessionHint();
      await queryClient.resetQueries({ queryKey: queryKeys.session });
    },
    onError: showError,
  });

  useEffect(() => followNotifications(), []);

  // Like the Windows key opens Start, a tap on the Meta key opens and closes the Pithyx menu.
  const toggleStart = useEffectEvent(() => {
    if (startOpen) {
      closeStart(true);
    } else {
      setStartOpen(true);
    }
  });
  useEffect(
    () =>
      listenForMetaTap(() => {
        toggleStart();
      }),
    [],
  );

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
  useDismiss(notificationsPanel, notificationsButton, notificationsOpen, (reason) => {
    setNotificationsOpen(false);
    if (reason === 'escape') {
      notificationsButton.current?.focus();
    }
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

  const addToDesktop =
    items === undefined
      ? undefined
      : (app: string) => {
          items.addShortcut(queryClient, app);
          announce(t('taskbar.shortcutAdded', { name: appName(app) }));
        };

  const apps = appsFor((id) => isAppAllowed(id, permissions)).map((app) => ({
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
    <UiProvider locale={locale}>
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
            canAddToDesktop={addToDesktop !== undefined}
            onAppAction={(id, action) => {
              if (action === 'close') {
                void requestClose(id);
              } else if (action === 'addToDesktop') {
                const app = useDesktop.getState().windows[id]?.app;
                if (app !== undefined) {
                  addToDesktop?.(app);
                }
              } else {
                dispatch(action === 'minimize' ? { type: 'minimize', id } : { type: 'focus', id });
              }
            }}
            jobs={runningJobs}
            unread={alerts.filter((alert) => !alert.muted).length + unreadNotifications}
            notificationsOpen={notificationsOpen}
            notificationsRef={notificationsButton}
            onNotifications={() => {
              if (!notificationsOpen && unreadNotifications > 0) {
                void markAllRead();
              }
              setNotificationsOpen(!notificationsOpen);
            }}
            now={now}
            locale={locale}
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
                apps={apps}
                recent={[]}
                results={{ apps: matches, settings: [], files: [] }}
                user={{
                  name,
                  initials: initialsOf(name),
                  role: permissions.admin ? t('roles.administrator') : t('roles.user'),
                }}
                userMenu={(trigger) => (
                  <UserMenu
                    trigger={trigger}
                    name={name}
                    position={taskbarPosition}
                    canMoveTaskbar={sizeClass !== 'compact'}
                    onTwoFactor={() => {
                      closeStart(false);
                      setTotpOpen(true);
                    }}
                    onFileDefaults={() => {
                      closeStart(false);
                      setFileDefaultsOpen(true);
                    }}
                    onDone={() => {
                      closeStart(false);
                    }}
                  />
                )}
                power={permissions.admin || permissions.rights.includes('right:power')}
                onPower={(action) => {
                  closeStart(false);
                  setPowerAction(action);
                }}
                onOpen={open}
                {...(addToDesktop === undefined ? {} : { onAddToDesktop: addToDesktop })}
                onSignOut={() => {
                  closeStart(false);
                  logout.mutate();
                }}
              />
            )}
            <Suspense>
              <NotificationToasts position={taskbarPosition} locale={locale} />
            </Suspense>
            <PermissionsSync />
            <InstalledAppsSync />
            <AppHostLayer />
            {powerAction !== null && (
              <Suspense>
                <PowerDialog
                  action={powerAction}
                  onClose={() => {
                    setPowerAction(null);
                  }}
                />
              </Suspense>
            )}
            {switcherOpen && (
              <AppSwitcher
                switcherRef={switcher}
                position={taskbarPosition}
                cards={running.map((entry) => ({
                  id: entry.id,
                  app: entry,
                  title: entry.name,
                  ...(entry.badge === undefined ? {} : { badge: entry.badge }),
                }))}
                onOpen={(id) => {
                  setSwitcherOpen(false);
                  dispatch({ type: 'focus', id });
                }}
                onClose={(id) => {
                  void requestClose(id);
                }}
                onCloseAll={() => {
                  setSwitcherOpen(false);
                  void requestCloseAll();
                }}
              />
            )}
            {notificationsOpen && (
              <Suspense>
                <NotificationCenter
                  panelRef={notificationsPanel}
                  alerts={alerts}
                  now={now}
                  locale={locale}
                  position={taskbarPosition}
                />
              </Suspense>
            )}
            {banner !== undefined && status === 'open' && (
              <Suspense>
                <AlertBanner alert={banner} />
              </Suspense>
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
            {fileDefaultsOpen && (
              <Suspense>
                <FileDefaultsDialog
                  onClose={() => {
                    setFileDefaultsOpen(false);
                  }}
                />
              </Suspense>
            )}
            {totpOpen && (
              <Suspense>
                <TotpSetup
                  user={user}
                  isOpen
                  onClose={() => {
                    setTotpOpen(false);
                  }}
                />
              </Suspense>
            )}
          </>
        }
      >
        {items !== undefined && <items.ItemsLayer />}
        <WindowLayer />
      </DesktopShell>
    </UiProvider>
  );
}
