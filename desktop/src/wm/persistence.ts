import { useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@vyxis/api-client';
import { useEffect, useLayoutEffect } from 'react';
import { api } from '../api/client';
import { findApp } from '../apps/registry';
import { realtime } from '../realtime/connection';
import { isSessionExpired } from '../errors/describe';
import { clearToasts, notify } from '../shell/toasts';
import { fromLayout, toLayout } from './layout';
import { clearPreferredSizes, setPreferredSize } from './preferences';
import { initialState } from './state';
import { dispatch, useDesktop } from './store';
import type { DesktopState, WindowOptions } from './types';
import { useWindowUi } from './ui';

/** Saves are debounced; a drag or a burst of focus changes becomes one message. */
const SAVE_DELAY = 1000;

let saveTimer: ReturnType<typeof setTimeout> | undefined;
// Saving starts only after the saved layout was restored, so an empty desktop never overwrites it.
let saving = false;
// Replacing the whole desktop (restore, "Get desktop from ...") is not closing its windows.
let replacing = false;

function appOptions(app: string): WindowOptions | undefined {
  return findApp(app)?.window;
}

function save() {
  saveTimer = undefined;
  realtime.saveLayout(toLayout(useDesktop.getState()));
}

/** Sends a pending save right away, for example when the page goes away. */
export function flushLayoutSave(): void {
  if (saveTimer !== undefined) {
    clearTimeout(saveTimer);
    save();
  }
}

/** How long signing out waits for the daemon to confirm the last save; the sign-out closes the WebSocket. */
const SIGN_OUT_SAVE_WAIT = 1000;

/** Sends a pending save and waits until the daemon answered it, at most one second. */
export async function saveLayoutBeforeSignOut(): Promise<void> {
  flushLayoutSave();
  await Promise.race([
    realtime.layoutSaved(),
    new Promise((resolve) => setTimeout(resolve, SIGN_OUT_SAVE_WAIT)),
  ]);
}

/** Retries of the first layout fetch (exponential up to 30 s between attempts), about five minutes in total. */
const RESTORE_RETRIES = 12;

function persistedChange(next: DesktopState, previous: DesktopState): boolean {
  return (
    next.windows !== previous.windows ||
    next.order !== previous.order ||
    next.focused !== previous.focused ||
    next.taskbar !== previous.taskbar
  );
}

function replaceDesktop(state: DesktopState) {
  replacing = true;
  dispatch({ type: 'replace', state });
  replacing = false;
  // An open window menu or keyboard mode belongs to windows that are gone now.
  useWindowUi.setState({ menu: null, mode: null });
}

/** Replaces the desktop with a saved layout. Returns the app ids of windows that could not be restored. */
export function applyLayout(layout: unknown): string[] {
  const { state, skipped } = fromLayout(layout, appOptions, useDesktop.getState().workspace);
  replaceDesktop(state);
  return skipped;
}

/** Forgets everything of the signed-in user that lives outside React, so the next user of this tab starts clean. */
function forgetDesktop() {
  replaceDesktop(initialState(useDesktop.getState().workspace));
  clearPreferredSizes();
  clearToasts();
}

function rememberSize(app: string, width: number, height: number) {
  // Phones and tablets clamp windows to their small workspace; only free windows on a large screen set the size.
  if (!matchMedia('(min-width: 1024px)').matches) {
    return;
  }
  setPreferredSize(app, { width, height });
  // A lost preference only means the next window opens at the default size.
  void api
    .PUT('/api/desktop/preferences/{appId}', {
      params: { path: { appId: app } },
      body: { width, height },
    })
    .catch(() => undefined);
}

/**
 * Restores this device's layout when the desktop opens and saves every change of windows, stacking order, focus and
 * taskbar position over the WebSocket, debounced. Closing a window remembers its size for the app's next window.
 */
export function useLayoutPersistence() {
  const queryClient = useQueryClient();
  // The store is empty here: the previous desktop forgot everything on unmount. Resetting it again at mount could
  // run after a pending save of a replaced desktop was flushed and save an empty layout.
  useLayoutEffect(() => {
    saving = false;
  }, []);

  const desktop = useQuery({
    queryKey: ['desktop'],
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/desktop', { signal })),
    staleTime: Infinity,
    gcTime: 0,
    // Saving waits for this, so a daemon restart during sign-in must not disable it for the whole session.
    retry: (failures, error) => !isSessionExpired(error) && failures < RESTORE_RETRIES,
  });

  // Without the saved layout nothing is saved either (it would overwrite it); tell the user once.
  useEffect(() => {
    if (desktop.isError && !isSessionExpired(desktop.error)) {
      notify({ code: 'desktop.restoreFailed', params: {} });
    }
  }, [desktop.isError, desktop.error]);

  // Moves the fetched layout into the window store once; the store lives outside React.
  useEffect(() => {
    if (desktop.data === undefined || saving) {
      return;
    }
    for (const preference of desktop.data.preferences) {
      setPreferredSize(preference.appId, { width: preference.width, height: preference.height });
    }
    // Windows the user opened while the layout was loading win over the saved ones.
    if (desktop.data.layout !== null && Object.keys(useDesktop.getState().windows).length === 0) {
      const skipped = applyLayout(desktop.data.layout);
      if (skipped.length > 0) {
        notify({ code: 'desktop.restoreSkipped', params: { count: skipped.length } });
      }
    }
    saving = true;
  }, [desktop.data]);

  useEffect(() => {
    const unsubscribe = useDesktop.subscribe((next, previous) => {
      if (!saving || !persistedChange(next, previous)) {
        return;
      }
      clearTimeout(saveTimer);
      saveTimer = setTimeout(save, SAVE_DELAY);
      if (!replacing) {
        for (const [id, closed] of Object.entries(previous.windows)) {
          if (next.windows[id] === undefined) {
            rememberSize(closed.app, closed.rect.width, closed.rect.height);
          }
        }
      }
    });
    addEventListener('pagehide', flushLayoutSave);
    return () => {
      unsubscribe();
      removeEventListener('pagehide', flushLayoutSave);
      flushLayoutSave();
      saving = false;
      // Signed out or session ended: nothing of this user may show up for the next one in this tab.
      forgetDesktop();
      queryClient.removeQueries({ queryKey: ['desktop'] });
    };
  }, [queryClient]);
}
