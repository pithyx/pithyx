import type { LaunchArgs } from '@pithyx/sdk';
import { create } from 'zustand';
import { isAppAllowed } from '../apps/access';
import { emitTo } from '../apps/host/connections';
import { withArgs, type WindowArgs } from '../apps/host/rules';
import { findApp } from '../apps/registry';
import { preferredSize } from './preferences';
import { initialState, launchTarget, reduce } from './state';
import type { Action, DesktopState } from './types';

/** The window manager state. Components read it with narrow selectors; every change goes through reduce(). */
export const useDesktop = create<DesktopState>(() => initialState({ width: 1024, height: 700 }));

export function dispatch(action: Action): void {
  useDesktop.setState((state) => reduce(state, action), true);
}

/**
 * Launches an app for a user action: opens a window, or for a `single` app focuses the user's window on this device
 * and sends it the new launch arguments as `launch` (an open window keeps its saved state). `session` is state for a
 * new window, as if the app had saved it. False if the app is unknown, not allowed for the user or the desktop is full.
 */
export function openApp(
  id: string,
  launch: LaunchArgs = { args: null },
  options: { session?: unknown } = {},
): boolean {
  const app = findApp(id);
  if (app === undefined || !isAppAllowed(id)) {
    return false;
  }
  const state = useDesktop.getState();
  const target = launchTarget(state, id, app.window.instances);
  if (target.kind === 'full') {
    return false;
  }
  if (target.kind === 'existing') {
    dispatch({
      type: 'setArgs',
      id: target.id,
      args: withArgs(state.windows[target.id]?.args, { launch }),
    });
    dispatch({ type: 'focus', id: target.id });
    emitTo(target.id, 'launch', launch);
    return true;
  }
  const size = preferredSize(id);
  const args: WindowArgs =
    options.session === undefined ? { launch } : { launch, session: options.session };
  dispatch({
    type: 'open',
    app: id,
    options: app.window,
    args,
    ...(size === undefined ? {} : { size }),
  });
  return true;
}
