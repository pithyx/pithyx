import { useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@pithyx/api-client';
import { useEffect, useLayoutEffect } from 'react';
import { api } from '../api/client';
import { isAppAllowed } from '../apps/access';
import { findApp } from '../apps/registry';
import { describeError, isSessionExpired } from '../errors/describe';
import { clearToasts, notify } from '../shell/toasts';
import { fromLayout, toLayout, type Layout } from './layout';
import { applyAppearance, checkedAppearance } from '../theme/appearance';
import { clearPreferredSizes, setPreferredSize } from './preferences';
import { initialState } from './state';
import { dispatch, useDesktop } from './store';
import type { DesktopState, WindowOptions } from './types';
import { useWindowUi } from './ui';

/** Saves are debounced; a drag or a burst of focus changes becomes one request. */
const SAVE_DELAY = 1000;

let saveTimer: ReturnType<typeof setTimeout> | undefined;
// Saving starts only after the saved layout was restored, so an empty desktop never overwrites it.
let saving = false;
// Replacing the whole desktop (restore, "Get desktop from ...") is not closing its windows.
let replacing = false;
// The version of this device's layout as the daemon last reported it. Every save names it (If-Match), so a save
// from another tab of this browser is noticed instead of silently overwritten.
let layoutVersion = 0;
// Saves run one after another, so each one names the version the previous one produced.
let saveQueue: Promise<void> = Promise.resolve();
// A failing save is reported once until a save succeeds again, not after every window move.
let failureShown = false;

function appOptions(app: string): WindowOptions | undefined {
  // A window of an app the user may no longer open is skipped like one of an app that is gone.
  return isAppAllowed(app) ? findApp(app)?.window : undefined;
}

/** Only the tab the user works in saves: the tabs of one browser share its layout. */
function isActiveTab(): boolean {
  return document.visibilityState === 'visible' && document.hasFocus();
}

function versionOf(etag: string | null): number | undefined {
  const match = /^"(\d+)"$/.exec(etag ?? '');
  return match === null ? undefined : Number(match[1]);
}

async function put(layout: Layout, keepalive: boolean) {
  return api.PUT('/api/desktop/layout', {
    body: layout,
    headers: { 'If-Match': `"${String(layoutVersion)}"` },
    // A save when the page goes away must outlive it.
    keepalive,
  });
}

async function send(layout: Layout, keepalive: boolean): Promise<void> {
  try {
    let result = await put(layout, keepalive);
    // Another tab saved in between. This tab is the one in use, so its layout wins.
    const conflict = result.error?.code === 'desktop.layout.conflict' ? result.error : undefined;
    if (conflict !== undefined) {
      layoutVersion = Number(conflict.params.version);
      result = await put(layout, keepalive);
    }
    unwrap(result);
    layoutVersion = versionOf(result.response.headers.get('ETag')) ?? layoutVersion + 1;
    failureShown = false;
  } catch (error) {
    // An ended session leads to the sign-in screen; a lost save there is expected.
    if (!failureShown && !isSessionExpired(error)) {
      failureShown = true;
      console.error(`Pithyx layout save failed, request id ${describeError(error).requestId}`);
      notify({ code: 'desktop.saveFailed', params: {} });
    }
  }
}

function save() {
  saveTimer = undefined;
  const layout = toLayout(useDesktop.getState());
  saveQueue = saveQueue.then(() => send(layout, false));
}

/**
 * Sends a pending save right away as a keepalive request, when the page goes away or is hidden. It does not wait for
 * a save in flight: the page may be gone before that one answers.
 */
function flushOnLeave(): void {
  if (saveTimer !== undefined) {
    clearTimeout(saveTimer);
    saveTimer = undefined;
    void send(toLayout(useDesktop.getState()), true);
  }
}

function onVisibilityChange() {
  if (document.visibilityState === 'hidden') {
    flushOnLeave();
  }
}

/** How long signing out waits for the last save; after the sign-out it would be refused. */
const SIGN_OUT_SAVE_WAIT = 1000;

/** Sends a pending save and waits until the daemon answered it, at most one second. */
export async function saveLayoutBeforeSignOut(): Promise<void> {
  if (saveTimer !== undefined) {
    clearTimeout(saveTimer);
    save();
  }
  await Promise.race([
    saveQueue,
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
function applyLayout(layout: unknown): string[] {
  const { state, skipped } = fromLayout(layout, appOptions, useDesktop.getState().workspace);
  replaceDesktop(state);
  return skipped;
}

/** "Get desktop from ...": shows a layout the daemon just saved for this device. */
export function applySavedLayout(saved: { layout: unknown; version: number }): void {
  layoutVersion = saved.version;
  applyLayout(saved.layout);
}

/** Forgets everything of the signed-in user that lives outside React, so the next user of this tab starts clean. */
function forgetDesktop() {
  replaceDesktop(initialState(useDesktop.getState().workspace));
  layoutVersion = 0;
  failureShown = false;
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
 * taskbar position, debounced, from the tab the user works in. Closing a window remembers its size for the app's next
 * window.
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
    layoutVersion = desktop.data.layoutVersion;
    applyAppearance(checkedAppearance(desktop.data.appearance));
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
      // A background tab follows along (for example a smaller screen clamps its windows) but does not save.
      if (!saving || !persistedChange(next, previous) || !isActiveTab()) {
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
    addEventListener('pagehide', flushOnLeave);
    document.addEventListener('visibilitychange', onVisibilityChange);
    return () => {
      unsubscribe();
      removeEventListener('pagehide', flushOnLeave);
      document.removeEventListener('visibilitychange', onVisibilityChange);
      if (saveTimer !== undefined) {
        clearTimeout(saveTimer);
        save();
      }
      saving = false;
      // Signed out or session ended: nothing of this user may show up for the next one in this tab.
      forgetDesktop();
      queryClient.removeQueries({ queryKey: ['desktop'] });
    };
  }, [queryClient]);
}
