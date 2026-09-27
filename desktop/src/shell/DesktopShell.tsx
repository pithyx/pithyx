import { useState, type ReactNode, type Ref } from 'react';
import { useTranslation } from 'react-i18next';
import styles from './DesktopShell.module.css';
import type { TaskbarPosition } from './types';
import { Wallpaper } from './Wallpaper';

/**
 * The frame of the desktop: wallpaper, the workspace that holds icons, notes and windows, the taskbar and
 * overlays such as the start menu. Size classes switch by viewport width (compact, medium, expanded).
 */
export function DesktopShell({
  taskbar,
  taskbarPosition = 'bottom',
  children,
  overlays,
  entering = false,
  workspaceRef,
}: {
  taskbar: ReactNode;
  taskbarPosition?: TaskbarPosition;
  children?: ReactNode;
  overlays?: ReactNode;
  /** Plays the entrance after sign-in. */
  entering?: boolean;
  /** The element windows are positioned in; the window manager measures it. */
  workspaceRef?: Ref<HTMLElement>;
}) {
  const { t } = useTranslation();
  // The entrance plays once. Without this, moving the taskbar later would change its animation and play it again.
  const [entered, setEntered] = useState(false);
  return (
    <div
      className={styles.shell}
      data-taskbar={taskbarPosition}
      data-entering={(entering && !entered) || undefined}
      onAnimationEnd={(event) => {
        if (
          entering &&
          event.target instanceof HTMLElement &&
          event.target.parentElement === event.currentTarget &&
          event.target.localName === 'nav'
        ) {
          setEntered(true);
        }
      }}
    >
      <Wallpaper />
      <main ref={workspaceRef} className={styles.workspace} aria-label={t('items.desktop')}>
        {children}
      </main>
      {taskbar}
      {overlays}
    </div>
  );
}

/** Desktop items flow in columns from the top left, like on a classic desktop. */
export function DesktopItems({ children }: { children: ReactNode }) {
  const { t } = useTranslation();
  return (
    <ul className={styles.items} aria-label={t('items.label')}>
      {children}
    </ul>
  );
}
