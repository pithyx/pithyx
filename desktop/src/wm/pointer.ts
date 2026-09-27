import { flushSync } from 'react-dom';
import type { PointerEvent as ReactPointerEvent } from 'react';
import { dragRect, resizeRect, restoreAnchor, snapZoneAt, zoneRect, type Edge } from './geometry';
import { showPreview } from './preview';
import { visibleRect } from './state';
import { dispatch, useDesktop } from './store';
import type { Point, Rect, SnapZone } from './types';

/** Pointer movement below this is a click, not a drag. */
const DRAG_THRESHOLD = 4;

/** While set, window contents ignore the pointer (iframes would swallow the events) and text is not selected. */
function setInteracting(active: boolean) {
  if (active) {
    document.documentElement.dataset.wmInteracting = '';
  } else {
    delete document.documentElement.dataset.wmInteracting;
  }
}

function currentRect(id: string): Rect | null {
  const state = useDesktop.getState();
  const windowState = state.windows[id];
  if (windowState === undefined) {
    return null;
  }
  switch (windowState.placement.kind) {
    case 'free':
      return visibleRect(windowState, state.workspace);
    case 'maximized':
      return zoneRect('maximize', state.workspace);
    case 'snapped':
      return zoneRect(windowState.placement.zone, state.workspace);
  }
}

/**
 * Runs one pointer gesture with pointer capture: `move` gets the latest pointer position once per animation frame,
 * `end` gets it on release, `cancel` on pointercancel. Nothing touches React until the gesture ends.
 */
function track(
  event: ReactPointerEvent<HTMLElement>,
  handlers: {
    move: (point: Point) => void;
    end: (point: Point) => void;
    cancel: () => void;
  },
) {
  const target = event.currentTarget;
  const pointerId = event.pointerId;
  target.setPointerCapture(pointerId);
  let latest: Point = { x: event.clientX, y: event.clientY };
  let frame = 0;
  // The end of the gesture is heard on the window: if the element goes away mid-gesture (its window was closed or
  // maximized from the keyboard), capture is lost, but release and cancel still arrive there.
  const listeners = new AbortController();
  const onMove = (moveEvent: PointerEvent) => {
    latest = { x: moveEvent.clientX, y: moveEvent.clientY };
    if (frame === 0) {
      frame = requestAnimationFrame(() => {
        frame = 0;
        handlers.move(latest);
      });
    }
  };
  const finish = (callback: () => void) => {
    cancelAnimationFrame(frame);
    listeners.abort();
    if (target.hasPointerCapture(pointerId)) {
      target.releasePointerCapture(pointerId);
    }
    callback();
    // Keep transitions off until the committed geometry is on screen.
    requestAnimationFrame(() => {
      setInteracting(false);
    });
  };
  const options = { capture: true, signal: listeners.signal };
  target.addEventListener('pointermove', onMove, { signal: listeners.signal });
  addEventListener(
    'pointerup',
    (upEvent) => {
      if (upEvent.pointerId === pointerId) {
        finish(() => {
          handlers.end({ x: upEvent.clientX, y: upEvent.clientY });
        });
      }
    },
    options,
  );
  addEventListener(
    'pointercancel',
    (cancelEvent) => {
      if (cancelEvent.pointerId === pointerId) {
        finish(handlers.cancel);
      }
    },
    options,
  );
}

function isPrimary(event: ReactPointerEvent): boolean {
  return event.button === 0 && event.isPrimary;
}

/** Starts dragging a window by its title bar. Maximized and snapped windows get their free size back once moved. */
export function startDrag(event: ReactPointerEvent<HTMLElement>, id: string, frame: HTMLElement) {
  if (!isPrimary(event) || (event.target as Element).closest('button') !== null) {
    return;
  }
  const workspaceElement = frame.offsetParent;
  const initial = currentRect(id);
  const state = useDesktop.getState();
  const windowState = state.windows[id];
  if (!(workspaceElement instanceof HTMLElement) || initial === null || windowState === undefined) {
    return;
  }
  event.preventDefault();
  const origin = workspaceElement.getBoundingClientRect();
  const workspace = state.workspace;
  const down: Point = { x: event.clientX, y: event.clientY };
  const local = (point: Point): Point => ({ x: point.x - origin.left, y: point.y - origin.top });
  let start: Rect | null = null;
  let rect: Rect = initial;
  let zone: SnapZone | 'maximize' | null = null;

  track(event, {
    move(point) {
      if (start === null) {
        if (Math.hypot(point.x - down.x, point.y - down.y) < DRAG_THRESHOLD) {
          return;
        }
        setInteracting(true);
        if (windowState.placement.kind === 'free') {
          start = initial;
        } else {
          // Tear the window off its maximized or snapped place at its free size, under the pointer.
          const torn = restoreAnchor(visibleRect(windowState, workspace), initial, local(down));
          start = torn;
          flushSync(() => {
            dispatch({ type: 'move', id, rect: torn });
          });
        }
      }
      rect = dragRect(
        start,
        { x: point.x - down.x, y: point.y - down.y },
        windowState.minSize,
        workspace,
      );
      frame.style.transform = `translate3d(${String(rect.x - start.x)}px, ${String(rect.y - start.y)}px, 0)`;
      zone = snapZoneAt(local(point), workspace);
      showPreview(zone === null ? null : zoneRect(zone, workspace));
    },
    end() {
      showPreview(null);
      if (start === null) {
        return;
      }
      const finalZone = zone;
      flushSync(() => {
        if (finalZone === 'maximize') {
          dispatch({ type: 'maximize', id });
        } else if (finalZone !== null) {
          dispatch({ type: 'snap', id, zone: finalZone });
        } else {
          dispatch({ type: 'move', id, rect });
        }
      });
      frame.style.transform = '';
    },
    cancel() {
      showPreview(null);
      frame.style.transform = '';
    },
  });
}

/** Starts resizing a free window from one edge or corner, with live layout of the content. */
export function startResize(
  event: ReactPointerEvent<HTMLElement>,
  id: string,
  frame: HTMLElement,
  edge: Edge,
) {
  const state = useDesktop.getState();
  const windowState = state.windows[id];
  if (!isPrimary(event) || windowState === undefined) {
    return;
  }
  event.preventDefault();
  event.stopPropagation();
  setInteracting(true);
  const start = visibleRect(windowState, state.workspace);
  const down: Point = { x: event.clientX, y: event.clientY };
  let rect = start;
  const write = (next: Rect) => {
    frame.style.setProperty('--x', `${String(next.x)}px`);
    frame.style.setProperty('--y', `${String(next.y)}px`);
    frame.style.setProperty('--w', `${String(next.width)}px`);
    frame.style.setProperty('--h', `${String(next.height)}px`);
  };

  track(event, {
    move(point) {
      rect = resizeRect(
        start,
        edge,
        { x: point.x - down.x, y: point.y - down.y },
        windowState.minSize,
        state.workspace,
      );
      write(rect);
    },
    end() {
      // Maximized or snapped from the keyboard mid-resize: that placement wins over the resize.
      if (useDesktop.getState().windows[id]?.placement.kind !== 'free') {
        write(start);
        return;
      }
      flushSync(() => {
        dispatch({ type: 'move', id, rect });
      });
    },
    cancel() {
      write(start);
    },
  });
}
