import type { TablerIcon } from '@tabler/icons-react';

export type AppTone =
  'petrol' | 'ocean' | 'indigo' | 'plum' | 'rose' | 'moss' | 'amber' | 'graphite';

export interface AppInfo {
  id: string;
  name: string;
  icon: TablerIcon;
  tone: AppTone;
}

export type RunningState = 'focused' | 'open' | 'minimized';

export interface RunningApp extends AppInfo {
  state: RunningState;
}

export type TaskbarPosition = 'bottom' | 'top';
