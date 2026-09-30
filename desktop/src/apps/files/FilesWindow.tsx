import type { AppProps } from '../registry';
import { CoreSdkApp } from '../sdk/CoreSdkApp';
import { filesHost } from '../sdk/filesInternal';
import { FilesApp } from './FilesApp';

/** The Files app in a window: a core app on the SDK's in-process transport, with the right to browse shares, and its own calls for thumbnails and the viewer. */
export function FilesWindow({ windowId }: AppProps) {
  return (
    <CoreSdkApp
      windowId={windowId}
      app="files"
      permissions={['files.shares', 'notifications']}
      internal={filesHost}
      Component={FilesApp}
    />
  );
}
