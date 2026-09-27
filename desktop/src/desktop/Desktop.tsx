import { IconLanguage, IconLogout, IconShieldLock } from '@tabler/icons-react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { unwrap, type components } from '@vyxis/api-client';
import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { Header, MenuSection, SubmenuTrigger } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import { queryKeys } from '../api/queries';
import { useAppName } from '../apps/names';
import { coreApps, findApp } from '../apps/registry';
import { initialsOf, LanguageOptions } from '../auth/parts';
import { TotpSetup } from '../auth/TotpSetup';
import { useDaemonStatus } from '../daemonStatus';
import { showError } from '../errors/errorStore';
import { realtime, useRealtime } from '../realtime/connection';
import { DesktopIcon } from '../shell/DesktopIcon';
import { DesktopItems, DesktopShell } from '../shell/DesktopShell';
import { StartMenu } from '../shell/StartMenu';
import { Taskbar } from '../shell/Taskbar';
import type { RunningApp } from '../shell/types';
import { useDismiss } from '../shell/useDismiss';
import { cx } from '../ui/cx';
import { Menu, MenuItem, MenuPopover, MenuSeparator, MenuTrigger } from '../ui/Menu';
import { dispatch, openApp, useDesktop } from '../wm/store';
import { WindowLayer } from '../wm/WindowLayer';
import { useMinuteClock } from './clock';
import styles from './Desktop.module.css';

type User = components['schemas']['UserInfo'];

function UserMenu({
  trigger,
  name,
  onTwoFactor,
  onSignOut,
}: {
  trigger: ReactNode;
  name: string;
  onTwoFactor: () => void;
  onSignOut: () => void;
}) {
  const { t } = useTranslation();
  const status = useDaemonStatus();
  return (
    <MenuTrigger>
      {trigger}
      <MenuPopover placement="top end">
        <Menu
          aria-label={t('taskbar.userMenu', { name })}
          onAction={(key) => {
            if (key === 'two-factor') {
              onTwoFactor();
            } else if (key === 'sign-out') {
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
          </MenuSection>
          <MenuSeparator />
          <MenuItem id="sign-out" icon={<IconLogout size={18} stroke={1.75} />}>
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
    const measure = () => {
      dispatch({
        type: 'setWorkspace',
        size: { width: element.clientWidth, height: element.clientHeight },
      });
    };
    measure();
    const observer = new ResizeObserver(measure);
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
  const startButton = useRef<HTMLButtonElement>(null);
  const startMenu = useRef<HTMLDivElement>(null);
  const workspace = useWorkspaceSize();
  const running = useRunningApps();
  const focused = useDesktop((state) => state.focused);
  const taskbarPosition = useDesktop((state) => state.taskbar);
  const name = user.displayName ?? user.username;

  // The WebSocket lives as long as the desktop; a closed session sends the user back to the login screen.
  useEffect(() => {
    realtime.connect(() => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.session });
    });
    return () => {
      realtime.disconnect();
      dispatch({ type: 'closeAll' });
    };
  }, [queryClient]);

  const logout = useMutation({
    mutationFn: async () => {
      unwrap(await api.POST('/api/auth/logout'));
    },
    onSuccess: async () => {
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
  );
}
