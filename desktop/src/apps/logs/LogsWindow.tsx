import { coreAppIds } from '../ids';
import type { AppProps } from '../registry';
import { CoreSdkApp } from '../sdk/CoreSdkApp';
import { logsHost } from '../sdk/logs';
import { LogsApp } from './LogsApp';

/** The Log Center in a window: a core app on the SDK. Administrators only. */
export function LogsWindow({ windowId }: AppProps) {
  return (
    <CoreSdkApp
      windowId={windowId}
      app={coreAppIds.logs}
      permissions={[]}
      internal={logsHost}
      Component={LogsApp}
    />
  );
}
