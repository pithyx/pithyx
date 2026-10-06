import type { QueryClient } from '@tanstack/react-query';
import type { components } from '@pithyx/api-client';
import { PROTOCOL_VERSION, SDK_VERSION, type AppContext, type SizeClass } from '@pithyx/sdk';
import i18next from 'i18next';
import { formatLocale } from '../../i18n';
import { queryKeys } from '../../api/queries';
import { useDesktop } from '../../wm/store';
import { currentTheme } from '../sandbox/theme';

type User = components['schemas']['UserInfo'];
type Setup = components['schemas']['SetupStatus'];

/** The Pithyx version this desktop was built with (daemon/Directory.Build.props). */
export const PITHYX_VERSION = __PITHYX_VERSION__;

/**
 * The init context of an app window, the same for sandboxed and core apps. Until the daemon serves the app context
 * (A11), the desktop fills in what it knows: the user's groups are empty, there is no backend and no storage, debug
 * logs stay in the browser console, and the box counts as reachable.
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
  const args = window?.args as { session?: unknown } | null | undefined;
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
    launch: { args: null },
    session: args?.session ?? null,
    permissions: [...options.permissions],
    box: { name: setup?.hostname ?? location.hostname, version: PITHYX_VERSION },
    visible: window?.minimized !== true,
    online: true,
    logLevel: 'info',
    backend: null,
    storage: [],
    host: { version: SDK_VERSION, protocol: PROTOCOL_VERSION },
  };
}
