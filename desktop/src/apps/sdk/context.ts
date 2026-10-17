import type { QueryClient } from '@tanstack/react-query';
import type { components } from '@pithyx/api-client';
import {
  PROTOCOL_VERSION,
  SDK_VERSION,
  type AppContext,
  type BackendStatus,
  type BackendUnavailableReason,
  type SizeClass,
} from '@pithyx/sdk';
import i18next from 'i18next';
import { formatLocale } from '../../i18n';
import { queryKeys } from '../../api/queries';
import { useRealtime } from '../../realtime/connection';
import { useDesktop } from '../../wm/store';
import { isOnline, isWindowVisible, launchOf, sessionOf } from '../host/rules';
import { currentTheme } from '../sandbox/theme';

type User = components['schemas']['UserInfo'];
type Setup = components['schemas']['SetupStatus'];
/** What the daemon decides about a third-party app for this user (topic `apps/<id>/context`). */
export type DaemonAppContext = components['schemas']['AppContextState'];

/** The Pithyx version this desktop was built with (daemon/Directory.Build.props). */
export const PITHYX_VERSION = __PITHYX_VERSION__;

/** The storage requests as the SDK names their states and access. */
export function storageOf(daemon: DaemonAppContext): AppContext['storage'] {
  return daemon.storage.map((storage) => ({
    id: storage.id,
    state: storage.state,
    access:
      storage.access === 'readWrite' ? 'readWrite' : storage.access === 'read' ? 'read' : null,
  }));
}

/**
 * The init context of an app window, the same for sandboxed and core apps. The desktop fills in what is its own (the
 * device's language, formats, time zone and theme, the window) and the user from the session; for a third-party app
 * the daemon's context (A11) brings its permissions, log level, backend and storage. Core apps have no backend or
 * storage, and their debug logs stay in the browser console.
 */
export function appContext(options: {
  client: QueryClient;
  windowId: string;
  app: { id: string; version: string };
  permissions: readonly string[];
  sizeClass: SizeClass;
  daemon?: DaemonAppContext;
  /** The launch arguments, when the window made a handle for the file it was launched with. */
  launch?: AppContext['launch'];
}): Omit<AppContext, 'capabilities'> {
  const { client, windowId } = options;
  const user = client.getQueryData<User>(queryKeys.session);
  const setup = client.getQueryData<Setup>(queryKeys.setup);
  const window = useDesktop.getState().windows[windowId];
  return {
    app: options.app,
    instance: windowId,
    user: {
      id: user?.id ?? 0,
      name: user?.username ?? '',
      displayName: user?.displayName ?? null,
      groups: (user?.groups ?? []).map((group) => ({ id: group.id, name: group.name })),
      isAdmin: user?.isAdmin ?? false,
      twoFactor: user?.totpEnabled ?? false,
    },
    language: i18next.language,
    locale: formatLocale(),
    timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
    theme: currentTheme(),
    sizeClass: options.sizeClass,
    launch: options.launch ?? launchOf(window?.args),
    session: sessionOf(window?.args),
    permissions: [...(options.daemon?.permissions ?? options.permissions)],
    box: { name: setup?.hostname ?? location.hostname, version: PITHYX_VERSION },
    visible: isWindowVisible(window?.minimized === true, document.visibilityState === 'hidden'),
    online: isOnline(useRealtime.getState().status),
    logLevel: options.daemon?.logLevel ?? 'info',
    backend:
      options.daemon?.backend == null
        ? null
        : {
            status: options.daemon.backend.status as BackendStatus,
            ...(options.daemon.backend.reason == null
              ? {}
              : { reason: options.daemon.backend.reason as BackendUnavailableReason }),
            endpoints: [...options.daemon.backend.endpoints],
          },
    storage: options.daemon === undefined ? [] : storageOf(options.daemon),
    host: { version: SDK_VERSION, protocol: PROTOCOL_VERSION },
  };
}
