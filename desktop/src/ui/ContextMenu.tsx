import { useEffect, useEffectEvent, useRef, type ReactNode, type RefObject } from 'react';
import { Popover } from 'react-aria-components';
import { flushSync } from 'react-dom';
import menuStyles from './Menu.module.css';

/**
 * The popover of a context menu, opened at a point of the viewport. A second right-click while it is open moves the
 * menu there, or to whichever menu the element at that point opens.
 */
export function ContextMenuPopover({
  x,
  y,
  onClose,
  children,
}: {
  x: number;
  y: number;
  onClose: () => void;
  children: ReactNode;
}) {
  const anchor = useRef<HTMLDivElement>(null);
  const popover = useRef<HTMLElement>(null);
  useContextMenuHandOff(popover, onClose);
  return (
    <>
      {/* An empty anchor at the point: fixed and without content, it has no size. */}
      <div ref={anchor} style={{ position: 'fixed', left: x, top: y }} aria-hidden />
      <Popover
        ref={popover}
        triggerRef={anchor}
        isOpen
        onOpenChange={(open) => {
          if (!open) {
            onClose();
          }
        }}
        placement="bottom start"
        offset={2}
        className={menuStyles.popover}
      >
        {children}
      </Popover>
    </>
  );
}

/**
 * A modal popover covers the page with an underlay that takes every right-click and makes the rest of the page inert,
 * so nobody under it prevents the browser's menu. While the menu is open, a right-click inside it only prevents that
 * menu. A right-click outside also closes it and goes to the element at that point, which may open its own menu
 * there; one that does not (the text of a sticky note) leaves the menu closed. A touch long press outside is
 * dismissed like a tap, and the long press that opened the menu must not close it again.
 */
function useContextMenuHandOff(popover: RefObject<HTMLElement | null>, onClose: () => void) {
  const close = useEffectEvent(onClose);
  useEffect(() => {
    function onContextMenu(event: MouseEvent) {
      // The popover's parent also holds its submenus.
      const group = popover.current?.parentElement;
      // The hand-off below is not trusted and must not be handed off again.
      if (!event.isTrusted || group == null) {
        return;
      }
      event.preventDefault();
      if (!(event.target instanceof Node) || group.contains(event.target)) {
        return;
      }
      if (event instanceof PointerEvent && event.pointerType === 'touch') {
        return;
      }
      // Closing removes the underlay and the inert marks at once, so the point hits what the user sees.
      flushSync(close);
      document
        .elementFromPoint(event.clientX, event.clientY)
        ?.dispatchEvent(new MouseEvent('contextmenu', event));
    }
    document.addEventListener('contextmenu', onContextMenu, true);
    return () => {
      document.removeEventListener('contextmenu', onContextMenu, true);
    };
  }, [popover]);
}
