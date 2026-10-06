import { coreAppIds } from '../ids';
import type { AppProps } from '../registry';
import { CoreSdkApp } from '../sdk/CoreSdkApp';
import { viewerHost } from '../sdk/viewerInternal';
import { ViewerApp } from './ViewerApp';

/** The viewer in a window: a core app on the SDK that reads the file it was opened with from its session. */
export function ViewerWindow({ windowId }: AppProps) {
  return (
    <CoreSdkApp
      windowId={windowId}
      app={coreAppIds.viewer}
      permissions={['files.shares', 'notifications']}
      internal={viewerHost}
      Component={ViewerApp}
    />
  );
}
