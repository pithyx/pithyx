import { cx } from '../ui/cx';
import styles from './AppIcon.module.css';
import type { AppInfo } from './types';

/** A rounded tile with the app's glyph. Decorative: the app name is always shown or announced next to it. */
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
      className={cx(styles.tile, styles[app.tone], className)}
      style={{ width: size, height: size, borderRadius: Math.round(size * 0.28) }}
      aria-hidden
    >
      <Glyph size={Math.round(size * 0.58)} stroke={1.75} />
    </span>
  );
}
