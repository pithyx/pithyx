import type { QueryClient } from '@tanstack/react-query';
import type { components } from '@pithyx/api-client';
import { PROTOCOL_VERSION, SDK_VERSION, type AppContext, type SizeClass } from '@pithyx/sdk';
import i18next from 'i18next';
import { formatLocale } from '../../i18n';
import { queryKeys } from '../../api/queries';
import { useRealtime } from '../../realtime/connection';
import { useDesktop } from '../../wm/store';
import { isOnline, isWindowVisible, launchOf, sessionOf } from '../host/rules';
import { currentTheme } from '../sandbox/theme';

type User = components['schemas']['UserInfo'];
type Setup = components['schemas']['SetupStatus'];

/** The Pithyx version this desktop was built with (daemon/Directory.Build.props). */
export const PITHYX_VERSION = __PITHYX_VERSION__;

/**
 * The init context of an app window, the same for sandboxed and core apps. Until the daemon serves the app context
 * (A11), the desktop fills in what it knows: the user's groups are empty, there is no backend and no storage, and debug
 * logs stay in the browser console.
 */
export function appContext(options: {
  client: QueryClient;
  windowId: string;
  app: { id: string; version: string };
  permissions: readonly string[];
  sizeClass: SizeClass;
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
      groups: [],
      isAdmin: user?.isAdmin ?? false,
      twoFactor: user?.totpEnabled ?? false,
    },
    language: i18next.language,
    locale: formatLocale(),
    timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
    theme: currentTheme(),
    sizeClass: options.sizeClass,
    launch: launchOf(window?.args),
    session: sessionOf(window?.args),
    permissions: [...options.permissions],
    box: { name: setup?.hostname ?? location.hostname, version: PITHYX_VERSION },
    visible: isWindowVisible(window?.minimized === true, document.visibilityState === 'hidden'),
    online: isOnline(useRealtime.getState().status),
    logLevel: 'info',
    backend: null,
    storage: [],
    host: { version: SDK_VERSION, protocol: PROTOCOL_VERSION },
  };
}
