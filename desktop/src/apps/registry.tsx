import {
  IconActivity,
  IconApps,
  IconDatabase,
  IconFolders,
  IconInfoCircle,
  IconListDetails,
  IconSearch,
  IconSettings,
  type TablerIcon,
} from '../ui/icons';
import { lazy, type ComponentType, type LazyExoticComponent } from 'react';
import type { AppTone } from '../shell/types';
import type { WindowOptions } from '../wm/types';
import { loadTranslations } from '../i18n';

/** What every app component receives. The SDK replaces this in step 5.3. */
export interface AppProps {
  windowId: string;
}

export interface CoreApp {
  id: string;
  icon: TablerIcon;
  tone: AppTone;
  window: WindowOptions;
  /** Loaded when the first window of the app opens, never at sign-in. */
  Component: LazyExoticComponent<ComponentType<AppProps>>;
  /** Listed only for administrators; the daemon refuses everyone else anyway. */
  adminOnly?: true;
  /** Opened by other apps with a file, never listed in the menu or on the desktop. */
  launchOnly?: true;
}

/**
 * The core apps that run in the desktop context. An internal list until the manifest schema and the SDK (step 5.3)
 * replace it; names come from `apps.<id>` in the desktop namespace.
 */
export const coreApps: readonly CoreApp[] = [
  {
    id: 'files',
    icon: IconFolders,
    tone: 'petrol',
    window: {
      instances: 'multi',
      defaultSize: { width: 920, height: 580 },
      minSize: { width: 320, height: 280 },
    },
    Component: lazy(() =>
      Promise.all([import('./files/FilesWindow'), loadTranslations('files')]).then(
        ([{ FilesWindow }]) => ({
          default: FilesWindow,
        }),
      ),
    ),
  },
  {
    id: 'storage',
    icon: IconDatabase,
    tone: 'ocean',
    adminOnly: true,
    window: {
      instances: 'single',
      defaultSize: { width: 1000, height: 660 },
      minSize: { width: 340, height: 360 },
    },
    Component: lazy(() =>
      Promise.all([import('./storage/StorageWindow'), loadTranslations('storage')]).then(
        ([{ StorageWindow }]) => ({ default: StorageWindow }),
      ),
    ),
  },
  {
    id: 'settings',
    icon: IconSettings,
    tone: 'graphite',
    adminOnly: true,
    window: {
      instances: 'single',
      defaultSize: { width: 960, height: 640 },
      minSize: { width: 340, height: 360 },
    },
    Component: lazy(() =>
      // Settings runs plans with the Storage app's plan dialogs, and their texts; Users is one of its pages.
      Promise.all([
        import('./settings/SettingsWindow'),
        loadTranslations('settings', 'storage', 'users'),
      ]).then(([{ SettingsWindow }]) => ({ default: SettingsWindow })),
    ),
  },
  {
    id: 'monitor',
    icon: IconActivity,
    tone: 'moss',
    adminOnly: true,
    window: {
      instances: 'single',
      defaultSize: { width: 980, height: 680 },
      minSize: { width: 320, height: 320 },
    },
    Component: lazy(() =>
      Promise.all([import('./monitor/MonitorWindow'), loadTranslations('monitor')]).then(
        ([{ MonitorWindow }]) => ({ default: MonitorWindow }),
      ),
    ),
  },
  {
    id: 'logs',
    icon: IconListDetails,
    tone: 'graphite',
    adminOnly: true,
    window: {
      instances: 'single',
      defaultSize: { width: 1040, height: 640 },
      minSize: { width: 320, height: 320 },
    },
    Component: lazy(() =>
      Promise.all([import('./logs/LogsWindow'), loadTranslations('logs')]).then(
        ([{ LogsWindow }]) => ({ default: LogsWindow }),
      ),
    ),
  },
  {
    id: 'viewer',
    icon: IconSearch,
    tone: 'plum',
    launchOnly: true,
    window: {
      instances: 'multi',
      defaultSize: { width: 900, height: 640 },
      minSize: { width: 320, height: 280 },
    },
    Component: lazy(() =>
      Promise.all([import('./viewer/ViewerWindow'), loadTranslations('viewer')]).then(
        ([{ ViewerWindow }]) => ({ default: ViewerWindow }),
      ),
    ),
  },
  {
    id: 'about',
    icon: IconInfoCircle,
    tone: 'graphite',
    window: {
      instances: 'single',
      defaultSize: { width: 560, height: 620 },
      minSize: { width: 320, height: 320 },
    },
    Component: lazy(() =>
      import('./about/AboutApp').then(({ AboutApp }) => ({ default: AboutApp })),
    ),
  },
  {
    // Prototype for the SDK spec: a third-party app in a sandboxed window (apps/demo), until manifests drive this list.
    id: 'demo',
    icon: IconApps,
    tone: 'plum',
    window: {
      instances: 'single',
      defaultSize: { width: 520, height: 560 },
      minSize: { width: 320, height: 320 },
    },
    Component: lazy(() =>
      import('./sandbox/SandboxApp').then(({ SandboxApp }) => ({
        default: ({ windowId }: AppProps) => (
          <SandboxApp
            windowId={windowId}
            app="demo"
            entry="/apps/demo/index.html"
            permissions={['notifications']}
          />
        ),
      })),
    ),
  },
];

/** The apps a user sees in the start menu and can put on the desktop. */
export function appsFor(isAdmin: boolean): readonly CoreApp[] {
  return coreApps.filter((app) => app.launchOnly !== true && (app.adminOnly !== true || isAdmin));
}

export function findApp(id: string): CoreApp | undefined {
  return coreApps.find((app) => app.id === id);
}
