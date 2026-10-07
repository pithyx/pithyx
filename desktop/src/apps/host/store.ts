import type { ContextMenuItem, NotificationAction, NotificationActivation } from '@pithyx/sdk';
import { create } from 'zustand';
import { dispatch, openApp, useDesktop } from '../../wm/store';
import { emitTo, isReady } from './connections';

/** A context menu an app asked for, at a point of the page. */
export interface AppMenu {
  windowId: string;
  x: number;
  y: number;
  items: ContextMenuItem[];
}

/** The buttons and data of a notification an app sent from this tab (until the daemon keeps them, A11). */
export interface AppNotification {
  app: string;
  /** The window that sent it; another window of the app gets the action once it is gone. */
  windowId: string;
  actions: NotificationAction[];
  data: unknown;
}

/**
 * What the desktop draws for app windows and keeps outside the window manager: badges, attention, unsaved changes,
 * the open app menu, the dialogs for an app that does not answer and for a copy the browser refused, and the actions
 * of notifications. Never saved; it ends with the windows.
 */
interface HostState {
  /** Badge per app id, shown on every taskbar entry of the app. */
  badges: Record<string, number>;
  /** Windows whose taskbar entry pulses until the user looks at them. */
  attention: Record<string, true>;
  /** Unsaved changes per window, for apps that called `setDirty`; missing for apps that never did. */
  dirty: Record<string, boolean>;
  menu: AppMenu | null;
  /** Windows that did not answer `beforeClose` in time, first one shown. */
  unresponsive: string[];
  /** Text the browser refused to copy, shown for the user to copy by hand. */
  copy: string | null;
  notifications: Record<string, AppNotification>;
}

const empty: HostState = {
  badges: {},
  attention: {},
  dirty: {},
  menu: null,
  unresponsive: [],
  copy: null,
  notifications: {},
};

export const useHost = create<HostState>(() => empty);

let menuAnswer: ((id: string | null) => void) | undefined;
const unresponsiveAnswers = new Map<string, (close: boolean) => void>();
let copyDone: (() => void) | undefined;

export function setBadge(app: string, count: number | null): void {
  useHost.setState((state) => ({
    badges:
      count === null || count <= 0
        ? kept(state.badges, (key) => key !== app)
        : { ...state.badges, [app]: count },
  }));
}

/** Pulses the window's taskbar entry, unless the user is looking at it already. */
export function requestAttention(windowId: string): void {
  const desktop = useDesktop.getState();
  if (desktop.focused === windowId && desktop.windows[windowId]?.minimized === false) {
    return;
  }
  useHost.setState((state) => ({ attention: { ...state.attention, [windowId]: true } }));
}

export function setDirty(windowId: string, dirty: boolean): void {
  useHost.setState((state) => ({ dirty: { ...state.dirty, [windowId]: dirty } }));
}

/** Shows an app's menu and resolves with the picked entry, or null. A menu still open resolves with null first. */
export function showMenu(menu: AppMenu): Promise<string | null> {
  closeMenu(null);
  useHost.setState({ menu });
  return new Promise((resolve) => {
    menuAnswer = resolve;
  });
}

export function closeMenu(picked: string | null): void {
  const answer = menuAnswer;
  menuAnswer = undefined;
  if (useHost.getState().menu !== null) {
    useHost.setState({ menu: null });
  }
  answer?.(picked);
}

/** Asks the user whether to close a window whose app did not answer; resolves true to close it. */
export function offerForceClose(windowId: string): Promise<boolean> {
  unresponsiveAnswers.get(windowId)?.(false);
  useHost.setState((state) => ({
    unresponsive: [...state.unresponsive.filter((id) => id !== windowId), windowId],
  }));
  return new Promise((resolve) => {
    unresponsiveAnswers.set(windowId, resolve);
  });
}

export function answerForceClose(windowId: string, close: boolean): void {
  const answer = unresponsiveAnswers.get(windowId);
  unresponsiveAnswers.delete(windowId);
  useHost.setState((state) => ({
    unresponsive: state.unresponsive.filter((id) => id !== windowId),
  }));
  answer?.(close);
}

/** Shows text the browser would not copy, so the user can copy it; resolves when the dialog closes. */
export function offerManualCopy(text: string): Promise<void> {
  copyDone?.();
  useHost.setState({ copy: text });
  return new Promise((resolve) => {
    copyDone = resolve;
  });
}

export function closeManualCopy(): void {
  const done = copyDone;
  copyDone = undefined;
  useHost.setState({ copy: null });
  done?.();
}

export function rememberNotification(id: string, notification: AppNotification): void {
  useHost.setState((state) => ({ notifications: { ...state.notifications, [id]: notification } }));
}

/**
 * The user clicked an app's notification (`action` null) or one of its buttons. A window of the app on this device
 * comes to the front, as for any user action, and gets `notificationAction`: the window that sent the notification,
 * otherwise the top-most one. Without a window that is connected, the app starts (or its `single` window gets) the
 * activation as `launch.notification`. False if the app cannot be opened.
 */
export function activateNotification(
  id: string,
  app: string,
  action: string | null,
  /** The data the daemon kept with the notification; undefined for one it keeps none for. */
  data?: unknown,
): boolean {
  const remembered = useHost.getState().notifications[id];
  const activation: NotificationActivation = {
    notification: id,
    action,
    data: data !== undefined ? data : remembered?.app === app ? remembered.data : null,
  };
  const { windows, order } = useDesktop.getState();
  const connected = (windowId: string) => windows[windowId]?.app === app && isReady(windowId);
  const target =
    remembered !== undefined && connected(remembered.windowId)
      ? remembered.windowId
      : order.findLast(connected);
  if (target === undefined) {
    return openApp(app, { args: null, notification: activation });
  }
  dispatch({ type: 'focus', id: target });
  emitTo(target, 'notificationAction', activation);
  return true;
}

function kept<T>(record: Record<string, T>, keep: (key: string) => boolean): Record<string, T> {
  const entries = Object.entries(record);
  const left = entries.filter(([key]) => keep(key));
  return left.length === entries.length ? record : Object.fromEntries(left);
}

// Everything of a window goes with it, a badge with the app's last window, and attention once the user looks.
useDesktop.subscribe((next, previous) => {
  if (next.windows === previous.windows && next.focused === previous.focused) {
    return;
  }
  const open = (id: string) => next.windows[id] !== undefined;
  for (const id of Object.keys(previous.windows).filter((id) => !open(id))) {
    unresponsiveAnswers.get(id)?.(true);
    unresponsiveAnswers.delete(id);
  }
  const menu = useHost.getState().menu;
  if (menu !== null && !open(menu.windowId)) {
    closeMenu(null);
  }
  const apps = new Set(Object.values(next.windows).map((window) => window.app));
  const looked =
    next.focused !== null && next.windows[next.focused]?.minimized === false ? next.focused : null;
  const state = useHost.getState();
  const change = {
    attention: kept(state.attention, (id) => open(id) && id !== looked),
    dirty: kept(state.dirty, open),
    badges: kept(state.badges, (app) => apps.has(app)),
    unresponsive: state.unresponsive.every(open)
      ? state.unresponsive
      : state.unresponsive.filter(open),
  };
  if (
    change.attention !== state.attention ||
    change.dirty !== state.dirty ||
    change.badges !== state.badges ||
    change.unresponsive !== state.unresponsive
  ) {
    useHost.setState(change);
  }
});

/** Forgets everything when the user signs out. */
export function resetHost(): void {
  closeMenu(null);
  closeManualCopy();
  for (const answer of unresponsiveAnswers.values()) {
    answer(true);
  }
  unresponsiveAnswers.clear();
  useHost.setState(empty);
}
