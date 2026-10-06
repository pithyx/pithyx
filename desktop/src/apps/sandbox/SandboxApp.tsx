import { useQueryClient } from '@tanstack/react-query';
import type { components } from '@pithyx/api-client';
import { portEndpoint, type Permission } from '@pithyx/sdk';
import { serveApp, type AppConnection } from '@pithyx/sdk/host';
import { useRef } from 'react';
import { queryKeys } from '../../api/queries';
import { useSizeClass } from '../../wm/sizeClass';
import { useAppName } from '../names';
import type { AppProps } from '../registry';
import { brokerFor } from './broker';
import styles from './SandboxApp.module.css';
import { appContext } from '../sdk/context';
import { useAppLifecycle } from '../sdk/useAppLifecycle';

type User = components['schemas']['UserInfo'];

/**
 * A third-party app in a sandboxed iframe from the same host. `allow-scripts allow-forms` without
 * `allow-same-origin` gives it an opaque origin: no desktop cookies, no storage, no access to this page. It talks to the
 * desktop only through the MessagePort it gets on load, and that port is its identity.
 */
export function SandboxApp({
  windowId,
  app,
  entry,
  permissions,
}: AppProps & { app: string; entry: string; permissions: readonly Permission[] }) {
  const frame = useRef<HTMLIFrameElement>(null);
  const connection = useRef<AppConnection | null>(null);
  const client = useQueryClient();
  const appName = useAppName();
  const sizeClass = useSizeClass();

  function connect() {
    const target = frame.current?.contentWindow;
    if (target == null) {
      return;
    }
    connection.current?.close();
    const channel = new MessageChannel();
    const user = client.getQueryData<User>(queryKeys.session);
    connection.current = serveApp(portEndpoint(channel.port1), {
      context: appContext({
        client,
        windowId,
        app: { id: app, version: '0.1.0' },
        permissions,
        sizeClass,
      }),
      methods: brokerFor({
        windowId,
        app,
        user: user?.id ?? 0,
        permissions,
      }),
    });
    // The iframe has an opaque origin, so no target origin can be named; the transferred port carries the trust.
    target.postMessage({ type: 'pithyx:connect' }, '*', [channel.port2]);
  }

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
