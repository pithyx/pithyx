import type { TaskbarPosition } from '../shell/types';
import { cascade, clampRect, clampSize } from './geometry';
import type { Action, DesktopState, Placement, Rect, Size, WindowState } from './types';

/** More windows than this are refused; it also bounds the saved layout. */
export const MAX_WINDOWS = 50;

const FREE: Placement = { kind: 'free' };

export function initialState(workspace: Size, taskbar: TaskbarPosition = 'bottom'): DesktopState {
  return { windows: {}, order: [], focused: null, workspace, taskbar, nextId: 1 };
}

/** The top-most window that is not minimized. */
export function topVisible(state: Pick<DesktopState, 'windows' | 'order'>): string | null {
  for (let index = state.order.length - 1; index >= 0; index--) {
    const id = state.order[index];
    if (id !== undefined && state.windows[id]?.minimized === false) {
      return id;
    }
  }
  return null;
}

/**
 * Where a free window is shown: its saved rect, kept fully inside the current workspace. The saved rect itself stays
 * untouched, so a window returns to its place when the screen grows again.
 */
export function visibleRect(window: Pick<WindowState, 'rect' | 'minSize'>, workspace: Size): Rect {
  return clampRect(window.rect, window.minSize, workspace);
}

/**
 * Where a launch of an app goes: a `single` app that already has a window on this device goes to that window (the
 * top-most one, should a restored layout hold several), anything else opens a new window unless the desktop is full.
 */
export function launchTarget(
  state: Pick<DesktopState, 'windows' | 'order'>,
  app: string,
  instances: 'single' | 'multi',
): { kind: 'existing'; id: string } | { kind: 'new' } | { kind: 'full' } {
  if (instances === 'single') {
    const id = state.order.findLast((candidate) => state.windows[candidate]?.app === app);
    if (id !== undefined) {
      return { kind: 'existing', id };
    }
  }
  return Object.keys(state.windows).length < MAX_WINDOWS ? { kind: 'new' } : { kind: 'full' };
}

export function canOpen(state: DesktopState, app: string, instances: 'single' | 'multi'): boolean {
  return launchTarget(state, app, instances).kind !== 'full';
}

function update(state: DesktopState, id: string, change: Partial<WindowState>): DesktopState {
  const current = state.windows[id];
  if (current === undefined) {
    return state;
  }
  return { ...state, windows: { ...state.windows, [id]: { ...current, ...change } } };
}

function raise(state: DesktopState, id: string): DesktopState {
  if (state.windows[id] === undefined) {
    return state;
  }
  const raised = update(state, id, { minimized: false });
  const order =
    raised.order.at(-1) === id ? raised.order : [...raised.order.filter((w) => w !== id), id];
  return { ...raised, order, focused: id };
}

function remove(state: DesktopState, id: string): DesktopState {
  if (state.windows[id] === undefined) {
    return state;
  }
  const windows = { ...state.windows };
  // eslint-disable-next-line @typescript-eslint/no-dynamic-delete -- the map is a fresh copy
  delete windows[id];
  const order = state.order.filter((w) => w !== id);
  const next = { ...state, windows, order };
  return { ...next, focused: state.focused === id ? topVisible(next) : state.focused };
}

function open(state: DesktopState, action: Extract<Action, { type: 'open' }>): DesktopState {
  const { app, options } = action;
  const target = launchTarget(state, app, options.instances);
  if (target.kind === 'existing') {
    const raised = raise(state, target.id);
    return action.args === undefined ? raised : update(raised, target.id, { args: action.args });
  }
  if (target.kind === 'full') {
    return state;
  }
  const size = clampSize(action.size ?? options.defaultSize, options.minSize, state.workspace);
  const previousId = state.order.at(-1);
  const previousWindow = previousId === undefined ? undefined : state.windows[previousId];
  const previous =
    previousWindow === undefined ? null : visibleRect(previousWindow, state.workspace);
  const id = `w${String(state.nextId)}`;
  const window: WindowState = {
    id,
    app,
    screen: 1,
    title: null,
    rect: clampRect(cascade(previous, size, state.workspace), options.minSize, state.workspace),
    placement: FREE,
    minimized: false,
    minSize: options.minSize,
    args: action.args ?? null,
  };
  return {
    ...state,
    windows: { ...state.windows, [id]: window },
    order: [...state.order, id],
    focused: id,
    nextId: state.nextId + 1,
  };
}

/** Next or previous window in taskbar order, wrapping around. */
function cycle(state: DesktopState, direction: 1 | -1): DesktopState {
  const ids = Object.keys(state.windows);
  if (ids.length === 0) {
    return state;
  }
  const current = state.focused === null ? -1 : ids.indexOf(state.focused);
  const start = current === -1 && direction === -1 ? 0 : current;
  const next = ids[(start + direction + ids.length) % ids.length];
  return next === undefined ? state : raise(state, next);
}

/** Every state change of the window manager. Pure: no DOM, no React, no time. */
export function reduce(state: DesktopState, action: Action): DesktopState {
  switch (action.type) {
    case 'open':
      return open(state, action);
    case 'focus':
      return raise(state, action.id);
    case 'close':
      return remove(state, action.id);
    case 'closeAll':
      return { ...state, windows: {}, order: [], focused: null };
    case 'minimize': {
      const minimized = update(state, action.id, { minimized: true });
      return minimized.focused === action.id
        ? { ...minimized, focused: topVisible(minimized) }
        : minimized;
    }
    case 'maximize':
      return update(raise(state, action.id), action.id, { placement: { kind: 'maximized' } });
    case 'restore':
      return update(raise(state, action.id), action.id, { placement: FREE });
    case 'toggleMaximize': {
      const kind = state.windows[action.id]?.placement.kind;
      return reduce(state, { type: kind === 'maximized' ? 'restore' : 'maximize', id: action.id });
    }
    case 'snap':
      return update(raise(state, action.id), action.id, {
        placement: { kind: 'snapped', zone: action.zone },
      });
    case 'move': {
      const window = state.windows[action.id];
      if (window === undefined) {
        return state;
      }
      return update(raise(state, action.id), action.id, {
        rect: clampRect(action.rect, window.minSize, state.workspace),
        placement: FREE,
      });
    }
    case 'setGeometry':
      // Undo of a keyboard move or resize: back to exactly what was stored, without clamping.
      return update(state, action.id, { rect: action.rect, placement: action.placement });
    case 'setWorkspace':
      return action.size.width === state.workspace.width &&
        action.size.height === state.workspace.height
        ? state
        : { ...state, workspace: action.size };
    case 'cycle':
      return cycle(state, action.direction);
    case 'setTitle':
      return update(state, action.id, { title: action.title });
    case 'setArgs':
      return update(state, action.id, { args: action.args });
    case 'setTaskbar':
      return { ...state, taskbar: action.position };
    case 'replace':
      return action.state;
  }
}
