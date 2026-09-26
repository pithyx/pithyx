import {
  IconAlertTriangle,
  IconCopy,
  IconLanguage,
  IconLogout,
  IconPalette,
  IconShieldLock,
} from '@tabler/icons-react';
import type { ReactNode } from 'react';
import { Header, MenuSection } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { AppSwitcher } from '../shell/AppSwitcher';
import { DesktopIcon } from '../shell/DesktopIcon';
import { DesktopItems, DesktopShell } from '../shell/DesktopShell';
import { NotificationPanel, ToastStack } from '../shell/Notifications';
import { StartMenu } from '../shell/StartMenu';
import { StickyNote } from '../shell/StickyNote';
import { Taskbar } from '../shell/Taskbar';
import type { RunningApp, TaskbarPosition } from '../shell/types';
import { Window, type WindowProps } from '../shell/Window';
import { Button, IconButton } from '../ui/Button';
import { Dialog } from '../ui/Dialog';
import { cx } from '../ui/cx';
import { Menu, MenuItem, MenuPopover, MenuSeparator, MenuTrigger } from '../ui/Menu';
import styles from './DesktopScreens.module.css';
import { FilesContent, LogsContent, SettingsContent, StorageContent } from './SampleApps';
import {
  apps,
  desktopItems,
  jobs,
  noteText,
  notifications,
  now,
  pinned,
  recent,
  requestId,
  searchQuery,
  searchResults,
  user,
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
  userMenuOpen?: boolean;
  entering?: boolean;
}

function runningApps(windows: WindowSet): RunningApp[] {
  if (windows === 'three') {
    return [
      { ...apps.files, state: 'focused' },
      { ...apps.storage, state: 'open' },
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

function windowsFor(set: WindowSet, accent: string): WindowProps[] {
  if (set === 'three') {
    return [
      {
        app: apps.logs,
        title: 'Logs',
        focused: false,
        geometry: { x: 8, y: 40, width: 38, height: 50 },
        medium: 'hidden',
        layer: 1,
        children: <LogsContent />,
      },
      {
        app: apps.storage,
        title: 'Storage',
        focused: false,
        geometry: { x: 52, y: 22, width: 40, height: 62 },
        medium: 'right',
        layer: 2,
        children: <StorageContent />,
      },
      {
        app: apps.files,
        title: 'Photos 2026 - Files',
        focused: true,
        geometry: { x: 18, y: 5, width: 50, height: 64 },
        medium: 'left',
        compactActive: true,
        layer: 3,
        children: <FilesContent />,
      },
    ];
  }
  if (set === 'maximized') {
    return [
      {
        app: apps.files,
        title: 'Files',
        focused: false,
        minimized: true,
        geometry: { x: 10, y: 10, width: 50, height: 60 },
        layer: 1,
        children: <FilesContent />,
      },
      {
        app: apps.settings,
        title: 'Settings',
        focused: true,
        maximized: true,
        geometry: { x: 0, y: 0, width: 100, height: 100 },
        compactActive: true,
        layer: 2,
        children: <SettingsContent accent={accent} />,
      },
    ];
  }
  return [];
}

function UserMenu({ trigger, open }: { trigger: ReactNode; open: boolean }) {
  const { t } = useTranslation();
  return (
    <MenuTrigger defaultOpen={open}>
      {trigger}
      <MenuPopover placement="top end">
        <Menu aria-label={t('taskbar.userMenu', { name: user.name })}>
          <MenuSection>
            <Header className={styles.menuHeader}>
              {t('menu.signedInAs', { name: user.name })}
            </Header>
            <MenuItem icon={<IconPalette size={18} stroke={1.75} />}>
              {t('menu.personalization')}
            </MenuItem>
            <MenuItem icon={<IconShieldLock size={18} stroke={1.75} />}>
              {t('menu.twoFactor')}
            </MenuItem>
            <MenuItem icon={<IconLanguage size={18} stroke={1.75} />}>
              {t('menu.language')}
            </MenuItem>
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
  userMenuOpen = false,
  entering = false,
}: SceneProps) {
  return (
    <DesktopShell
      taskbarPosition={context.taskbar}
      entering={entering}
      taskbar={
        <Taskbar
          position={context.taskbar}
          apps={runningApps(windows)}
          startOpen={startOpen}
          jobs={windows === 'none' ? 0 : jobs.length}
          unread={windows === 'none' ? 0 : 2}
          notificationsOpen={notificationsOpen}
          switcherOpen={switcherOpen}
          now={now}
          locale={context.locale}
          user={user}
          userMenu={(trigger) => <UserMenu trigger={trigger} open={userMenuOpen} />}
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
          </DesktopItems>
          <div className={styles.noteSlot}>
            <StickyNote text={noteText} x={76} y={6} />
          </div>
        </>
      )}
      {windowsFor(windows, context.accent).map((props) => (
        <Window key={props.title} {...props} />
      ))}
    </DesktopShell>
  );
}

export function StartMenuScene({ context, query }: { context: SceneContext; query: string }) {
  return (
    <DesktopScene
      context={context}
      startOpen
      overlays={
        <StartMenu
          position={context.taskbar}
          query={query}
          pinned={pinned}
          recent={recent}
          results={searchResults}
          user={user}
        />
      }
    />
  );
}

export const startSearchQuery = searchQuery;

export function NotificationsScene({ context, panel }: { context: SceneContext; panel: boolean }) {
  const items = notifications.map((item) => ({ ...item }));
  return (
    <DesktopScene
      context={context}
      windows="three"
      notificationsOpen={panel}
      overlays={
        panel ? (
          <NotificationPanel
            jobs={jobs}
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
        <div className={styles.requestId}>
          <span className={styles.requestLabel}>{t('error.requestId')}</span>
          <code className={styles.requestValue}>{requestId}</code>
          <IconButton label={t('error.copy')} size="sm">
            <IconCopy size={16} stroke={1.75} />
          </IconButton>
        </div>
      </Dialog>
    </>
  );
}

export function SwitcherScene({ context }: { context: SceneContext }) {
  return (
    <DesktopScene
      context={context}
      windows="three"
      switcherOpen
      overlays={
        <AppSwitcher
          position={context.taskbar}
          cards={[
            {
              id: 'files',
              app: apps.files,
              title: 'Photos 2026 - Files',
              preview: <FilesContent />,
            },
            { id: 'storage', app: apps.storage, title: 'Storage', preview: <StorageContent /> },
            { id: 'logs', app: apps.logs, title: 'Logs', preview: <LogsContent /> },
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
              title={`Files: ${captions[index]?.name ?? ''}`}
              focused={specimen.focused}
              maximized={specimen.maximized ?? false}
              geometry={{ x: 5, y: 7, width: 90, height: 86 }}
              compactActive
              layer={1}
            >
              <FilesContent />
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
              { ...apps.storage, state: 'open' },
              { ...apps.settings, state: 'minimized' },
            ]}
            jobs={1}
            unread={3}
            now={now}
            locale={context.locale}
            user={user}
          />
        </div>
        <figcaption className={styles.caption}>{captions[3]?.caption}</figcaption>
      </figure>
    </main>
  );
}
