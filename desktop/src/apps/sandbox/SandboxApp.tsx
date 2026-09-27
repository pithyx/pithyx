import { useQueryClient } from '@tanstack/react-query';
import type { components } from '@vyxis/api-client';
import {
  BEFORE_CLOSE_TIMEOUT,
  BEFORE_LOGOUT_TIMEOUT,
  PROTOCOL_VERSION,
  SDK_VERSION,
  portEndpoint,
} from '@vyxis/sdk';
import { serveApp, type AppConnection } from '@vyxis/sdk/host';
import i18next from 'i18next';
import { useEffect, useRef } from 'react';
import { queryKeys } from '../../api/queries';
import { guardClose } from '../../wm/commands';
import { useSizeClass } from '../../wm/sizeClass';
import { useDesktop } from '../../wm/store';
import { useAppName } from '../names';
import { onBeforeLogout } from '../lifecycle';
import type { AppProps } from '../registry';
import { brokerFor } from './broker';
import styles from './SandboxApp.module.css';
import { currentTheme, onThemeChange } from './theme';

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
}: AppProps & { app: string; entry: string; permissions: readonly string[] }) {
  const frame = useRef<HTMLIFrameElement>(null);
  const connection = useRef<AppConnection | null>(null);
  const client = useQueryClient();
  const appName = useAppName();
  const sizeClass = useSizeClass();
  const focused = useDesktop((state) => state.focused === windowId);
  const minimized = useDesktop((state) => state.windows[windowId]?.minimized === true);

  function connect() {
    const target = frame.current?.contentWindow;
    if (target == null) {
      return;
    }
    connection.current?.close();
    const channel = new MessageChannel();
    const user = client.getQueryData<User>(queryKeys.session);
    const args = useDesktop.getState().windows[windowId]?.args as
      { session?: unknown } | null | undefined;
    connection.current = serveApp(portEndpoint(channel.port1), {
      context: {
        app: { id: app, version: '0.1.0' },
        instance: windowId,
        user: {
          id: user?.id ?? 0,
          name: user?.username ?? '',
          displayName: user?.displayName ?? null,
        },
        locale: i18next.language,
        timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
        theme: currentTheme(),
        sizeClass,
        launch: { args: null },
        session: args?.session ?? null,
        permissions: [...permissions],
        host: { version: SDK_VERSION, protocol: PROTOCOL_VERSION },
      },
      methods: brokerFor({
        windowId,
        app,
        appName: appName(app),
        user: user?.id ?? 0,
        permissions,
      }),
    });
    // The iframe has an opaque origin, so no target origin can be named; the transferred port carries the trust.
    target.postMessage({ type: 'vyxis:connect' }, '*', [channel.port2]);
  }

  // Lifecycle events follow the window manager, the theme and the language (things outside React).
  useEffect(() => {
    connection.current?.emit(focused ? 'focus' : 'blur', {});
  }, [focused]);
  useEffect(() => {
    connection.current?.emit(minimized ? 'minimize' : 'restore', {});
  }, [minimized]);
  useEffect(() => {
    connection.current?.emit('sizeClassChange', { sizeClass });
  }, [sizeClass]);
  useEffect(() => {
    const onLanguage = (language: string) => {
      connection.current?.emit('localeChange', {
        locale: language,
        timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
      });
    };
    i18next.on('languageChanged', onLanguage);
    const stopTheme = onThemeChange(() => {
      connection.current?.emit('themeChange', currentTheme());
    });
    const stopGuard = guardClose(windowId, async () => {
      const answer = await connection.current?.ask('beforeClose', {}, BEFORE_CLOSE_TIMEOUT);
      // An app that does not answer in time does not keep its window.
      return answer?.allow ?? true;
    });
    const stopLogout = onBeforeLogout(async () =>
      connection.current?.ask('beforeLogout', {}, BEFORE_LOGOUT_TIMEOUT),
    );
    return () => {
      i18next.off('languageChanged', onLanguage);
      stopTheme();
      stopGuard();
      stopLogout();
      connection.current?.close();
      connection.current = null;
    };
  }, [windowId]);

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
