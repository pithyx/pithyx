import { useDesktop } from '../../wm/store';
import { useInstalledApps } from '../installed';
import type { AppProps } from '../registry';
import { SandboxApp, type WindowLoading } from './SandboxApp';

/** The manifest's `window.loading`, which the daemon passes on as written. */
function loadingOf(window: unknown): WindowLoading {
  const loading = (window as { loading?: Partial<WindowLoading> } | null)?.loading;
  return { text: loading?.text, untilReady: loading?.untilReady === true };
}

/** The window of an installed app: its frontend from the daemon in the sandboxed iframe; its permissions come from its context. */
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
      icon={app.icon}
      loading={loadingOf(app.window)}
    />
  );
}
