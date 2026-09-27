import type { Placement, SnapZone } from './types';

export type KeyCommand =
  | { type: 'snap'; side: 'left' | 'right' }
  | { type: 'maximize' }
  | { type: 'down' }
  | { type: 'cycle'; direction: 1 | -1 }
  | { type: 'menu' };

interface KeyInput {
  key: string;
  code: string;
  altKey: boolean;
  shiftKey: boolean;
  ctrlKey: boolean;
  metaKey: boolean;
}

/**
 * Global window shortcuts. They all use Alt+Shift, which neither browsers nor desktop operating systems claim;
 * Super, Alt+Tab, Alt+F4 and Ctrl+W never reach a web page or close the browser.
 */
export function commandFor(event: KeyInput): KeyCommand | null {
  if (!event.altKey || !event.shiftKey || event.ctrlKey || event.metaKey) {
    return null;
  }
  // Option+Space types a no-break space on macOS, so the space bar is matched by its code.
  if (event.code === 'Space') {
    return { type: 'menu' };
  }
  switch (event.key) {
    case 'ArrowLeft':
      return { type: 'snap', side: 'left' };
    case 'ArrowRight':
      return { type: 'snap', side: 'right' };
    case 'ArrowUp':
      return { type: 'maximize' };
    case 'ArrowDown':
      return { type: 'down' };
    case 'PageDown':
      return { type: 'cycle', direction: 1 };
    case 'PageUp':
      return { type: 'cycle', direction: -1 };
    default:
      return null;
  }
}

interface FocusTarget {
  tagName: string;
  isContentEditable: boolean;
  type?: string | undefined;
}

const TEXT_INPUT_TYPES = new Set([
  'text',
  'search',
  'email',
  'url',
  'tel',
  'password',
  'number',
  '',
]);

/**
 * Whether the element edits text. Window shortcuts stay out of text fields: on macOS Option+Shift with an arrow key
 * selects by word, which is far more common there than moving a window.
 */
export function isTextEditing(target: FocusTarget | null): boolean {
  if (target === null) {
    return false;
  }
  if (target.isContentEditable || target.tagName === 'TEXTAREA') {
    return true;
  }
  return target.tagName === 'INPUT' && TEXT_INPUT_TYPES.has(target.type ?? '');
}

const ULTRAWIDE_STEPS: Record<'left' | 'right', SnapZone[]> = {
  left: ['left', 'left-third', 'left-two-thirds'],
  right: ['right', 'right-third', 'right-two-thirds'],
};

/** Snapping to a side again cycles through half, third and two thirds on ultrawide workspaces. */
export function nextSnapZone(
  placement: Placement,
  side: 'left' | 'right',
  ultrawide: boolean,
): SnapZone {
  if (!ultrawide) {
    return side;
  }
  const steps = ULTRAWIDE_STEPS[side];
  const current = placement.kind === 'snapped' ? steps.indexOf(placement.zone) : -1;
  return steps[(current + 1) % steps.length] ?? side;
}
