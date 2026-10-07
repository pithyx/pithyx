import type { Permission } from '@pithyx/sdk';
import { useDesktop } from '../../wm/store';
import { useInstalledApps } from '../installed';
import type { AppProps } from '../registry';
import { SandboxApp } from './SandboxApp';

/** The window of an installed app: its frontend from the daemon in the sandboxed iframe, with the permissions it holds. */
export function ThirdPartyWindow({ windowId }: AppProps) {
  const appId = useDesktop((state) => state.windows[windowId]?.app);
  const app = useInstalledApps((state) => state.apps?.find((entry) => entry.id === appId));
  // An app that left the topic (uninstalled, disabled or no longer granted) has its windows closed by the desktop.
  if (app === undefined) {
    return null;
  }
  return (
    <SandboxApp
      windowId={windowId}
      app={app.id}
      entry={app.entry}
      // The daemon lists the permissions of the manifest the app still holds; the SDK names the same ones.
      permissions={app.permissions as Permission[]}
    />
  );
}
