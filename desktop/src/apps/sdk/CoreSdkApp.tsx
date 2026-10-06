import { useQueryClient } from '@tanstack/react-query';
import type { components } from '@pithyx/api-client';
import { createApp, inProcessPair, type PithyxApp } from '@pithyx/sdk';
import { serveApp, type AppConnection, type HostExtensions } from '@pithyx/sdk/host';
import { useEffect, useEffectEvent, useRef, useState, type ComponentType } from 'react';
import { queryKeys } from '../../api/queries';
import { useSizeClass } from '../../wm/sizeClass';
import type { AppProps } from '../registry';
import { setReady } from '../host/connections';
import { brokerFor } from '../sandbox/broker';
import { appContext, PITHYX_VERSION } from './context';
import styles from './CoreSdkApp.module.css';
import { fileBrokerFor } from './fileBroker';
import { useAppLifecycle } from './useAppLifecycle';

type User = components['schemas']['UserInfo'];

/** What a core app built on the SDK receives: the same `PithyxApp` a sandboxed app gets from `connect()`. */
export interface SdkAppProps {
  app: PithyxApp;
}

/**
 * Runs a core app in the desktop context on the SDK's in-process transport. The app gets the same API, events and
 * checks as a sandboxed app; it never calls the daemon or the desktop past the SDK.
 */
export function CoreSdkApp({
  windowId,
  app: appId,
  version = PITHYX_VERSION,
  permissions,
  internal,
  Component,
}: AppProps & {
  app: string;
  /** Core apps ship with the desktop, so their version is the product's. */
  version?: string;
  permissions: readonly string[];
  /** Internal methods for this core app (apps/sdk/internal.ts); `emit` sends an internal event to this window. */
  internal?: (emit: (event: string, data?: unknown) => void) => {
    extensions: HostExtensions;
    close: () => void;
  };
  Component: ComponentType<SdkAppProps>;
}) {
  const root = useRef<HTMLDivElement>(null);
  const connection = useRef<AppConnection | null>(null);
  const [app, setApp] = useState<PithyxApp | null>(null);
  const client = useQueryClient();
  const sizeClass = useSizeClass();

  useAppLifecycle(windowId, connection);

  const open = useEffectEvent(() => {
    const [hostEnd, appEnd] = inProcessPair();
    setReady(windowId, false);
    const user = client.getQueryData<User>(queryKeys.session);
    const files = fileBrokerFor({
      permissions,
      emit: (event, data) => {
        connection.current?.emit(event, data);
      },
    });
    const host = internal?.((event, data) => {
      (connection.current?.emit as ((event: string, data: unknown) => void) | undefined)?.(
        event,
        data ?? {},
      );
    });
    connection.current = serveApp(hostEnd, {
      context: appContext({
        client,
        windowId,
        app: { id: appId, version },
        permissions,
        sizeClass,
      }),
      methods: {
        ...brokerFor({
          windowId,
          app: appId,
          user: user?.id ?? 0,
          permissions,
          content: () => root.current,
        }),
        ...files.methods,
      },
      onReady: () => {
        setReady(windowId, true);
      },
      ...(host === undefined ? {} : { extensions: host.extensions }),
    });
    let active = true;
    void createApp(appEnd, { themeRoot: root.current }).then((connected) => {
      if (active) {
        setApp(connected);
      }
    });
    return () => {
      active = false;
      files.close();
      host?.close();
      connection.current?.close();
      connection.current = null;
    };
  });

  // One connection per window, for as long as it is open: synchronization with the SDK, not data fetching. The
  // lifecycle hook sends every later change as an event.
  useEffect(() => open(), [windowId]);

  return (
    <div ref={root} className={styles.root} data-size-class={sizeClass}>
      {app !== null && <Component app={app} />}
    </div>
  );
}
