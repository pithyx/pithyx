import type { AppProps } from '../registry';
import { CoreSdkApp } from '../sdk/CoreSdkApp';
import { monitorHost } from '../sdk/monitor';
import { MonitorApp } from './MonitorApp';

/** The Resource Monitor in a window: a core app on the SDK. Administrators only. */
export function MonitorWindow({ windowId }: AppProps) {
  return (
    <CoreSdkApp
      windowId={windowId}
      app="monitor"
      permissions={[]}
      internal={monitorHost}
      Component={MonitorApp}
    />
  );
}
