import { unwrap } from '@pithyx/api-client';
import { sdkError, PithyxError, type MethodHandlers } from '@pithyx/sdk/host';
import { writeAppLog } from './appLogs';
import { api } from '../../api/client';
import { copyText, openInNewTab } from '../../platform/browser';
import { pagePoint, withArgs } from '../host/rules';
import {
  offerManualCopy,
  rememberNotification,
  requestAttention,
  setBadge,
  setDirty,
  showMenu,
} from '../host/store';
import { toPithyxError } from '../sdk/daemonError';
import { dispatch, useDesktop } from '../../wm/store';

/** A stored value may be this large as JSON, and an app keeps at most this much per user. */
const MAX_VALUE = 64 * 1024;
const MAX_TOTAL = 1024 * 1024;
/** Window state saved with the layout; the layout's app data budget is 16 KB for all windows together. */
const MAX_SESSION = 16 * 1024;

// Prototype storage, per user and app, in this tab only. The daemon endpoint (see the SDK spec PR) replaces it.
const stores = new Map<string, Map<string, string>>();

// Values arrive as structured clones and can be undefined, which JSON has no text for.
function toJson(value: unknown): string {
  return JSON.stringify(value === undefined ? null : value);
}

function sizeOf(value: unknown): number {
  return new TextEncoder().encode(toJson(value)).length;
}

function storeOf(user: number, app: string): Map<string, string> {
  const key = `${String(user)}:${app}`;
  let store = stores.get(key);
  if (store === undefined) {
    store = new Map();
    stores.set(key, store);
  }
  return store;
}

export interface BrokerTarget {
  windowId: string;
  app: string;
  user: number;
  permissions: readonly string[];
  /** The element that shows the app's content (the iframe, or the root of a core app); menus are placed from it. */
  content: () => Element | null;
  /** The app called `window.ready`: its loading placeholder goes. Core apps have none. */
  onReady?: () => void;
}

/**
 * What an app window may do, bound to that window. The connection an app talks through decides the window and app
 * here; nothing in a message can point a call at another window or app. Window calls act on this window only.
 *
 * These are the methods of the core apps, and the window methods every app shares. For a third-party app,
 * `appBrokerFor` replaces every method that reaches data (logs, notifications, storage, directory, files, backend
 * tokens) with a call of the daemon's API for apps, where the daemon checks the app's permissions and the user's rights
 * (A11). The permission checks here only answer early. A core app's notification keeps its buttons and data in this
 * tab until it is reloaded.
 */
export function brokerFor(target: BrokerTarget): MethodHandlers {
  const store = storeOf(target.user, target.app);
  const require = (permission: string) => {
    if (!target.permissions.includes(permission)) {
      throw new PithyxError({ code: 'app.permission.denied', params: { permission } });
    }
  };
  return {
    'window.setTitle': ({ title }) => {
      dispatch({
        type: 'setTitle',
        id: target.windowId,
        title: title.trim() === '' ? null : title,
      });
    },
    'window.close': () => {
      dispatch({ type: 'close', id: target.windowId });
    },
    'window.setSize': ({ width, height }) => {
      const window = useDesktop.getState().windows[target.windowId];
      if (window === undefined) {
        throw sdkError('sdk.transport.closed');
      }
      dispatch({
        type: 'move',
        id: target.windowId,
        rect: { ...window.rect, width: Math.round(width), height: Math.round(height) },
      });
      const rect = useDesktop.getState().windows[target.windowId]?.rect ?? window.rect;
      return { width: rect.width, height: rect.height };
    },
    'window.setDirty': ({ dirty }) => {
      setDirty(target.windowId, dirty);
    },
    'window.requestAttention': () => {
      requestAttention(target.windowId);
    },
    'window.ready': () => {
      target.onReady?.();
    },
    setBadge: ({ count }) => {
      setBadge(target.app, count);
    },
    // Guardrail 7: only the window the user works in may open a menu, which takes the keyboard focus.
    'contextMenu.show': async ({ at, items }) => {
      const content = target.content();
      if (content === null || useDesktop.getState().focused !== target.windowId) {
        return null;
      }
      const point = pagePoint(at, content.getBoundingClientRect());
      return showMenu({ windowId: target.windowId, ...point, items });
    },
    // Over plain HTTP the browser may refuse to copy; the desktop then shows the text for the user to copy.
    'clipboard.writeText': async ({ text }) => {
      if (!(await copyText(text))) {
        await offerManualCopy(text);
      }
    },
    openUrl: ({ url }) => {
      openInNewTab(url);
    },
    // Debug entries reach the daemon only while an administrator switched debug on (D305); until the daemon serves
    // that switch (A11) the context says `info`, so they stay in the browser console.
    'log.write': ({ entries }) => {
      writeAppLog(
        target.app,
        entries.filter((entry) => entry.level !== 'debug'),
      );
    },
    // The daemon keeps it in the user's history and sends it back on the notifications topic, which shows the toast.
    notify: async ({ title, body, level, actions, data }) => {
      require('notifications');
      try {
        const notification = unwrap(
          await api.POST('/api/notifications/app', {
            body: {
              app: target.app,
              title,
              body: body ?? null,
              tone: level === 'error' ? 'critical' : (level ?? 'info'),
            },
          }),
        );
        const id = String(notification.id);
        if ((actions !== undefined && actions.length > 0) || data !== undefined) {
          rememberNotification(id, {
            app: target.app,
            windowId: target.windowId,
            actions: actions ?? [],
            data: data ?? null,
          });
        }
        return { id };
      } catch (error) {
        throw toPithyxError(error);
      }
    },
    'session.save': ({ state }) => {
      if (sizeOf(state) > MAX_SESSION) {
        throw new PithyxError({
          code: 'app.storage.too_large',
          params: { maxKb: MAX_SESSION / 1024 },
        });
      }
      const args = useDesktop.getState().windows[target.windowId]?.args;
      dispatch({ type: 'setArgs', id: target.windowId, args: withArgs(args, { session: state }) });
    },
    'storage.get': ({ key }) => {
      const value = store.get(key);
      return value === undefined ? null : (JSON.parse(value) as unknown);
    },
    'storage.set': ({ key, value }) => {
      const json = toJson(value);
      const size = new TextEncoder().encode(json).length;
      const total = [...store].reduce(
        (sum, [name, stored]) => (name === key ? sum : sum + stored.length),
        size,
      );
      if (size > MAX_VALUE || total > MAX_TOTAL) {
        throw new PithyxError({
          code: 'app.storage.too_large',
          params: { maxKb: (size > MAX_VALUE ? MAX_VALUE : MAX_TOTAL) / 1024 },
        });
      }
      store.set(key, json);
    },
    'storage.remove': ({ key }) => {
      store.delete(key);
    },
    'storage.keys': () => [...store.keys()],
  };
}
