import { useEffect, type RefObject } from 'react';

/**
 * Closes a non-modal overlay (start menu, app switcher, notification panel) on Escape, on a pointer press outside
 * it and when focus moves out of it. Presses on the element that toggles it are ignored; it handles them itself.
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
    const onPointerDown = (event: PointerEvent) => {
      if (!inside(event.target)) {
        onDismiss('outside');
      }
    };
    const onFocusIn = (event: FocusEvent) => {
      if (!inside(event.target)) {
        onDismiss('outside');
      }
    };
    document.addEventListener('keydown', onKeyDown);
    document.addEventListener('pointerdown', onPointerDown, true);
    document.addEventListener('focusin', onFocusIn);
    return () => {
      document.removeEventListener('keydown', onKeyDown);
      document.removeEventListener('pointerdown', onPointerDown, true);
      document.removeEventListener('focusin', onFocusIn);
    };
  }, [overlay, trigger, open, onDismiss]);
}
