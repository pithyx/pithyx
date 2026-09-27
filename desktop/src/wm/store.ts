import { create } from 'zustand';
import { findApp } from '../apps/registry';
import { preferredSize } from './preferences';
import { canOpen, initialState, reduce } from './state';
import type { Action, DesktopState } from './types';

/** The window manager state. Components read it with narrow selectors; every change goes through reduce(). */
export const useDesktop = create<DesktopState>(() => initialState({ width: 1024, height: 700 }));

export function dispatch(action: Action): void {
  useDesktop.setState((state) => reduce(state, action), true);
}

/** Opens a window of a core app, or focuses it for single-instance apps. False if the app is unknown or full. */
export function openApp(id: string, args?: unknown): boolean {
  const app = findApp(id);
  if (app === undefined || !canOpen(useDesktop.getState(), id, app.window.instances)) {
    return false;
  }
  const size = preferredSize(id);
  dispatch({
    type: 'open',
    app: id,
    options: app.window,
    ...(args === undefined ? {} : { args }),
    ...(size === undefined ? {} : { size }),
  });
  return true;
}
