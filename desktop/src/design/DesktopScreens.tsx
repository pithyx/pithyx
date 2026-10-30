import {
  IconAlertTriangle,
  IconLogout,
  IconNetwork,
  IconPalette,
  IconShieldLock,
  IconUserCog,
} from '../ui/icons';
import type { ReactNode } from 'react';
import { Header, MenuSection } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { AppSwitcher } from '../shell/AppSwitcher';
import { DesktopIcon } from '../shell/DesktopIcon';
import { DesktopItems, DesktopShell } from '../shell/DesktopShell';
import { JobList } from '../shell/JobList';
import { NotificationPanel } from '../shell/NotificationPanel';
import { ToastStack } from '../shell/Notifications';
import { StartMenu, type StartResults } from '../shell/StartMenu';
import { FolderTiles, RecentTiles, StatusTiles } from '../shell/StartTiles';
import { StickyNote } from '../shell/StickyNote';
import { Taskbar } from '../shell/Taskbar';
import type { RunningApp, TaskbarPosition } from '../shell/types';
import { Window, type WindowProps } from '../shell/Window';
import {
  Button,
  cx,
  Dialog,
  Menu,
  MenuItem,
  MenuPopover,
  MenuSeparator,
  MenuTrigger,
} from '@pithyx/ui';
import styles from './DesktopScreens.module.css';
import { LiveFiles, LiveLogs, LiveSettings } from './LiveApps';
import {
  currentFolder,
  desktopItems,
  desktopShortcuts,
  jobs,
  noteText,
  notifications,
  now,
  startApps,
  startPools,
  startRecent,
  startSample,
  startShares,
  startUpdates,
  searchFiles,
  searchQuery,
  useApps,
  user,
  type Apps,
} from './sampleData';

export interface SceneContext {
  taskbar: TaskbarPosition;
  locale: string;
  accent: string;
}

type WindowSet = 'none' | 'three' | 'maximized';

interface SceneProps {
  context: SceneContext;
  windows?: WindowSet;
  items?: boolean;
  overlays?: ReactNode;
  startOpen?: boolean;
  notificationsOpen?: boolean;
  switcherOpen?: boolean;
  entering?: boolean;
}

function runningApps(windows: WindowSet, apps: Apps): RunningApp[] {
  if (windows === 'three') {
    return [
      { ...apps.files, state: 'focused' },
      { ...apps.settings, state: 'open' },
      { ...apps.logs, state: 'open' },
    ];
  }
  if (windows === 'maximized') {
    return [
      { ...apps.settings, state: 'focused' },
      { ...apps.files, state: 'minimized' },
    ];
  }
  return [];
}

function useFilesTitle(): string {
  const { t } = useTranslation('files');
  return t('windowTitle', { folder: currentFolder });
}

function windowsFor(set: WindowSet, apps: Apps, filesTitle: string): WindowProps[] {
  if (set === 'three') {
    return [
      {
        app: apps.logs,
        title: apps.logs.name,
        focused: false,
        geometry: { x: 8, y: 40, width: 38, height: 50 },
        medium: 'hidden',
        layer: 1,
        children: <LiveLogs />,
      },
      {
        app: apps.settings,
        title: apps.settings.name,
        focused: false,
        geometry: { x: 52, y: 25, width: 46, height: 70 },
        medium: 'right',
        layer: 2,
        children: <LiveSettings />,
      },
      {
        app: apps.files,
        title: filesTitle,
        focused: true,
        geometry: { x: 7, y: 3, width: 58, height: 70 },
        medium: 'left',
        compactActive: true,
        layer: 3,
        children: <LiveFiles />,
      },
    ];
  }
  if (set === 'maximized') {
    return [
      {
        app: apps.files,
        title: filesTitle,
        focused: false,
        minimized: true,
        geometry: { x: 10, y: 10, width: 50, height: 60 },
        layer: 1,
        children: <LiveFiles />,
      },
      {
        app: apps.settings,
        title: apps.settings.name,
        focused: true,
        maximized: true,
        geometry: { x: 0, y: 0, width: 100, height: 100 },
        compactActive: true,
        layer: 2,
        children: <LiveSettings page="users" />,
      },
    ];
  }
  return [];
}

function useUser() {
  const { t } = useTranslation();
  return { ...user, role: t('roles.administrator') };
}

function UserMenu({ trigger, open }: { trigger: ReactNode; open: boolean }) {
  const { t } = useTranslation();
  return (
    <MenuTrigger defaultOpen={open}>
      {trigger}
      <MenuPopover placement="end top">
        <Menu aria-label={t('start.userMenu', { name: user.name })}>
          <MenuSection>
            <Header className={styles.menuHeader}>
              {t('menu.signedInAs', { name: user.name })}
            </Header>
            <MenuItem icon={<IconUserCog size={18} stroke={1.75} />}>
              {t('menu.preferences')}
            </MenuItem>
            <MenuItem icon={<IconPalette size={18} stroke={1.75} />}>{t('menu.theme')}</MenuItem>
          </MenuSection>
          <MenuSeparator />
          <MenuItem icon={<IconLogout size={18} stroke={1.75} />}>{t('menu.signOut')}</MenuItem>
        </Menu>
      </MenuPopover>
    </MenuTrigger>
  );
}

export function DesktopScene({
  context,
  windows = 'none',
  items = true,
  overlays,
  startOpen = false,
  notificationsOpen = false,
  switcherOpen = false,
  entering = false,
}: SceneProps) {
  const apps = useApps();
  const filesTitle = useFilesTitle();
  return (
    <DesktopShell
      taskbarPosition={context.taskbar}
      entering={entering}
      taskbar={
        <Taskbar
          position={context.taskbar}
          apps={runningApps(windows, apps)}
          startOpen={startOpen}
          jobs={windows === 'none' ? 0 : jobs.length}
          unread={windows === 'none' ? 0 : 2}
          notificationsOpen={notificationsOpen}
          switcherOpen={switcherOpen}
          now={now}
          locale={context.locale}
        />
      }
      overlays={overlays}
    >
      {items && (
        <>
          <DesktopItems>
            {desktopItems.map((entry, index) => (
              <DesktopIcon
                key={entry.name}
                name={entry.name}
                item={entry.item}
                selected={index === 1}
              />
            ))}
            {desktopShortcuts.map((id) => (
              <DesktopIcon
                key={id}
                name={apps[id].name}
                item={{ kind: 'shortcut', app: apps[id] }}
              />
            ))}
          </DesktopItems>
          <div className={styles.noteSlot}>
            <StickyNote text={noteText} x={76} y={6} />
          </div>
        </>
      )}
      {windowsFor(windows, apps, filesTitle).map((props) => (
        <Window key={props.title} {...props} />
      ))}
    </DesktopShell>
  );
}

function useSearchResults(apps: Apps): StartResults {
  const { t } = useTranslation();
  const { t: settings } = useTranslation('settings');
  return {
    apps: [
      { id: 's-photos', name: apps.photos.name, detail: t('start.appDetail'), app: apps.photos },
    ],
    settings: [
      {
        id: 's-network',
        name: settings('pages.network.title'),
        detail: t('start.settingsPath', { section: settings('categories.system') }),
        icon: IconNetwork,
      },
      {
        id: 's-users',
        name: settings('pages.users.title'),
        detail: t('start.settingsPath', { section: settings('categories.accounts') }),
        icon: IconShieldLock,
      },
    ],
    files: searchFiles,
  };
}

// The gallery's buttons do nothing.
const ignore = () => undefined;

export function StartMenuScene({
  context,
  search,
  userMenuOpen = false,
}: {
  context: SceneContext;
  search: boolean;
  userMenuOpen?: boolean;
}) {
  const apps = useApps();
  const results = useSearchResults(apps);
  const currentUser = useUser();
  const { t, i18n } = useTranslation();
  const query = search ? searchQuery[i18n.language === 'de' ? 'de' : 'en'] : '';
  return (
    <DesktopScene
      context={context}
      startOpen
      overlays={
        <StartMenu
          position={context.taskbar}
          query={query}
          groups={[
            {
              id: 'system',
              title: t('start.system'),
              apps: startApps.system.map((id) => apps[id]),
            },
            { id: 'apps', title: t('start.apps'), apps: startApps.apps.map((id) => apps[id]) },
            { id: 'you', title: t('start.you'), apps: startApps.you.map((id) => apps[id]) },
          ]}
          results={results}
          tiles={
            <>
              <StatusTiles
                pools={startPools}
                sample={startSample}
                updates={startUpdates}
                locale={context.locale}
                onOpen={ignore}
              />
              <RecentTiles
                files={startRecent}
                locale={context.locale}
                onFile={ignore}
                onAll={ignore}
              />
              <FolderTiles shares={startShares} onFolder={ignore} />
            </>
          }
          onHome={ignore}
          onPreferences={ignore}
          user={currentUser}
          userMenu={(trigger) => <UserMenu trigger={trigger} open={userMenuOpen} />}
        />
      }
    />
  );
}

export function NotificationsScene({ context, panel }: { context: SceneContext; panel: boolean }) {
  const items = notifications(useApps());
  return (
    <DesktopScene
      context={context}
      windows="three"
      notificationsOpen={panel}
      overlays={
        panel ? (
          <NotificationPanel
            jobs={<JobList jobs={jobs} />}
            items={items}
            locale={context.locale}
            position={context.taskbar}
          />
        ) : (
          <ToastStack
            items={items.slice(0, 2)}
            locale={context.locale}
            position={context.taskbar}
          />
        )
      }
    />
  );
}

export function ErrorDialogScene({ context }: { context: SceneContext }) {
  const { t } = useTranslation();
  return (
    <>
      <DesktopScene context={context} windows="three" />
      <Dialog
        isOpen
        role="alertdialog"
        tone="danger"
        icon={<IconAlertTriangle size={22} stroke={1.75} />}
        title={t('error.title')}
        actions={<Button variant="primary">{t('error.close')}</Button>}
      >
        <p>{t('system.internal.unexpected', { ns: 'errors' })}</p>
      </Dialog>
    </>
  );
}

export function SwitcherScene({ context }: { context: SceneContext }) {
  const apps = useApps();
  const filesTitle = useFilesTitle();
  return (
    <DesktopScene
      context={context}
      windows="three"
      switcherOpen
      overlays={
        <AppSwitcher
          position={context.taskbar}
          cards={[
            { id: 'files', app: apps.files, title: filesTitle, preview: <LiveFiles /> },
            {
              id: 'settings',
              app: apps.settings,
              title: apps.settings.name,
              preview: <LiveSettings />,
            },
            { id: 'logs', app: apps.logs, title: apps.logs.name, preview: <LiveLogs /> },
          ]}
        />
      }
    />
  );
}

/** Window states side by side, plus the taskbar item states. */
export function WindowStatesScene({
  context,
  heading,
  specimens: captions,
}: {
  context: SceneContext;
  heading: string;
  specimens: readonly { name: string; caption: string }[];
}) {
  const apps = useApps();
  const specimens: { focused: boolean; maximized?: boolean }[] = [
    { focused: true },
    { focused: false },
    { focused: true, maximized: true },
  ];
  return (
    <main className={styles.states}>
      <h1 className="visually-hidden">{heading}</h1>
      {specimens.map((specimen, index) => (
        <figure key={captions[index]?.name} className={styles.specimen}>
          <div className={styles.stage}>
            <Window
              app={apps.files}
              title={`${apps.files.name}: ${captions[index]?.name ?? ''}`}
              focused={specimen.focused}
              maximized={specimen.maximized ?? false}
              geometry={{ x: 5, y: 7, width: 90, height: 86 }}
              compactActive
              layer={1}
            >
              <LiveFiles />
            </Window>
          </div>
          <figcaption className={styles.caption}>{captions[index]?.caption}</figcaption>
        </figure>
      ))}
      <figure className={cx(styles.specimen, styles.wide)}>
        <div className={styles.taskbarStage}>
          <Taskbar
            position="bottom"
            apps={[
              { ...apps.files, state: 'focused' },
              { ...apps.settings, state: 'open' },
              { ...apps.logs, state: 'minimized' },
            ]}
            jobs={1}
            unread={3}
            now={now}
            locale={context.locale}
          />
        </div>
        <figcaption className={styles.caption}>{captions[3]?.caption}</figcaption>
      </figure>
    </main>
  );
}
