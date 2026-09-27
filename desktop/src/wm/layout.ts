import type { TaskbarPosition } from '../shell/types';
import { MAX_WINDOWS, initialState, topVisible } from './state';
import type {
  DesktopState,
  Placement,
  Rect,
  Size,
  SnapZone,
  WindowOptions,
  WindowState,
} from './types';

export const LAYOUT_VERSION = 1;
/** "App data in a layout is capped at 16 KB": the sum of all window arguments, in UTF-8 bytes. */
export const APP_DATA_LIMIT = 16 * 1024;

export interface LayoutWindow {
  id: string;
  app: string;
  screen: number;
  title: string | null;
  rect: Rect;
  placement: Placement;
  minimized: boolean;
  args: unknown;
}

/** What the daemon stores per user and device. It never contains permissions. */
export interface Layout {
  version: typeof LAYOUT_VERSION;
  taskbar: TaskbarPosition;
  windows: LayoutWindow[];
  order: string[];
  focused: string | null;
}

const encoder = new TextEncoder();

function bytes(value: unknown): number {
  return value === null || value === undefined ? 0 : encoder.encode(JSON.stringify(value)).length;
}

/**
 * The saved form of a desktop. Arguments are kept for the top-most windows first; windows whose arguments no
 * longer fit the budget are saved without them.
 */
export function toLayout(state: DesktopState): Layout {
  let budget = APP_DATA_LIMIT;
  const kept = new Set<string>();
  for (const id of [...state.order].reverse()) {
    const size = bytes(state.windows[id]?.args);
    if (size <= budget) {
      budget -= size;
      kept.add(id);
    }
  }
  return {
    version: LAYOUT_VERSION,
    taskbar: state.taskbar,
    windows: Object.values(state.windows).map((window) => ({
      id: window.id,
      app: window.app,
      screen: window.screen,
      title: window.title,
      rect: window.rect,
      placement: window.placement,
      minimized: window.minimized,
      args: kept.has(window.id) ? window.args : null,
    })),
    order: state.order,
    focused: state.focused,
  };
}

const TASKBAR_POSITIONS: readonly string[] = ['bottom', 'top', 'left', 'right'];
const SNAP_ZONES: readonly string[] = [
  'left',
  'right',
  'left-third',
  'center-third',
  'right-third',
  'left-two-thirds',
  'right-two-thirds',
];

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function finite(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

function parseRect(value: unknown): Rect | null {
  if (!isRecord(value)) {
    return null;
  }
  const { x, y, width, height } = value;
  return finite(x) && finite(y) && finite(width) && finite(height) && width > 0 && height > 0
    ? { x, y, width, height }
    : null;
}

function parsePlacement(value: unknown): Placement {
  if (isRecord(value)) {
    if (value.kind === 'maximized') {
      return { kind: 'maximized' };
    }
    if (
      value.kind === 'snapped' &&
      typeof value.zone === 'string' &&
      SNAP_ZONES.includes(value.zone)
    ) {
      return { kind: 'snapped', zone: value.zone as SnapZone };
    }
  }
  return { kind: 'free' };
}

export interface RestoredLayout {
  state: DesktopState;
  /** App ids of windows that could not be restored. */
  skipped: string[];
}

/**
 * Turns a saved layout back into state. Windows whose app is unknown or no longer allowed are skipped, and anything
 * malformed is dropped instead of failing the whole layout.
 */
export function fromLayout(
  input: unknown,
  appOptions: (app: string) => WindowOptions | undefined,
  workspace: Size,
): RestoredLayout {
  if (!isRecord(input) || input.version !== LAYOUT_VERSION || !Array.isArray(input.windows)) {
    return { state: initialState(workspace), skipped: [] };
  }
  const taskbar =
    typeof input.taskbar === 'string' && TASKBAR_POSITIONS.includes(input.taskbar)
      ? (input.taskbar as TaskbarPosition)
      : 'bottom';
  const windows: Record<string, WindowState> = {};
  const skipped: string[] = [];
  let nextId = 1;
  for (const entry of input.windows.slice(0, MAX_WINDOWS)) {
    if (!isRecord(entry) || typeof entry.id !== 'string' || typeof entry.app !== 'string') {
      continue;
    }
    const options = appOptions(entry.app);
    const rect = parseRect(entry.rect);
    if (options === undefined) {
      skipped.push(entry.app);
      continue;
    }
    if (rect === null || windows[entry.id] !== undefined) {
      continue;
    }
    windows[entry.id] = {
      id: entry.id,
      app: entry.app,
      screen: 1,
      title: typeof entry.title === 'string' ? entry.title : null,
      // Kept as saved; windows are shown clamped to the workspace (visibleRect), so a layout from a larger screen
      // gets its positions back there.
      rect,
      placement: parsePlacement(entry.placement),
      minimized: entry.minimized === true,
      minSize: options.minSize,
      args: entry.args ?? null,
    };
    const number = /^w(\d+)$/.exec(entry.id)?.[1];
    if (number !== undefined) {
      nextId = Math.max(nextId, Number(number) + 1);
    }
  }
  const saved = Array.isArray(input.order)
    ? input.order.filter((id): id is string => typeof id === 'string' && windows[id] !== undefined)
    : [];
  const order = [
    ...new Set([...Object.keys(windows).filter((id) => !saved.includes(id)), ...saved]),
  ];
  const partial = { windows, order };
  const focused =
    typeof input.focused === 'string' && windows[input.focused]?.minimized === false
      ? input.focused
      : topVisible(partial);
  return {
    state: { windows, order, focused, workspace, taskbar, nextId },
    skipped,
  };
}
