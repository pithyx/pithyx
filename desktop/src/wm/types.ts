import type { TaskbarPosition } from '../shell/types';

/** CSS pixels relative to the top left corner of the workspace (the desktop area without the taskbar). */
export interface Rect {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface Size {
  width: number;
  height: number;
}

export interface Point {
  x: number;
  y: number;
}

/** Halves everywhere, thirds and two thirds on ultrawide workspaces. */
export type SnapZone =
  | 'left'
  | 'right'
  | 'left-third'
  | 'center-third'
  | 'right-third'
  | 'left-two-thirds'
  | 'right-two-thirds';

/** From the viewport width: below 600 px, 600 to 1023 px, 1024 px and up. */
export type SizeClass = 'compact' | 'medium' | 'expanded';

export type Placement =
  { kind: 'free' } | { kind: 'maximized' } | { kind: 'snapped'; zone: SnapZone };

/** How an app's windows behave, from the app definition (later the manifest). */
export interface WindowOptions {
  /** `single` focuses the existing window instead of opening a second one. */
  instances: 'single' | 'multi';
  defaultSize: Size;
  minSize: Size;
}

export interface WindowState {
  id: string;
  app: string;
  /** Every window belongs to a screen; there is one screen for now. */
  screen: number;
  /** Set by the app; null shows the app name. */
  title: string | null;
  /** The free geometry. Kept while maximized or snapped, so restoring returns to it. */
  rect: Rect;
  placement: Placement;
  minimized: boolean;
  minSize: Size;
  /** Launch arguments and small app data. Saved with the layout within a shared size budget. */
  args: unknown;
}

export interface DesktopState {
  /** In opening order, which is also the taskbar order. */
  windows: Record<string, WindowState>;
  /** Stacking order, bottom to top. */
  order: string[];
  focused: string | null;
  workspace: Size;
  taskbar: TaskbarPosition;
  nextId: number;
}

export type Action =
  | { type: 'open'; app: string; options: WindowOptions; args?: unknown; size?: Size }
  | { type: 'focus'; id: string }
  | { type: 'close'; id: string }
  | { type: 'closeAll' }
  | { type: 'minimize'; id: string }
  | { type: 'maximize'; id: string }
  | { type: 'restore'; id: string }
  | { type: 'toggleMaximize'; id: string }
  | { type: 'snap'; id: string; zone: SnapZone }
  | { type: 'move'; id: string; rect: Rect }
  | { type: 'setWorkspace'; size: Size }
  | { type: 'cycle'; direction: 1 | -1 }
  | { type: 'setTitle'; id: string; title: string | null }
  | { type: 'setTaskbar'; position: TaskbarPosition }
  | { type: 'replace'; state: DesktopState };
