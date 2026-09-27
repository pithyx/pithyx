import { useEffect } from 'react';
import { dispatch, useDesktop } from './store';

const APP_STATE = 'vyxis-app';

function inAppEntry(): boolean {
  return (history.state as { vyxis?: string } | null)?.vyxis === APP_STATE;
}

// Set while the desktop consumes its own history entry, so that pop is not taken as the user's back gesture.
let consuming = false;

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
        return;
      }
      const { focused } = useDesktop.getState();
      if (focused !== null) {
        dispatch({ type: 'minimize', id: focused });
      }
    };
    addEventListener('popstate', onPopState);
    return () => {
      removeEventListener('popstate', onPopState);
    };
  }, []);

  useEffect(() => {
    if (active && !inAppEntry()) {
      history.pushState({ vyxis: APP_STATE }, '');
    } else if (!active && inAppEntry()) {
      consuming = true;
      history.back();
    }
  }, [active]);
}
