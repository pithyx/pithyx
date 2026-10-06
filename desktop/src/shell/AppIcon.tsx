import { cx } from '@pithyx/ui';
import { LogoMark } from '../ui/Logo';
import styles from './AppIcon.module.css';
import type { AppInfo } from './types';

/**
 * A rounded tile with the app's glyph. An app without an icon of its own gets the default icon: the Pithyx box on a
 * neutral tile (D307). Decorative: the app name is always shown or announced next to it.
 */
export function AppIcon({
  app,
  size = 32,
  className,
}: {
  app: Pick<AppInfo, 'icon' | 'tone'>;
  size?: number;
  className?: string;
}) {
  const Glyph = app.icon;
  return (
    <span
      className={cx(styles.tile, Glyph === null ? styles.neutral : styles[app.tone], className)}
      style={{ width: size, height: size, borderRadius: Math.round(size * 0.28) }}
      data-default-icon={Glyph === null || undefined}
      aria-hidden
    >
      {Glyph === null ? (
        <LogoMark size={Math.round(size * 0.72)} />
      ) : (
        <Glyph size={Math.round(size * 0.58)} stroke={1.75} />
      )}
    </span>
  );
}
