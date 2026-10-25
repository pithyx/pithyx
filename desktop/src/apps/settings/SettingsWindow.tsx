import { useQueryClient } from '@tanstack/react-query';
import { coreAppIds } from '../ids';
import type { AppProps } from '../registry';
import { CoreSdkApp } from '../sdk/CoreSdkApp';
import { backupHost } from '../sdk/backup';
import { adminStorageHost } from '../sdk/internal';
import { networkHost } from '../sdk/network';
import { notificationsHost } from '../sdk/notifications';
import { proxyHost } from '../sdk/proxy';
import { systemHost } from '../sdk/system';
import { tlsHost } from '../sdk/tls';
import { updatesHost } from '../sdk/updates';
import { upsHost } from '../sdk/ups';
import { usersHost } from '../sdk/users';
import { SettingsApp } from './SettingsApp';

/** Settings in a window: a core app on the SDK with the internal administrator calls. Administrators only. */
export function SettingsWindow({ windowId }: AppProps) {
  const queryClient = useQueryClient();
  return (
    <CoreSdkApp
      windowId={windowId}
      app={coreAppIds.settings}
      permissions={['notifications']}
      internal={(emit) => {
        const storage = adminStorageHost(emit);
        const network = networkHost(emit);
        const updates = updatesHost(emit);
        const ups = upsHost(emit);
        return {
          extensions: {
            ...storage.extensions,
            ...network.extensions,
            ...updates.extensions,
            ...ups.extensions,
            ...usersHost(),
            ...systemHost(queryClient),
            ...backupHost(),
            ...tlsHost(),
            ...proxyHost(),
            ...notificationsHost(),
          },
          close: () => {
            storage.close();
            network.close();
            updates.close();
            ups.close();
          },
        };
      }}
      Component={SettingsApp}
    />
  );
}
