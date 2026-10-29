import { IconPalette, IconUserCog } from '../ui/icons';
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
import { coreAppIds } from '../apps/ids';
import { initialsOf } from '../auth/parts';
import { clearSessionHint } from '../auth/sessionHint';
import { ThemeOptions } from './ThemeOptions';
import { useDaemonStatus } from '../daemonStatus';
import { showError } from '../errors/errorStore';
import { useRealtime } from '../realtime/connection';
import { AppSwitcher } from '../shell/AppSwitcher';
import { DesktopShell } from '../shell/DesktopShell';
import { itemsQuery, settleItemWrites } from '../items/query';
import { listenForMetaTap } from '../shell/metaTap';
import { bannerAlert, useStorageAlerts } from '../shell/storageAlerts';
import { useReportLanguage } from '../notifications/language';
import { BoxStatus } from '../shell/BoxStatus';
import { StartMenu } from '../shell/StartMenu';
import { StartTiles } from '../shell/StartTiles';
import { showInFiles } from '../items/fileTargets';
import { Taskbar } from '../shell/Taskbar';
import type { RunningApp, TaskbarPosition } from '../shell/types';
import { setNotificationPanelOpen } from '../shell/toasts';
import {
  followNotifications,
  markAllRead,
  useRunningJobs,
  useUnreadNotifications,
} from '../shell/notificationFeed';
import { useDismiss } from '../shell/useDismiss';
import { cx, Menu, MenuItem, MenuPopover, MenuTrigger, UiProvider } from '@pithyx/ui';
import { keepsBrowserMenu } from '@pithyx/sdk/host';
import { useBackGesture } from '../wm/backGesture';
import { saveLayoutBeforeSignOut, useLayoutPersistence } from '../wm/persistence';
import { useSizeClass } from '../wm/sizeClass';
import { dispatch, openApp, useDesktop } from '../wm/store';
import { useTopic } from '../realtime/useTopic';
import type { PowerAction } from './PowerDialog';
import { announce } from '../wm/announce';
import { requestClose, requestCloseAll } from '../wm/commands';
import { WindowLayer } from '../wm/WindowLayer';
import { useMinuteClock } from './clock';
import styles from './Desktop.module.css';
import { loadTranslations, useFormatLocale } from '../i18n';
import { trustHintDismissed, trustQuery } from '../security/trust';

type User = components['schemas']['UserInfo'];

/** Where a popover opens from the status area, so it never covers the taskbar. */
// The account menu opens from the profile in the start menu, which sits next to the taskbar.
const POPOVER_PLACEMENT = {
  bottom: 'top start',
  top: 'bottom start',
  left: 'top start',
  right: 'top start',
} as const;

/** The account menu: who is signed in, Personal settings (D392) and the appearance, which people switch often. */
function UserMenu({
  trigger,
  name,
  position,
  onPreferences,
}: {
  trigger: ReactNode;
  name: string;
  position: TaskbarPosition;
  onPreferences: () => void;
}) {
  const { t } = useTranslation();
  const status = useDaemonStatus();
  return (
    <MenuTrigger>
      {trigger}
      <MenuPopover placement={POPOVER_PLACEMENT[position]}>
        <Menu
          aria-label={t('start.userMenu', { name })}
          onAction={(key) => {
            if (key === 'preferences') {
              onPreferences();
            }
          }}
        >
          <MenuSection>
            <Header className={styles.menuHeader}>
              <span className={styles.menuName}>{t('menu.signedInAs', { name })}</span>
              <span>{status}</span>
            </Header>
            <MenuItem id="preferences" icon={<IconUserCog size={18} stroke={1.75} />}>
              {t('menu.preferences')}
            </MenuItem>
            <SubmenuTrigger>
              <MenuItem id="theme" icon={<IconPalette size={18} stroke={1.75} />}>
                {t('menu.theme')}
              </MenuItem>
              <MenuPopover>
                <ThemeOptions />
              </MenuPopover>
            </SubmenuTrigger>
          </MenuSection>
        </Menu>
      </MenuPopover>
    </MenuTrigger>
  );
}

// The desktop items (icons, notes, their menus and gestures) are a chunk of their own that starts loading together with
// this one and shows once it and the items query are there; the taskbar and windows never wait for it.
// The confirmation of a restart or shutdown loads when it is opened, not with the desktop.
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
// The trust steps load with their texts when opened; the hint only on a device that does not trust the box.
const TrustDialog = lazy(() =>
  Promise.all([import('../security/TrustDialog'), loadTranslations('security')]).then(
    ([{ TrustDialog }]) => ({ default: TrustDialog }),
  ),
);
const TrustHint = lazy(() =>
  Promise.all([import('../security/TrustHint'), loadTranslations('security')]).then(
    ([{ TrustHint }]) => ({ default: TrustHint }),
  ),
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
  useReportLanguage(user.id, i18n.language);
  const status = useRealtime((state) => state.status);
  const now = useMinuteClock();
  const appName = useAppName();
  const [trustOpen, setTrustOpen] = useState(false);
  const trust = useQuery(trustQuery);
  const untrusted = trust.data === 'untrusted';
  const [showTrustHint] = useState(() => !trustHintDismissed());
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
  // The desktop is one surface with its own menus: a right-click never opens the browser's menu, except in text
  // fields. Popovers render in portals on the body, so the listener sits on the document.
  useEffect(() => {
    const onContextMenu = (event: MouseEvent) => {
      if (!keepsBrowserMenu(event.target)) {
        event.preventDefault();
      }
    };
    document.addEventListener('contextmenu', onContextMenu);
    return () => {
      document.removeEventListener('contextmenu', onContextMenu);
    };
  }, []);

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
  // The menu's app list (D394): System only for administrators, then the other apps, then what is the user's own.
  const coreIds: ReadonlySet<string> = new Set(Object.values(coreAppIds));
  const own = (id: string) => id === coreAppIds.preferences;
  const system = (id: string) => permissions.admin && coreIds.has(id) && !own(id);
  const startGroups = [
    { id: 'system', title: t('start.system'), apps: apps.filter((app) => system(app.id)) },
    {
      id: 'apps',
      title: t('start.apps'),
      apps: apps.filter((app) => !system(app.id) && !own(app.id)),
    },
    { id: 'you', title: t('start.you'), apps: apps.filter((app) => own(app.id)) },
  ];
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
            {...(permissions.admin
              ? {
                  boxStatus: (
                    <BoxStatus
                      locale={locale}
                      onOpen={(app) => {
                        open(app === 'storage' ? coreAppIds.storage : coreAppIds.monitor);
                      }}
                    />
                  ),
                }
              : {})}
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
                groups={startGroups}
                results={{ apps: matches, settings: [], files: [] }}
                tiles={
                  <StartTiles
                    isAdmin={permissions.admin}
                    locale={locale}
                    onOpen={(app) => {
                      open(coreAppIds[app]);
                    }}
                    onFolder={(share) => {
                      closeStart(false);
                      showInFiles(share, '');
                    }}
                  />
                }
                onHome={() => {
                  closeStart(false);
                  openApp(coreAppIds.files);
                }}
                onPreferences={() => {
                  open(coreAppIds.preferences);
                }}
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
                    onPreferences={() => {
                      open(coreAppIds.preferences);
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
            {untrusted && showTrustHint && !trustOpen && (
              <Suspense>
                <TrustHint
                  onTrust={() => {
                    setTrustOpen(true);
                  }}
                />
              </Suspense>
            )}
            {trustOpen && (
              <Suspense>
                <TrustDialog
                  onClose={() => {
                    setTrustOpen(false);
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
