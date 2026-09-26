import { IconArrowsDiagonalMinimize2, IconMinus, IconSquare, IconX } from '@tabler/icons-react';
import type { CSSProperties, ReactNode } from 'react';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { IconButton } from '../ui/Button';
import { cx } from '../ui/cx';
import { AppIcon } from './AppIcon';
import type { AppInfo } from './types';
import styles from './Window.module.css';

/** Position and size in percent of the workspace, used in the expanded size class. */
export interface WindowGeometry {
  x: number;
  y: number;
  width: number;
  height: number;
}

/** Placement in the medium size class: maximized, one half of a split, or behind the others. */
export type MediumPlacement = 'max' | 'left' | 'right' | 'hidden';

export interface WindowProps {
  app: Pick<AppInfo, 'icon' | 'tone'>;
  title: string;
  focused: boolean;
  maximized?: boolean;
  minimized?: boolean;
  geometry: WindowGeometry;
  medium?: MediumPlacement;
  /** In the compact size class only the active window is shown, full screen. */
  compactActive?: boolean;
  layer: number;
  children: ReactNode;
}

/** A window frame: title bar with icon, title and controls, and the app content in a size container. */
export function Window({
  app,
  title,
  focused,
  maximized = false,
  minimized = false,
  geometry,
  medium = 'max',
  compactActive = false,
  layer,
  children,
}: WindowProps) {
  const { t } = useTranslation();
  const titleId = useId();
  const style = {
    '--x': `${String(geometry.x)}%`,
    '--y': `${String(geometry.y)}%`,
    '--w': `${String(geometry.width)}%`,
    '--h': `${String(geometry.height)}%`,
    zIndex: `calc(var(--z-windows) + ${String(layer)})`,
  } as CSSProperties;

  return (
    <section
      className={cx(styles.window, focused && styles.focused)}
      style={style}
      aria-labelledby={titleId}
      data-maximized={maximized || undefined}
      data-minimized={minimized || undefined}
      data-medium={medium}
      data-compact-active={compactActive || undefined}
    >
      <header className={styles.titlebar}>
        <AppIcon app={app} size={20} className={styles.icon} />
        <h2 id={titleId} className={styles.title}>
          {title}
        </h2>
        <div className={styles.controls}>
          <IconButton label={t('window.minimize')} size="sm" className={styles.control}>
            <IconMinus size={16} stroke={1.75} />
          </IconButton>
          <IconButton
            label={maximized ? t('window.restore') : t('window.maximize')}
            size="sm"
            className={cx(styles.control, styles.maximize)}
          >
            {maximized ? (
              <IconArrowsDiagonalMinimize2 size={15} stroke={1.75} />
            ) : (
              <IconSquare size={13} stroke={2} />
            )}
          </IconButton>
          <IconButton
            label={t('window.close')}
            size="sm"
            className={cx(styles.control, styles.close)}
          >
            <IconX size={17} stroke={1.75} />
          </IconButton>
        </div>
      </header>
      <div className={styles.content}>{children}</div>
    </section>
  );
}
