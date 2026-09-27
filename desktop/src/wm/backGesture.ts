import { useEffect } from 'react';
import { dispatch, useDesktop } from './store';

const APP_STATE = 'vyxis-app';

function inAppEntry(): boolean {
  return (history.state as { vyxis?: string } | null)?.vyxis === APP_STATE;
}

// Whether an app is full screen right now, and whether the desktop is waiting for its own history.back().
let wanted = false;
let consuming = false;

/** Brings history in line with `wanted`: one entry while an app is active, none otherwise. */
function sync() {
  if (consuming) {
    // history.back() is asynchronous; the pending popstate syncs again.
    return;
  }
  if (wanted && !inAppEntry()) {
    history.pushState({ vyxis: APP_STATE }, '');
  } else if (!wanted && inAppEntry()) {
    consuming = true;
    history.back();
  }
}

/** Every app that is shown goes to the background, like the home button of a phone. */
function goHome() {
  const { windows } = useDesktop.getState();
  for (const entry of Object.values(windows)) {
    if (!entry.minimized) {
      dispatch({ type: 'minimize', id: entry.id });
    }
  }
}

/**
 * Phones: while an app is full screen, one history entry belongs to it, so the Android back gesture and the browser
 * back button return to the home screen instead of leaving the desktop. Leaving the app in another way removes the
 * entry again, so history never grows with app switches.
 */
export function useBackGesture(active: boolean) {
  useEffect(() => {
    const onPopState = () => {
      if (consuming) {
        consuming = false;
        sync();
        return;
      }
      if (wanted) {
        goHome();
      }
    };
    addEventListener('popstate', onPopState);
    return () => {
      removeEventListener('popstate', onPopState);
    };
  }, []);

  useEffect(() => {
    wanted = active;
    sync();
  }, [active]);
}
