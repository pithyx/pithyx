import { useShallow } from 'zustand/react/shallow';
import { registerAnnouncer } from './announce';
import { useWindowKeys } from './commands';
import { ManagedWindow } from './ManagedWindow';
import { registerPreview } from './preview';
import { useDesktop } from './store';
import styles from './WindowLayer.module.css';

/**
 * All windows in opening order, the snap preview and the live region for keyboard actions. The stacking order comes
 * from z-index only: reordering the DOM on every focus change would move iframes, and a moved iframe reloads.
 */
export function WindowLayer() {
  const ids = useDesktop(useShallow((state) => Object.keys(state.windows)));
  useWindowKeys();
  return (
    <>
      {ids.map((id) => (
        <ManagedWindow key={id} id={id} />
      ))}
      <div ref={registerPreview} className={styles.preview} data-visible="false" aria-hidden />
      <p ref={registerAnnouncer} className="visually-hidden" aria-live="polite" />
    </>
  );
}
