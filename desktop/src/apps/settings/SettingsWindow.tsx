import type { AppProps } from '../registry';
import { CoreSdkApp } from '../sdk/CoreSdkApp';
import { adminStorageHost } from '../sdk/internal';
import { SettingsApp } from './SettingsApp';

/** Settings in a window: a core app on the SDK with the internal administrator calls. Administrators only. */
export function SettingsWindow({ windowId }: AppProps) {
  return (
    <CoreSdkApp
      windowId={windowId}
      app="settings"
      permissions={['notifications']}
      internal={adminStorageHost}
      Component={SettingsApp}
    />
  );
}
