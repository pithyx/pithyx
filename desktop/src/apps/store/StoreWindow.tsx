import { coreAppIds } from '../ids';
import type { AppProps } from '../registry';
import { CoreSdkApp } from '../sdk/CoreSdkApp';
import { storeHost } from '../sdk/store';
import { usersHost } from '../sdk/users';
import { StoreApp } from './StoreApp';

/**
 * The Store in a window: a core app on the SDK with the internal administrator calls, and the shares to pick a folder
 * for a storage request in. Administrators only (D320).
 */
export function StoreWindow({ windowId }: AppProps) {
  return (
    <CoreSdkApp
      windowId={windowId}
      app={coreAppIds.store}
      permissions={['files.shares']}
      internal={(emit) => {
        const store = storeHost(emit);
        return { extensions: { ...store.extensions, ...usersHost() }, close: store.close };
      }}
      Component={StoreApp}
    />
  );
}
