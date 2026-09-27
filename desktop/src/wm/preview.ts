import type { Rect } from './types';

// The snap preview is one element, updated directly while dragging so a drag never renders React.
let element: HTMLElement | null = null;

export function registerPreview(target: HTMLElement | null): void {
  element = target;
}

/** Shows the area a window will snap to, or hides it with null. */
export function showPreview(rect: Rect | null): void {
  if (element === null) {
    return;
  }
  if (rect === null) {
    element.dataset.visible = 'false';
    return;
  }
  element.style.setProperty('--x', `${String(rect.x)}px`);
  element.style.setProperty('--y', `${String(rect.y)}px`);
  element.style.setProperty('--w', `${String(rect.width)}px`);
  element.style.setProperty('--h', `${String(rect.height)}px`);
  element.dataset.visible = 'true';
}
