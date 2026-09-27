import { BEFORE_CLOSE_TIMEOUT, BEFORE_LOGOUT_TIMEOUT } from '@pithyx/sdk';
import type { AppConnection } from '@pithyx/sdk/host';
import i18next from 'i18next';
import { useEffect, type RefObject } from 'react';
import { guardClose } from '../../wm/commands';
import { useSizeClass } from '../../wm/sizeClass';
import { useDesktop } from '../../wm/store';
import { onBeforeLogout } from '../lifecycle';
import { currentTheme, onThemeChange } from '../sandbox/theme';

/**
 * Sends an app window its lifecycle events (focus, minimize, size class, theme, language) and asks it before the
 * window closes and before sign-out. The same for sandboxed and core apps: only the connection's transport differs.
 */
export function useAppLifecycle(
  windowId: string,
  connection: RefObject<AppConnection | null>,
): void {
  const sizeClass = useSizeClass();
  const focused = useDesktop((state) => state.focused === windowId);
  const minimized = useDesktop((state) => state.windows[windowId]?.minimized === true);

  // Lifecycle events follow the window manager, the theme and the language (things outside React).
  useEffect(() => {
    connection.current?.emit(focused ? 'focus' : 'blur', {});
  }, [connection, focused]);
  useEffect(() => {
    connection.current?.emit(minimized ? 'minimize' : 'restore', {});
  }, [connection, minimized]);
  useEffect(() => {
    connection.current?.emit('sizeClassChange', { sizeClass });
  }, [connection, sizeClass]);
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
  }, [connection, windowId]);
}
