import i18next from 'i18next';
import { useEffect } from 'react';
import { announce } from './announce';
import { dragRect, isUltrawide, resizeRect } from './geometry';
import { commandFor, isTextEditing, nextSnapZone } from './keys';
import { visibleRect } from './state';
import { dispatch, useDesktop } from './store';
import { useWindowUi } from './ui';

export type WindowCommand =
  'restore' | 'move' | 'size' | 'minimize' | 'maximize' | 'snap-left' | 'snap-right' | 'close';

type Announcement =
  | 'snapped'
  | 'maximized'
  | 'restored'
  | 'minimized'
  | 'focused'
  | 'move'
  | 'size'
  | 'done'
  | 'cancelled';

function say(message: Announcement, id: string) {
  const target = useDesktop.getState().windows[id];
  if (target === undefined) {
    return;
  }
  const title = target.title ?? i18next.t(`apps.${target.app}` as 'apps.disks');
  announce(i18next.t(`window.announce.${message}`, { title }));
}

/** A window action from the window menu or the keyboard, announced for screen reader users. */
export function runWindowCommand(id: string, command: WindowCommand) {
  const state = useDesktop.getState();
  const target = state.windows[id];
  if (target === undefined) {
    return;
  }
  switch (command) {
    case 'restore':
      dispatch({ type: 'restore', id });
      say('restored', id);
      break;
    case 'maximize':
      dispatch({ type: 'maximize', id });
      say('maximized', id);
      break;
    case 'minimize':
      dispatch({ type: 'minimize', id });
      say('minimized', id);
      break;
    case 'snap-left':
    case 'snap-right': {
      const side = command === 'snap-left' ? 'left' : 'right';
      dispatch({
        type: 'snap',
        id,
        zone: nextSnapZone(target.placement, side, isUltrawide(state.workspace)),
      });
      say('snapped', id);
      break;
    }
    case 'move':
    case 'size':
      dispatch({ type: 'restore', id });
      useWindowUi.setState({
        mode: { id, kind: command, start: visibleRect(target, state.workspace) },
      });
      say(command, id);
      break;
    case 'close':
      dispatch({ type: 'close', id });
      break;
  }
}

/** Arrow keys move or resize the window in keyboard mode; Enter confirms, Escape restores where it was. */
function handleModeKey(event: KeyboardEvent): boolean {
  const { mode } = useWindowUi.getState();
  const state = useDesktop.getState();
  if (mode === null) {
    return false;
  }
  const target = state.windows[mode.id];
  if (target === undefined) {
    // The window closed while in the mode; let the key through and leave the mode.
    useWindowUi.setState({ mode: null });
    return false;
  }
  const step = event.shiftKey ? 1 : 16;
  const deltas: Record<string, { x: number; y: number }> = {
    ArrowLeft: { x: -step, y: 0 },
    ArrowRight: { x: step, y: 0 },
    ArrowUp: { x: 0, y: -step },
    ArrowDown: { x: 0, y: step },
  };
  const delta = deltas[event.key];
  if (delta !== undefined) {
    const shown = visibleRect(target, state.workspace);
    const rect =
      mode.kind === 'move'
        ? dragRect(shown, delta, target.minSize, state.workspace)
        : resizeRect(shown, 'se', delta, target.minSize, state.workspace);
    dispatch({ type: 'move', id: mode.id, rect });
    return true;
  }
  if (event.key === 'Enter' || event.key === 'Escape') {
    if (event.key === 'Escape') {
      dispatch({ type: 'move', id: mode.id, rect: mode.start });
    }
    useWindowUi.setState({ mode: null });
    say(event.key === 'Enter' ? 'done' : 'cancelled', mode.id);
    return true;
  }
  return false;
}

function toTarget(element: HTMLElement) {
  return {
    tagName: element.tagName,
    isContentEditable: element.isContentEditable,
    type: element instanceof HTMLInputElement ? element.type : undefined,
  };
}

/** The global Alt+Shift shortcuts for the focused window, and the keys of the keyboard move and size mode. */
export function useWindowKeys() {
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.defaultPrevented) {
        return;
      }
      if (useWindowUi.getState().mode !== null) {
        if (handleModeKey(event)) {
          event.preventDefault();
          event.stopPropagation();
          return;
        }
        // Other keys pass through to the page while the mode lasts; a mode whose window is gone has ended.
        if (useWindowUi.getState().mode !== null) {
          return;
        }
      }
      const command = commandFor(event);
      const { focused } = useDesktop.getState();
      if (
        command === null ||
        isTextEditing(event.target instanceof HTMLElement ? toTarget(event.target) : null)
      ) {
        return;
      }
      event.preventDefault();
      if (command.type === 'cycle') {
        dispatch({ type: 'cycle', direction: command.direction });
        const next = useDesktop.getState().focused;
        if (next !== null) {
          say('focused', next);
        }
        return;
      }
      if (focused === null) {
        return;
      }
      const placement = useDesktop.getState().windows[focused]?.placement.kind;
      switch (command.type) {
        case 'snap':
          runWindowCommand(focused, command.side === 'left' ? 'snap-left' : 'snap-right');
          break;
        case 'maximize':
          runWindowCommand(focused, 'maximize');
          break;
        case 'down':
          runWindowCommand(focused, placement === 'free' ? 'minimize' : 'restore');
          break;
        case 'menu':
          useWindowUi.setState({ menu: focused });
          break;
      }
    };
    // A pointer press ends the keyboard mode where the window is.
    const onPointerDown = () => {
      if (useWindowUi.getState().mode !== null) {
        useWindowUi.setState({ mode: null });
      }
    };
    document.addEventListener('keydown', onKeyDown, true);
    document.addEventListener('pointerdown', onPointerDown, true);
    return () => {
      document.removeEventListener('keydown', onKeyDown, true);
      document.removeEventListener('pointerdown', onPointerDown, true);
    };
  }, []);
}
