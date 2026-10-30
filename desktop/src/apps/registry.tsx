import {
  IconActivity,
  IconBuildingStore,
  IconFolders,
  IconInfoCircle,
  IconListDetails,
  IconSearch,
  IconSettings,
  IconUserCog,
} from '../ui/icons';
import { lazy, type ComponentType, type LazyExoticComponent } from 'react';
import type { AppGlyph, AppTone } from '../shell/types';
import type { WindowOptions } from '../wm/types';
import { loadTranslations } from '../i18n';
import { coreAppIds } from './ids';
import { useInstalledApps, type InstalledApp } from './installed';

/** What every app component receives. The SDK replaces this in step 5.3. */
export interface AppProps {
  windowId: string;
}

/** An app the desktop can open: a core app, or a third-party app installed on the box. */
export interface AppEntry {
  id: string;
  icon: AppGlyph;
  tone: AppTone;
  window: WindowOptions;
  /** Loaded when the first window of the app opens, never at sign-in. */
  Component: LazyExoticComponent<ComponentType<AppProps>>;
  /** Listed only for administrators; the daemon refuses everyone else anyway. */
  adminOnly?: true;
  /** Given to users by a grant (the daemon's `CoreApps.Granted`); administrators have it anyway. */
  granted?: true;
  /** Opened by other apps with a file, never listed in the menu or on the desktop. */
  launchOnly?: true;
  /** Installed from a package; it runs in the sandboxed iframe and its name comes from its manifest. */
  thirdParty?: true;
}

/**
 * The core apps that run in the desktop context; names come from `apps.<id>` in the desktop namespace
 * (`apps.org.pithyx.files`). Third-party apps come from the topic `apps` (installed.ts).
 */
export const coreApps: readonly AppEntry[] = [
  {
    id: coreAppIds.files,
    icon: IconFolders,
    tone: 'petrol',
    granted: true,
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
    id: coreAppIds.settings,
    icon: IconSettings,
    tone: 'graphite',
    adminOnly: true,
    window: {
      instances: 'single',
      defaultSize: { width: 960, height: 640 },
      minSize: { width: 340, height: 360 },
    },
    Component: lazy(() =>
      // Storage and Users are groups and pages of Settings (D402), with their own texts.
      Promise.all([
        import('./settings/SettingsWindow'),
        loadTranslations('settings', 'storage', 'users', 'network', 'security'),
      ]).then(([{ SettingsWindow }]) => ({ default: SettingsWindow })),
    ),
  },
  {
    id: coreAppIds.monitor,
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
    id: coreAppIds.logs,
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
    id: coreAppIds.viewer,
    icon: IconSearch,
    tone: 'plum',
    granted: true,
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
    id: coreAppIds.about,
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
    id: coreAppIds.preferences,
    icon: IconUserCog,
    tone: 'ocean',
    window: {
      instances: 'single',
      defaultSize: { width: 640, height: 640 },
      minSize: { width: 320, height: 360 },
    },
    Component: lazy(() =>
      import('./preferences/PreferencesApp').then(({ PreferencesApp }) => ({
        default: PreferencesApp,
      })),
    ),
  },
  {
    id: coreAppIds.store,
    icon: IconBuildingStore,
    tone: 'indigo',
    adminOnly: true,
    window: {
      instances: 'single',
      defaultSize: { width: 960, height: 660 },
      minSize: { width: 320, height: 360 },
    },
    Component: lazy(() =>
      Promise.all([import('./store/StoreWindow'), loadTranslations('store')]).then(
        ([{ StoreWindow }]) => ({ default: StoreWindow }),
      ),
    ),
  },
];

// Every installed app opens in the same component; it finds its app by the window.
const ThirdPartyWindow = lazy(() =>
  import('./sandbox/ThirdPartyWindow').then(({ ThirdPartyWindow }) => ({
    default: ThirdPartyWindow,
  })),
);

// One entry per summary object, so the entry stays the same until the topic changes the app.
const entries = new WeakMap<InstalledApp, AppEntry>();

/** The registry entry of an installed app: window settings of its manifest, its icon from the daemon. */
export function thirdPartyApp(app: InstalledApp): AppEntry {
  let entry = entries.get(app);
  if (entry === undefined) {
    entry = {
      id: app.id,
      icon: app.icon,
      tone: 'graphite',
      // The daemon checked the manifest against its schema at the install.
      window: app.window as WindowOptions,
      Component: ThirdPartyWindow,
      thirdParty: true,
    };
    entries.set(app, entry);
  }
  return entry;
}

/** Every app: the core apps, then the installed apps the user may use, in the order the daemon lists them. */
export function allApps(
  installed: readonly InstalledApp[] | undefined = useInstalledApps.getState().apps,
): readonly AppEntry[] {
  return [...coreApps, ...(installed ?? []).map(thirdPartyApp)];
}

/** The apps a user sees in the start menu and can put on the desktop. */
export function appsFor(allowed: (id: string) => boolean): readonly AppEntry[] {
  return allApps().filter((app) => app.launchOnly !== true && allowed(app.id));
}

export function findApp(id: string): AppEntry | undefined {
  return allApps().find((app) => app.id === id);
}
