import { IconArrowsDiagonalMinimize2, IconMinus, IconSquare, IconX } from '../ui/icons';
import type { CSSProperties, HTMLAttributes, ReactNode, Ref } from 'react';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { IconButton } from '../ui/Button';
import { cx } from '../ui/cx';
import { AppIcon } from './AppIcon';
import type { AppInfo } from './types';
import styles from './Window.module.css';

/** Position and size in percent of the workspace, used by the design gallery. */
export interface WindowGeometry {
  x: number;
  y: number;
  width: number;
  height: number;
}

/** Placement in the medium size class: maximized, one half of a split, or behind the others. */
export type MediumPlacement = 'max' | 'left' | 'right' | 'hidden';

export interface WindowFrameProps {
  app: Pick<AppInfo, 'icon' | 'tone'>;
  title: string;
  focused: boolean;
  maximized?: boolean;
  minimized?: boolean;
  /** CSS lengths for left, top, width and height in the expanded size class. */
  position: { x: string; y: string; width: string; height: string };
  medium?: MediumPlacement;
  /** In the compact size class only the active window is shown, full screen. */
  compactActive?: boolean;
  layer: number;
  children: ReactNode;
  frameRef?: Ref<HTMLElement>;
  /** Replaces the app icon in the title bar, for example with the window menu button. */
  icon?: ReactNode;
  /** Pointer and mouse handlers of the title bar (dragging, double click). */
  titlebarProps?: HTMLAttributes<HTMLElement> | undefined;
  /** Extra elements inside the frame, for example resize handles. */
  decorations?: ReactNode;
  onFocusRequest?: () => void;
  onMinimize?: () => void;
  onToggleMaximize?: () => void;
  onClose?: () => void;
}

/** A window frame: title bar with icon, title and controls, and the app content in a size container. */
export function WindowFrame({
  app,
  title,
  focused,
  maximized = false,
  minimized = false,
  position,
  medium = 'max',
  compactActive = false,
  layer,
  children,
  frameRef,
  icon,
  titlebarProps,
  decorations,
  onFocusRequest,
  onMinimize,
  onToggleMaximize,
  onClose,
}: WindowFrameProps) {
  const { t } = useTranslation();
  const titleId = useId();
  const style = {
    '--x': position.x,
    '--y': position.y,
    '--w': position.width,
    '--h': position.height,
    '--layer': String(layer),
  } as CSSProperties;

  return (
    <section
      ref={frameRef}
      className={cx(styles.window, focused && styles.focused)}
      style={style}
      aria-labelledby={titleId}
      tabIndex={-1}
      data-maximized={maximized || undefined}
      data-minimized={minimized || undefined}
      data-medium={medium}
      data-compact-active={compactActive || undefined}
      onPointerDownCapture={onFocusRequest}
      onFocusCapture={onFocusRequest}
    >
      <header {...titlebarProps} className={cx(styles.titlebar, titlebarProps?.className)}>
        {icon ?? <AppIcon app={app} size={20} className={styles.icon} />}
        <h2 id={titleId} className={styles.title}>
          {title}
        </h2>
        <div className={styles.controls}>
          <IconButton
            label={t('window.minimize')}
            size="sm"
            className={styles.control}
            onPress={() => onMinimize?.()}
          >
            <IconMinus size={16} stroke={1.75} />
          </IconButton>
          <IconButton
            label={maximized ? t('window.restore') : t('window.maximize')}
            size="sm"
            className={cx(styles.control, styles.maximize)}
            onPress={() => onToggleMaximize?.()}
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
            onPress={() => onClose?.()}
          >
            <IconX size={17} stroke={1.75} />
          </IconButton>
        </div>
      </header>
      <div className={styles.content}>{children}</div>
      {decorations}
    </section>
  );
}

export interface WindowProps extends Omit<WindowFrameProps, 'position'> {
  geometry: WindowGeometry;
}

/** A static window for the design gallery, placed in percent of the workspace. */
export function Window({ geometry, ...props }: WindowProps) {
  return (
    <WindowFrame
      {...props}
      position={{
        x: `${String(geometry.x)}%`,
        y: `${String(geometry.y)}%`,
        width: `${String(geometry.width)}%`,
        height: `${String(geometry.height)}%`,
      }}
    />
  );
}
