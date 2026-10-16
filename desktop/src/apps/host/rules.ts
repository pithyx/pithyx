import type { EventName, LaunchArgs, Point, StorageInfo } from '@pithyx/sdk';
import type { RealtimeStatus } from '../../realtime/connection';

/** A file of a shared folder that an app is launched with; the window turns it into the app's `launch.file`. */
export interface PendingFile {
  share: string;
  path: string;
  size: number;
  modified: string;
  writable: boolean;
}

/** Launch arguments as the desktop passes them on: what the app sees, and the file it is opened with. */
export type DesktopLaunch = LaunchArgs & { pendingFile?: PendingFile };

/** The file a window was launched with, if any. */
export function pendingFileOf(args: unknown): PendingFile | undefined {
  const launch = argsRecord(args).launch;
  return typeof launch === 'object' && launch !== null && 'pendingFile' in launch
    ? (launch.pendingFile as PendingFile)
    : undefined;
}

/** What a window keeps in its `args`: the arguments it was launched with and the state the app saved. */
export interface WindowArgs {
  launch?: DesktopLaunch;
  session?: unknown;
}

function argsRecord(args: unknown): Record<string, unknown> {
  return typeof args === 'object' && args !== null && !Array.isArray(args)
    ? (args as Record<string, unknown>)
    : {};
}

/** The launch arguments a window keeps; windows of older layouts and windows opened without any get `{ args: null }`. */
export function launchOf(args: unknown): LaunchArgs {
  const launch = argsRecord(args).launch;
  if (typeof launch !== 'object' || launch === null || !('args' in launch)) {
    return { args: null };
  }
  // The file the window was launched with reaches the app as `file`, made by the window, not as the path it names.
  const visible: Record<string, unknown> = { ...launch };
  delete visible.pendingFile;
  delete visible.file;
  return visible as unknown as LaunchArgs;
}

/** The state an app saved for the window with `session.save`, or null. */
export function sessionOf(args: unknown): unknown {
  return argsRecord(args).session ?? null;
}

/** The window's arguments with one part replaced, keeping the other. */
export function withArgs(args: unknown, change: WindowArgs): WindowArgs {
  const current = argsRecord(args);
  const next: WindowArgs = {};
  if ('launch' in current) {
    next.launch = current.launch as DesktopLaunch;
  }
  if ('session' in current) {
    next.session = current.session;
  }
  return { ...next, ...change };
}

/**
 * Whether an app window can be seen, as `visibilityChange` reports it: not while it is minimized, and not while the
 * browser tab is hidden. A window behind another one still counts as visible (the contract only names minimizing).
 */
export function isWindowVisible(minimized: boolean, tabHidden: boolean): boolean {
  return !minimized && !tabHidden;
}

/** Whether the desktop reaches the box, as `connectionChange` reports it. Before the first connect it counts as reachable. */
export function isOnline(status: RealtimeStatus): boolean {
  return status !== 'reconnecting' && status !== 'stopped';
}

/** The content box of an app window on the page, in CSS pixels of the viewport. */
export interface ContentBox {
  left: number;
  top: number;
  width: number;
  height: number;
}

/**
 * Turns a point an app passes (relative to its window content) into a point of the page. A point outside the content
 * is moved to its nearest edge, so an app can never place a menu over another window or the taskbar.
 */
export function pagePoint(at: Point, content: ContentBox): Point {
  const clamp = (value: number, size: number) => Math.min(Math.max(value, 0), Math.max(size, 0));
  return {
    x: Math.round(content.left + clamp(at.x, content.width)),
    y: Math.round(content.top + clamp(at.y, content.height)),
  };
}

/** The highest count a badge shows in digits; above it shows this number with a plus. */
export const BADGE_MAX = 99;

/** The text of a taskbar badge, or null when none is shown (no count, zero). */
export function badgeText(count: number | null | undefined, locale: string): string | null {
  if (count === null || count === undefined || count <= 0) {
    return null;
  }
  const format = new Intl.NumberFormat(locale);
  return count > BADGE_MAX ? `${format.format(BADGE_MAX)}+` : format.format(count);
}

/**
 * The first step of closing a window the user asked to close: an app that marked its window clean with `setDirty`
 * closes at once; a dirty window, or one of an app that never called `setDirty`, asks the app with `beforeClose`.
 */
export function closeStep(dirty: boolean | undefined): 'close' | 'ask' {
  return dirty === false ? 'close' : 'ask';
}

/**
 * What the answer to `beforeClose` means: the app allows it, keeps the window, or did not answer in time, in which
 * case the desktop offers to close the window anyway (D62).
 */
export function answerStep(answer: { allow: boolean } | undefined): 'close' | 'keep' | 'offer' {
  if (answer === undefined) {
    return 'offer';
  }
  return answer.allow ? 'close' : 'keep';
}

/** The parts of the daemon's app context (`apps/<id>/context`) that reach a running app as events. */
export interface ContextState {
  /** The installed version; an update or a rollback changes it. */
  version?: string;
  permissions: readonly string[];
  logLevel: 'debug' | 'info';
  storage: readonly StorageInfo[];
  /** The backend's status, or null for an app without a backend. */
  backend?: { status: string } | null;
}

/**
 * What a change of the daemon's context means for a running window: `permissionsChange`, `logLevelChange`,
 * `storageChange`, `backendStatus` and `appUpdated` (another version installed, by an update or a rollback) for what
 * differs, in that order. Unchanged parts send nothing.
 */
export function contextChanges(
  previous: ContextState,
  next: ContextState,
): { permissions?: string[]; events: { event: EventName; data: unknown }[] } {
  const events: { event: EventName; data: unknown }[] = [];
  const permissions = [...next.permissions];
  const permissionsChanged =
    previous.permissions.length !== permissions.length ||
    previous.permissions.some((permission) => !permissions.includes(permission));
  if (permissionsChanged) {
    events.push({ event: 'permissionsChange', data: { permissions } });
  }
  if (previous.logLevel !== next.logLevel) {
    events.push({ event: 'logLevelChange', data: { level: next.logLevel } });
  }
  if (JSON.stringify(previous.storage) !== JSON.stringify(next.storage)) {
    events.push({
      event: 'storageChange',
      data: { storage: next.storage.map((storage) => ({ ...storage })) },
    });
  }
  const status = next.backend?.status;
  if (status !== undefined && status !== previous.backend?.status) {
    events.push({ event: 'backendStatus', data: { status } });
  }
  if (
    next.version !== undefined &&
    previous.version !== undefined &&
    next.version !== previous.version
  ) {
    events.push({ event: 'appUpdated', data: { version: next.version } });
  }
  return permissionsChanged ? { permissions, events } : { events };
}

/** What the loading placeholder of an app window shows: nothing, the app's icon and name, or also the backend's start. */
export type LoadingPhase = 'ready' | 'loading' | 'starting';

/**
 * The placeholder stays until the app connected, or with the manifest's `window.loading.untilReady` until it called
 * `window.ready`, and at most until its time is up (D354). While it shows, a starting backend gets its line of text.
 */
export function loadingPhase(state: {
  connected: boolean;
  appReady: boolean;
  untilReady: boolean;
  timedOut: boolean;
  backendStatus: string | undefined;
}): LoadingPhase {
  if (state.timedOut || (state.connected && (!state.untilReady || state.appReady))) {
    return 'ready';
  }
  return state.backendStatus === 'starting' ? 'starting' : 'loading';
}
