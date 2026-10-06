import type { TablerIcon } from '../ui/icons';

export type AppTone =
  'petrol' | 'ocean' | 'indigo' | 'plum' | 'rose' | 'moss' | 'amber' | 'graphite';

export interface AppInfo {
  id: string;
  name: string;
  /** The app's glyph; null for an app without an icon of its own, which shows the default icon (D307). */
  icon: TablerIcon | null;
  tone: AppTone;
}

export type RunningState = 'focused' | 'open' | 'minimized';

export interface RunningApp extends AppInfo {
  state: RunningState;
  /** The app's badge count (`setBadge`); 0 for none. */
  badge?: number;
  /** The app asked for attention (`requestAttention`); the entry pulses until the user looks. */
  attention?: boolean;
}

export type TaskbarPosition = 'bottom' | 'top' | 'left' | 'right';
