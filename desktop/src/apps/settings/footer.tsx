import { createContext, useContext, type ReactNode } from 'react';
import { createPortal } from 'react-dom';

/** The element at the bottom of the Settings frame that a page's action bar is drawn into. */
export const FooterSlot = createContext<HTMLElement | null>(null);

/**
 * A page's part of the frame's footer (D394): the action bar of a page whose changes need applying, fixed below the
 * scrolling page.
 */
export function PageFooter({ children }: { children: ReactNode }) {
  const slot = useContext(FooterSlot);
  return slot === null ? null : createPortal(children, slot);
}
