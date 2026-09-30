import type { AppProps } from '../registry';
import { CoreSdkApp } from '../sdk/CoreSdkApp';
import { adminStorageHost } from '../sdk/internal';
import { StorageApp } from './StorageApp';

/** Storage in a window: a core app on the SDK with the internal administrator calls. Administrators only. */
export function StorageWindow({ windowId }: AppProps) {
  return (
    <CoreSdkApp
      windowId={windowId}
      app="storage"
      permissions={['notifications']}
      internal={adminStorageHost}
      Component={StorageApp}
    />
  );
}
