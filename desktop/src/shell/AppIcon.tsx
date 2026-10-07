import { cx } from '@pithyx/ui';
import { useState } from 'react';
import { LogoMark } from '../ui/Logo';
import styles from './AppIcon.module.css';
import type { AppInfo } from './types';

/**
 * A rounded tile with the app's glyph, or an installed app's own icon filling it. An app without an icon of its own,
 * or whose icon does not load, gets the default icon: the Pithyx box on a neutral tile (D307). Decorative: the app
 * name is always shown or announced next to it.
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
  const [failed, setFailed] = useState<string | null>(null);
  const glyph = app.icon;
  const image = typeof glyph === 'string' && glyph !== failed ? glyph : null;
  const Glyph = typeof glyph === 'string' ? null : glyph;
  const neutral = image === null && Glyph === null;
  return (
    <span
      className={cx(
        styles.tile,
        neutral ? styles.neutral : image === null ? styles[app.tone] : styles.image,
        className,
      )}
      style={{ width: size, height: size, borderRadius: Math.round(size * 0.28) }}
      data-default-icon={neutral || undefined}
      aria-hidden
    >
      {image !== null ? (
        <img
          className={styles.picture}
          src={image}
          alt=""
          width={size}
          height={size}
          draggable={false}
          onError={() => {
            setFailed(image);
          }}
        />
      ) : Glyph === null ? (
        <LogoMark size={Math.round(size * 0.72)} />
      ) : (
        <Glyph size={Math.round(size * 0.58)} stroke={1.75} />
      )}
    </span>
  );
}
