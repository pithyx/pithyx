import { useQueryClient } from '@tanstack/react-query';
import type { components } from '@pithyx/api-client';
import { portEndpoint } from '@pithyx/sdk';
import { serveApp, type AppConnection } from '@pithyx/sdk/host';
import { useEffect, useEffectEvent, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AppIcon } from '../../shell/AppIcon';
import { manifestText } from '../installed';
import { useDesktop } from '../../wm/store';
import { queryKeys } from '../../api/queries';
import { realtime } from '../../realtime/connection';
import { useSizeClass } from '../../wm/sizeClass';
import { useAppName } from '../names';
import type { AppProps } from '../registry';
import { brokerFor } from './broker';
import { appApiFor } from './appApi';
import { appBrokerFor } from './appBroker';
import { registerFileAdopter, setReady } from '../host/connections';
import { contextChanges, launchOf, loadingPhase, pendingFileOf } from '../host/rules';
import styles from './SandboxApp.module.css';
import { appContext, storageOf, type DaemonAppContext } from '../sdk/context';
import { useAppLifecycle } from '../sdk/useAppLifecycle';

type User = components['schemas']['UserInfo'];

/** The manifest's `window.loading`: the text while the backend starts, and whether to wait for `window.ready`. */
export interface WindowLoading {
  text: Readonly<Record<string, string>> | undefined;
  untilReady: boolean;
}

/** The longest the placeholder covers a window that does not call `window.ready` (D354). */
const READY_TIMEOUT = 30_000;

/**
 * A third-party app in a sandboxed iframe from the same host. `allow-scripts allow-forms` without
 * `allow-same-origin` gives it an opaque origin: no desktop cookies, no storage, no access to this page. It talks to the
 * desktop only through the MessagePort it gets on load, and that port is its identity: the window's app and instance
 * come from here, never from what the app sends.
 *
 * The daemon decides what the app may do (A11). The window follows the app's context topic `apps/<id>/context`, which
 * is also the window lease: the connection starts once its first state is there, a change reaches the app as
 * `permissionsChange`, `logLevelChange` or `storageChange`. When the user loses the app (a revoked grant, a disabled or
 * removed app) its events stop and the app leaves the topic `apps`, which closes the window. Calls that reach data go to `/api/app/**` with this window's host token.
 */
export function SandboxApp({
  windowId,
  app,
  entry,
  icon,
  loading,
}: AppProps & { app: string; entry: string; icon: string | null; loading: WindowLoading }) {
  const frame = useRef<HTMLIFrameElement>(null);
  const connection = useRef<AppConnection | null>(null);
  const broker = useRef<ReturnType<typeof appBrokerFor> | null>(null);
  const daemonContext = useRef<DaemonAppContext | undefined>(undefined);
  const waiting = useRef(false);
  const generation = useRef(0);
  const [daemon] = useState(() => appApiFor(app, windowId));
  const client = useQueryClient();
  const appName = useAppName();
  const sizeClass = useSizeClass();
  const { t, i18n } = useTranslation();
  const [connected, setConnected] = useState(false);
  const [appReady, setAppReady] = useState(false);
  const [timedOut, setTimedOut] = useState(false);
  const [backend, setBackend] = useState<DaemonAppContext['backend'] | undefined>(undefined);
  const phase = loadingPhase({
    connected,
    appReady,
    untilReady: loading.untilReady,
    timedOut,
    backendStatus: backend?.status,
    backendReason: backend?.reason,
  });

  async function connect() {
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
    const mine = ++generation.current;
    connection.current?.close();
    broker.current?.close();
    setReady(windowId, false);
    broker.current = appBrokerFor({
      app,
      windowId,
      daemon,
      logLevel: () => daemonContext.current?.logLevel ?? 'info',
      permissions: () => daemonContext.current?.permissions ?? [],
      emit: (event, data) => {
        connection.current?.emit(event, data);
      },
    });
    const args = useDesktop.getState().windows[windowId]?.args;
    const pending = pendingFileOf(args);
    const file = pending === undefined ? undefined : await broker.current.adopt(pending);
    // The frame may have loaded again while the handle was made; that load connects it.
    if (generation.current !== mine) {
      return;
    }
    const channel = new MessageChannel();
    const user = client.getQueryData<User>(queryKeys.session);
    connection.current = serveApp(portEndpoint(channel.port1), {
      context: appContext({
        client,
        windowId,
        app: { id: app, version: context.version },
        permissions: context.permissions,
        sizeClass,
        daemon: context,
        launch: file === undefined ? launchOf(args) : { ...launchOf(args), file },
      }),
      methods: {
        ...brokerFor({
          windowId,
          app,
          user: user?.id ?? 0,
          permissions: context.permissions,
          content: () => frame.current,
          onReady: () => {
            setAppReady(true);
          },
        }),
        ...broker.current.methods,
      },
      onReady: () => {
        setReady(windowId, true);
        setConnected(true);
      },
    });
    // The iframe has an opaque origin, so no target origin can be named; the transferred port carries the trust.
    target.postMessage({ type: 'pithyx:connect' }, '*', [channel.port2]);
  }

  const onContext = useEffectEvent((data: unknown) => {
    const next = data as DaemonAppContext;
    const previous = daemonContext.current;
    daemonContext.current = next;
    setBackend(next.backend);
    if (previous === undefined) {
      if (waiting.current) {
        void connect();
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

  // A file opened with the app while its window is open gets a handle from the window's own broker.
  useEffect(
    () =>
      registerFileAdopter(
        windowId,
        async (file) => (await broker.current?.adopt(file)) ?? undefined,
      ),
    [windowId],
  );

  // The placeholder never covers a window for good, also when the app forgets to call window.ready.
  useEffect(() => {
    const timer = setTimeout(() => {
      setTimedOut(true);
    }, READY_TIMEOUT);
    return () => {
      clearTimeout(timer);
    };
  }, []);

  // The window's folder watches, jobs and share list end with the window.
  useEffect(
    () => () => {
      broker.current?.close();
      broker.current = null;
    },
    [],
  );

  const name = appName(app);
  return (
    <div className={styles.host}>
      <iframe
        ref={frame}
        className={styles.frame}
        src={entry}
        title={name}
        sandbox="allow-scripts allow-forms"
        referrerPolicy="no-referrer"
        onLoad={() => {
          void connect();
        }}
      />
      {phase !== 'ready' && (
        <div
          className={styles.loading}
          role="status"
          aria-label={t('appHost.loading.label', { name })}
        >
          <AppIcon app={{ icon, tone: 'graphite' }} size={56} />
          <p className={styles.name}>{name}</p>
          <span className={styles.indicator} aria-hidden />
          {phase === 'starting' && (
            <p className={styles.text}>
              {loading.text === undefined
                ? t('appHost.loading.starting', { name })
                : manifestText(loading.text, i18n.language)}
            </p>
          )}
          {phase === 'storage' && (
            <p className={styles.text}>{t('appHost.loading.storage', { name })}</p>
          )}
          {phase === 'pool' && <p className={styles.text}>{t('appHost.loading.pool', { name })}</p>}
        </div>
      )}
    </div>
  );
}
