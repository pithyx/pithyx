import type { PointerEvent as ReactPointerEvent } from 'react';

/** Pointer movement below this is a click or tap, not a drag. */
export const DRAG_THRESHOLD = 4;

interface Delta {
  x: number;
  y: number;
}

/**
 * Follows one pointer gesture from a press. `move` runs at most once per animation frame, and only once the pointer
 * went further than the drag threshold; `end` gets the total distance and whether it was a drag. Nothing touches
 * React during the gesture.
 */
export function trackPointer(
  event: ReactPointerEvent,
  handlers: {
    start?: () => void;
    move: (delta: Delta) => void;
    end: (delta: Delta, moved: boolean) => void;
  },
): void {
  const pointerId = event.pointerId;
  const origin = { x: event.clientX, y: event.clientY };
  let delta: Delta = { x: 0, y: 0 };
  let moved = false;
  let frame = 0;
  const listeners = new AbortController();
  const finish = (cancelled: boolean) => {
    listeners.abort();
    cancelAnimationFrame(frame);
    delete document.documentElement.dataset.wmInteracting;
    handlers.end(cancelled ? { x: 0, y: 0 } : delta, moved && !cancelled);
  };
  window.addEventListener(
    'pointermove',
    (moveEvent) => {
      if (moveEvent.pointerId !== pointerId) {
        return;
      }
      delta = { x: moveEvent.clientX - origin.x, y: moveEvent.clientY - origin.y };
      if (!moved && Math.hypot(delta.x, delta.y) < DRAG_THRESHOLD) {
        return;
      }
      if (!moved) {
        moved = true;
        // The same flag windows use: window contents ignore the pointer and no text gets selected.
        document.documentElement.dataset.wmInteracting = '';
        handlers.start?.();
      }
      if (frame === 0) {
        frame = requestAnimationFrame(() => {
          frame = 0;
          handlers.move(delta);
        });
      }
    },
    { signal: listeners.signal },
  );
  window.addEventListener(
    'pointerup',
    (upEvent) => {
      if (upEvent.pointerId === pointerId) {
        finish(false);
      }
    },
    { signal: listeners.signal },
  );
  window.addEventListener(
    'pointercancel',
    (cancelEvent) => {
      if (cancelEvent.pointerId === pointerId) {
        finish(true);
      }
    },
    { signal: listeners.signal },
  );
}
