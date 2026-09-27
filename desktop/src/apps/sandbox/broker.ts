import { sdkError, PithyxError, type MethodHandlers } from '@pithyx/sdk/host';
import { notify } from '../../shell/toasts';
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
  appName: string;
  user: number;
  permissions: readonly string[];
}

/**
 * What an app window may do, bound to that window. The connection an app talks through decides the window and app
 * here; nothing in a message can point a call at another window or app. Window calls act on this window only.
 *
 * The permission checks here only answer early. The daemon is the authority: once apps are installed on the daemon
 * (M4), every call that reaches data (storage, files, notifications, backend tokens) goes through an endpoint that
 * checks the app's permissions and the user's rights again, and fails with the same codes.
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
    notify: ({ title, body }) => {
      require('notifications');
      notify({ code: 'app.message', params: { app: target.appName, title, body: body ?? '' } });
    },
    'session.save': ({ state }) => {
      if (sizeOf(state) > MAX_SESSION) {
        throw new PithyxError({
          code: 'app.storage.too_large',
          params: { maxKb: MAX_SESSION / 1024 },
        });
      }
      dispatch({ type: 'setArgs', id: target.windowId, args: { session: state } });
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
    'auth.token': () => {
      require('backend');
      // Backends arrive with the app runtime (M4); the daemon will issue the token.
      throw new PithyxError({ code: 'sdk.method.unknown', params: { method: 'auth.token' } });
    },
  };
}
