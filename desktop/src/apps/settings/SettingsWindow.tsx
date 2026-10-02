import { useQueryClient } from '@tanstack/react-query';
import type { AppProps } from '../registry';
import { CoreSdkApp } from '../sdk/CoreSdkApp';
import { adminStorageHost } from '../sdk/internal';
import { systemHost } from '../sdk/system';
import { usersHost } from '../sdk/users';
import { SettingsApp } from './SettingsApp';

/** Settings in a window: a core app on the SDK with the internal administrator calls. Administrators only. */
export function SettingsWindow({ windowId }: AppProps) {
  const queryClient = useQueryClient();
  return (
    <CoreSdkApp
      windowId={windowId}
      app="settings"
      permissions={['notifications']}
      internal={(emit) => {
        const storage = adminStorageHost(emit);
        return {
          extensions: { ...storage.extensions, ...usersHost(), ...systemHost(queryClient) },
          close: storage.close,
        };
      }}
      Component={SettingsApp}
    />
  );
}
