import { useEffect, type RefObject } from 'react';

/**
 * Closes a non-modal overlay (start menu, app switcher, notification panel) on Escape, on a pointer press or a
 * right-click outside it and when focus moves out of it. Presses on the element that toggles it are ignored; it handles them itself.
 */
export function useDismiss(
  overlay: RefObject<HTMLElement | null>,
  trigger: RefObject<HTMLElement | null>,
  open: boolean,
  onDismiss: (reason: 'escape' | 'outside') => void,
): void {
  useEffect(() => {
    if (!open) {
      return;
    }
    // Menus opened from inside the overlay (the start menu's power menu) render as React Aria popovers in a portal
    // on the body; they carry data-trigger and count as inside.
    const inside = (target: EventTarget | null) =>
      target instanceof Element &&
      (overlay.current?.contains(target) === true ||
        trigger.current?.contains(target) === true ||
        target.closest('[data-trigger]') !== null);
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !event.defaultPrevented) {
        onDismiss('escape');
      }
    };
    // An open context menu is modal and takes presses and right-clicks itself: it closes, and a right-click is
    // handed on to the element under the pointer as a new event, which is judged here once the menu is gone.
    const outside = (event: Event) =>
      document.querySelector('[data-trigger="ContextMenu"]') === null && !inside(event.target);
    const onPress = (event: Event) => {
      if (outside(event)) {
        onDismiss('outside');
      }
    };
    const onFocusIn = (event: FocusEvent) => {
      if (!inside(event.target)) {
        onDismiss('outside');
      }
    };
    document.addEventListener('keydown', onKeyDown);
    document.addEventListener('pointerdown', onPress, true);
    document.addEventListener('contextmenu', onPress, true);
    document.addEventListener('focusin', onFocusIn);
    return () => {
      document.removeEventListener('keydown', onKeyDown);
      document.removeEventListener('pointerdown', onPress, true);
      document.removeEventListener('contextmenu', onPress, true);
      document.removeEventListener('focusin', onFocusIn);
    };
  }, [overlay, trigger, open, onDismiss]);
}
