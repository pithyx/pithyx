import { IconDatabase, IconInfoCircle, type TablerIcon } from '@tabler/icons-react';
import { lazy, type ComponentType, type LazyExoticComponent } from 'react';
import type { AppTone } from '../shell/types';
import type { WindowOptions } from '../wm/types';

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
}

/**
 * The core apps that run in the desktop context. An internal list until the manifest schema and the SDK (step 5.3)
 * replace it; names come from `apps.<id>` in the desktop namespace.
 */
export const coreApps: readonly CoreApp[] = [
  {
    id: 'disks',
    icon: IconDatabase,
    tone: 'ocean',
    window: {
      instances: 'single',
      defaultSize: { width: 880, height: 520 },
      minSize: { width: 360, height: 240 },
    },
    Component: lazy(() =>
      import('./disks/DisksApp').then(({ DisksApp }) => ({ default: DisksApp })),
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
];

export function findApp(id: string): CoreApp | undefined {
  return coreApps.find((app) => app.id === id);
}
