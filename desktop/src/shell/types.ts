import type { TablerIcon } from '../ui/icons';

export type AppTone =
  'petrol' | 'ocean' | 'indigo' | 'plum' | 'rose' | 'moss' | 'amber' | 'graphite';

/** An app's glyph: a Tabler icon for a core app, the icon URL of an installed app, or null for the default icon (D307). */
export type AppGlyph = TablerIcon | string | null;

export interface AppInfo {
  id: string;
  name: string;
  icon: AppGlyph;
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
