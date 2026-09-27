import { useShallow } from 'zustand/react/shallow';
import { ManagedWindow } from './ManagedWindow';
import { useDesktop } from './store';

/**
 * All windows in opening order. The stacking order comes from z-index only: reordering the DOM on every focus change
 * would move iframes, and a moved iframe reloads.
 */
export function WindowLayer() {
  const ids = useDesktop(useShallow((state) => Object.keys(state.windows)));
  return ids.map((id) => <ManagedWindow key={id} id={id} />);
}
