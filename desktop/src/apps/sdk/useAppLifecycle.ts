import { BEFORE_CLOSE_TIMEOUT, BEFORE_UNLOAD_TIMEOUT } from '@pithyx/sdk';
import type { AppConnection } from '@pithyx/sdk/host';
import i18next from 'i18next';
import { useEffect, useSyncExternalStore, type RefObject } from 'react';
import { formatLocale, subscribeFormats } from '../../i18n';
import { useRealtime } from '../../realtime/connection';
import { guardClose } from '../../wm/commands';
import { useSizeClass } from '../../wm/sizeClass';
import { useDesktop } from '../../wm/store';
import { isReady, registerWindow } from '../host/connections';
import { answerStep, closeStep, isOnline, isWindowVisible } from '../host/rules';
import { offerForceClose, useHost } from '../host/store';
import { onBeforeUnload } from '../lifecycle';
import { currentTheme, onThemeChange } from '../sandbox/theme';

function subscribeTabVisibility(callback: () => void): () => void {
  document.addEventListener('visibilitychange', callback);
  return () => {
    document.removeEventListener('visibilitychange', callback);
  };
}

const tabHidden = () => document.visibilityState === 'hidden';

/**
 * Closes a window the user asked to close once its app agrees: a clean window (`setDirty(false)`) at once, otherwise
 * after `beforeClose`. An app that does not answer in time gets the "not responding" dialog (D62); an app that never
 * finished connecting has nothing to save.
 */
async function mayClose(
  windowId: string,
  connection: RefObject<AppConnection | null>,
): Promise<boolean> {
  if (closeStep(useHost.getState().dirty[windowId]) === 'close' || !isReady(windowId)) {
    return true;
  }
  const answer = await connection.current?.ask('beforeClose', {}, BEFORE_CLOSE_TIMEOUT);
  switch (answerStep(answer)) {
    case 'close':
      return true;
    case 'keep':
      return false;
    case 'offer':
      // A window that closed while the desktop waited (the app closed it itself) needs no question.
      return useDesktop.getState().windows[windowId] === undefined || offerForceClose(windowId);
  }
}

/**
 * Sends an app window its lifecycle events (focus, minimize and visibility, the connection to the box, size class,
 * theme, language), asks it before the window closes and before it goes away (`beforeUnload`). The same for sandboxed
 * and core apps: only the connection's transport differs.
 */
export function useAppLifecycle(
  windowId: string,
  connection: RefObject<AppConnection | null>,
): void {
  const sizeClass = useSizeClass();
  const focused = useDesktop((state) => state.focused === windowId);
  const minimized = useDesktop((state) => state.windows[windowId]?.minimized === true);
  const hidden = useSyncExternalStore(subscribeTabVisibility, tabHidden);
  const visible = isWindowVisible(minimized, hidden);
  const online = useRealtime((state) => isOnline(state.status));

  // Lifecycle events follow the window manager, the tab, the connection, the theme and the language (things outside
  // React). The first run of each only repeats what the init context says, which the connection drops before the
  // handshake.
  useEffect(() => {
    connection.current?.emit(focused ? 'focus' : 'blur', {});
  }, [connection, focused]);
  useEffect(() => {
    connection.current?.emit(minimized ? 'minimize' : 'restore', {});
  }, [connection, minimized]);
  useEffect(() => {
    connection.current?.emit('visibilityChange', { visible });
  }, [connection, visible]);
  useEffect(() => {
    connection.current?.emit('connectionChange', { online });
  }, [connection, online]);
  useEffect(() => {
    connection.current?.emit('sizeClassChange', { sizeClass });
  }, [connection, sizeClass]);
  useEffect(() => {
    // The locale carries the system region (formatLocale), so a change of either reaches the app.
    const onLocale = () => {
      connection.current?.emit('localeChange', {
        language: i18next.language,
        locale: formatLocale(),
        timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
      });
    };
    i18next.on('languageChanged', onLocale);
    const stopFormats = subscribeFormats(onLocale);
    const stopTheme = onThemeChange(() => {
      connection.current?.emit('themeChange', currentTheme());
    });
    const unregister = registerWindow(windowId, connection);
    const stopGuard = guardClose(windowId, () => mayClose(windowId, connection));
    const stopUnload = onBeforeUnload(async (reason) =>
      connection.current?.ask('beforeUnload', { reason }, BEFORE_UNLOAD_TIMEOUT),
    );
    return () => {
      i18next.off('languageChanged', onLocale);
      stopFormats();
      stopTheme();
      stopGuard();
      stopUnload();
      unregister();
      connection.current?.close();
      connection.current = null;
    };
  }, [connection, windowId]);
}
