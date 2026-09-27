/**
 * The wire protocol between an app and the desktop. Both transports carry exactly these messages: the in-process
 * transport for core apps and a `MessagePort` for sandboxed third-party apps. Everything is structured-clone data;
 * functions, DOM nodes and class instances never cross.
 *
 * Handshake:
 * 1. The desktop creates a `MessageChannel` per window, keeps one port and posts the other to the app's iframe as
 *    `{ type: 'vyxis:connect' }` with the port transferred. The iframe is sandboxed without `allow-same-origin`, so
 *    the target origin is `*`; the port is what identifies the app from then on, never message data.
 * 2. The app sends `hello` with its protocol and SDK version on that port.
 * 3. The desktop answers `init` with the {@link AppContext}, or `refuse` with a problem (for example
 *    `sdk.protocol.unsupported`), and closes the port after a refusal.
 * 4. From then on the app sends `request`s and gets one `response` each; the desktop sends `event`s, and `query`s that
 *    the app answers with `reply` (used for `beforeClose` and `beforeLogout`).
 */
import type { Problem } from './errors';
import type { AppId } from './manifest';
import type { Theme } from './theme';

export type SizeClass = 'compact' | 'medium' | 'expanded';

export interface LaunchArgs {
  /** App-defined arguments, for example a view to show. */
  args: unknown;
  /** A file to open, as a path the user may access. */
  file?: string;
}

/** Everything an app needs to start, sent once in `init`. */
export interface AppContext {
  app: { id: AppId; version: string };
  /** Id of this window, unique per desktop tab. */
  instance: string;
  user: { id: number; name: string; displayName: string | null };
  /** BCP 47 tag of the user's language, for example `de-DE`. */
  locale: string;
  /** IANA time zone of the user, for example `Europe/Berlin`. Dates cross the API as UTC. */
  timeZone: string;
  theme: Theme;
  sizeClass: SizeClass;
  launch: LaunchArgs;
  /** The state this window saved with `session.save` before a reload or on another device, or null. */
  session: unknown;
  /** Permissions granted to this user for this app. */
  permissions: string[];
  /** Every method name the desktop supports; check before calling one added after 1.0. */
  capabilities: string[];
  host: { version: string; protocol: number };
}

/** A notification the app shows. The app translates the texts; it knows the locale from the context. */
export interface NotificationRequest {
  title: string;
  body?: string;
  level?: 'info' | 'success' | 'warning' | 'error';
}

/**
 * Every method an app can call: parameters and result. A method name is part of the stable API; a new method is a
 * minor change and appears in {@link AppContext.capabilities}.
 */
export interface Methods {
  /** Sets the window title (shown in the title bar and the taskbar). At most 200 characters. */
  'window.setTitle': { params: { title: string }; result: undefined };
  /** Closes the window. `beforeClose` is not sent for a close the app asked for. */
  'window.close': { params: Record<string, never>; result: undefined };
  /** Asks for a new free size. The desktop may clamp it; the result is the size the window got. */
  'window.setSize': {
    params: { width: number; height: number };
    result: { width: number; height: number };
  };
  /** Shows a notification. Needs the `notifications` permission. */
  notify: { params: NotificationRequest; result: undefined };
  /**
   * Saves small state of this window (the current folder, a scroll position) with the desktop layout, at most 16 KB
   * as JSON. It comes back in the init context after a reload and with "Get desktop from ...".
   */
  'session.save': { params: { state: unknown }; result: undefined };
  /** Reads a value the app stored for this user, or null. */
  'storage.get': { params: { key: string }; result: unknown };
  /**
   * Stores a value for this user, on the daemon (sandboxed apps have no `localStorage`). Keys have at most 128
   * characters, values at most 64 KB as JSON, and an app keeps at most 1 MB per user.
   */
  'storage.set': { params: { key: string; value: unknown }; result: undefined };
  'storage.remove': { params: { key: string }; result: undefined };
  'storage.keys': { params: Record<string, never>; result: string[] };
  /**
   * A token for the app's own backend at `/apps/<id>/api/`, valid 5 minutes. Send it as `Authorization: Bearer`.
   * Needs the `backend` permission. Ask again for a new one; do not cache it past `expiresAt`.
   */
  'auth.token': { params: Record<string, never>; result: { token: string; expiresAt: string } };
}

export type MethodName = keyof Methods;

/**
 * Events the desktop sends. An app ignores events it does not know, so new events are a minor change.
 */
export interface Events {
  /** A `single` app was launched again; the new launch arguments. */
  launch: LaunchArgs;
  focus: Record<string, never>;
  blur: Record<string, never>;
  minimize: Record<string, never>;
  restore: Record<string, never>;
  themeChange: Theme;
  localeChange: { locale: string; timeZone: string };
  sizeClassChange: { sizeClass: SizeClass };
  /** The user opened a file with the app while its window was open. */
  openFile: { file: string };
  /** A new version of the app is installed; reload to use it (unsaved work first). */
  appUpdated: { version: string };
  /** The app's backend stopped answering; calls to it fail until `backendRestored`. */
  backendUnavailable: Record<string, never>;
  backendRestored: Record<string, never>;
  /** A permission was revoked. Calls that need it fail with `sdk.permission.denied` from now on. */
  permissionRevoked: { permission: string };
}

export type EventName = keyof Events;

/** Questions the desktop asks and waits for, each with the answer it expects. */
export interface Queries {
  /**
   * The user closes the window. Answer `allow: false` to keep it open (for example to ask about unsaved changes).
   * The desktop waits at most 5 seconds and then offers to close the window anyway.
   */
  beforeClose: { data: Record<string, never>; answer: { allow: boolean } };
  /** The user signs out. Save what you can; the desktop waits at most 2 seconds. It cannot be refused. */
  beforeLogout: { data: Record<string, never>; answer: Record<string, never> };
}

export type QueryName = keyof Queries;

/** The desktop waits this long for an answer to `beforeClose`. */
export const BEFORE_CLOSE_TIMEOUT = 5000;
/** The desktop waits this long for an answer to `beforeLogout`. */
export const BEFORE_LOGOUT_TIMEOUT = 2000;
/** An app waits this long for a response before a call fails with `sdk.request.timeout`. */
export const REQUEST_TIMEOUT = 15_000;

/** What the desktop posts to the iframe window, with the app's port transferred. */
export interface ConnectMessage {
  type: 'vyxis:connect';
}

export type AppMessage =
  | { type: 'hello'; protocol: number; sdk: string }
  | { type: 'request'; id: number; method: string; params: unknown }
  | { type: 'reply'; id: number; answer: unknown };

export type HostMessage =
  | { type: 'init'; protocol: number; context: AppContext }
  | { type: 'refuse'; problem: Problem }
  | { type: 'response'; id: number; ok: true; result: unknown }
  | { type: 'response'; id: number; ok: false; problem: Problem }
  | { type: 'event'; event: string; data: unknown }
  | { type: 'query'; id: number; query: string; data: unknown };

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

/** Checks the shape of a message from an app. Anything else is dropped without an answer. */
export function isAppMessage(value: unknown): value is AppMessage {
  if (!isRecord(value)) {
    return false;
  }
  switch (value.type) {
    case 'hello':
      return typeof value.protocol === 'number' && typeof value.sdk === 'string';
    case 'request':
      return (
        Number.isSafeInteger(value.id) && typeof value.method === 'string' && 'params' in value
      );
    case 'reply':
      return Number.isSafeInteger(value.id) && 'answer' in value;
    default:
      return false;
  }
}

/** Checks the shape of a message from the desktop. */
export function isHostMessage(value: unknown): value is HostMessage {
  if (!isRecord(value)) {
    return false;
  }
  switch (value.type) {
    case 'init':
      return typeof value.protocol === 'number' && isRecord(value.context);
    case 'refuse':
      return isRecord(value.problem);
    case 'response':
      return Number.isSafeInteger(value.id) && typeof value.ok === 'boolean';
    case 'event':
      return typeof value.event === 'string';
    case 'query':
      return Number.isSafeInteger(value.id) && typeof value.query === 'string';
    default:
      return false;
  }
}
