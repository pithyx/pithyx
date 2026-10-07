import { useQueryClient } from '@tanstack/react-query';
import type { components } from '@pithyx/api-client';
import { portEndpoint } from '@pithyx/sdk';
import { serveApp, type AppConnection } from '@pithyx/sdk/host';
import { useEffect, useEffectEvent, useRef, useState } from 'react';
import { queryKeys } from '../../api/queries';
import { realtime } from '../../realtime/connection';
import { useSizeClass } from '../../wm/sizeClass';
import { dispatch } from '../../wm/store';
import { useAppName } from '../names';
import type { AppProps } from '../registry';
import { brokerFor } from './broker';
import { appApiFor } from './appApi';
import { appBrokerFor } from './appBroker';
import { setReady } from '../host/connections';
import { contextChanges } from '../host/rules';
import styles from './SandboxApp.module.css';
import { appContext, storageOf, type DaemonAppContext } from '../sdk/context';
import { useAppLifecycle } from '../sdk/useAppLifecycle';

type User = components['schemas']['UserInfo'];

/**
 * A third-party app in a sandboxed iframe from the same host. `allow-scripts allow-forms` without
 * `allow-same-origin` gives it an opaque origin: no desktop cookies, no storage, no access to this page. It talks to the
 * desktop only through the MessagePort it gets on load, and that port is its identity: the window's app and instance
 * come from here, never from what the app sends.
 *
 * The daemon decides what the app may do (A11). The window follows the app's context topic `apps/<id>/context`, which
 * is also the window lease: the connection starts once its first state is there, a change reaches the app as
 * `permissionsChange`, `logLevelChange` or `storageChange`, and when the user loses the app (a revoked grant, a disabled
 * or removed app) the window closes. Calls that reach data go to `/api/app/**` with this window's host token.
 */
export function SandboxApp({ windowId, app, entry }: AppProps & { app: string; entry: string }) {
  const frame = useRef<HTMLIFrameElement>(null);
  const connection = useRef<AppConnection | null>(null);
  const daemonContext = useRef<DaemonAppContext | undefined>(undefined);
  const waiting = useRef(false);
  const [daemon] = useState(() => appApiFor(app, windowId));
  const client = useQueryClient();
  const appName = useAppName();
  const sizeClass = useSizeClass();

  function connect() {
    const target = frame.current?.contentWindow;
    const context = daemonContext.current;
    if (target == null) {
      return;
    }
    if (context === undefined) {
      // The frame loaded before the daemon's context arrived; the context's first state connects it.
      waiting.current = true;
      return;
    }
    waiting.current = false;
    connection.current?.close();
    setReady(windowId, false);
    const channel = new MessageChannel();
    const user = client.getQueryData<User>(queryKeys.session);
    connection.current = serveApp(portEndpoint(channel.port1), {
      context: appContext({
        client,
        windowId,
        app: { id: app, version: context.version ?? '' },
        permissions: context.permissions,
        sizeClass,
        daemon: context,
      }),
      methods: {
        ...brokerFor({
          windowId,
          app,
          user: user?.id ?? 0,
          permissions: context.permissions,
          content: () => frame.current,
        }),
        ...appBrokerFor({
          app,
          windowId,
          daemon,
          logLevel: () => daemonContext.current?.logLevel ?? 'info',
        }),
      },
      onReady: () => {
        setReady(windowId, true);
      },
    });
    // The iframe has an opaque origin, so no target origin can be named; the transferred port carries the trust.
    target.postMessage({ type: 'pithyx:connect' }, '*', [channel.port2]);
  }

  const onContext = useEffectEvent((data: unknown) => {
    const next = data as DaemonAppContext;
    const previous = daemonContext.current;
    daemonContext.current = next;
    if (!next.access) {
      dispatch({ type: 'close', id: windowId });
      return;
    }
    if (previous === undefined) {
      if (waiting.current) {
        connect();
      }
      return;
    }
    const view = (state: DaemonAppContext) => ({ ...state, storage: storageOf(state) });
    const changes = contextChanges(view(previous), view(next));
    if (changes.permissions !== undefined) {
      // Tokens carry no permissions, but a fresh one confirms the app is still there for the user.
      daemon.reset();
    }
    for (const change of changes.events) {
      connection.current?.emit(change.event, change.data as never);
    }
  });

  // The daemon's context lives on the WebSocket, outside React: following it is synchronization, not data fetching.
  useEffect(() => realtime.listen(`apps/${app}/context`, onContext), [app]);

  useAppLifecycle(windowId, connection);

  return (
    <iframe
      ref={frame}
      className={styles.frame}
      src={entry}
      title={appName(app)}
      sandbox="allow-scripts allow-forms"
      referrerPolicy="no-referrer"
      onLoad={connect}
    />
  );
}
