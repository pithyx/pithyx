import { useId } from 'react';
import { cx } from './cx';
import styles from './Logo.module.css';

/**
 * - `static`: open, no glow (small sizes, favicon)
 * - `closed`: lid shut
 * - `hover`: shut; opens while the surrounding button or link is hovered, focused or expanded
 * - `welcome`: opens once
 * - `loop`: opens and closes continuously (setup, installer)
 * - `loading`: open, the lid breathes (waiting for the daemon)
 */
export type LogoMotion = 'static' | 'closed' | 'hover' | 'welcome' | 'loop' | 'loading';

const motionClass: Record<LogoMotion, string | undefined> = {
  static: undefined,
  closed: styles.closed,
  hover: styles.hover,
  welcome: styles.welcome,
  loop: styles.loop,
  loading: styles.loading,
};

// Below this size the glow is a smudge, so small marks never get it.
const GLOW_MIN_SIZE = 32;

/** The Pithyx mark: an isometric pithos with an amber lid. Decorative unless a label is given. */
export function LogoMark({
  size = 32,
  label,
  motion = 'static',
}: {
  size?: number;
  label?: string;
  motion?: LogoMotion;
}) {
  const filterId = `logo-glow-${useId().replace(/[^\w-]/g, '')}`;
  const glow = motion !== 'static' && motion !== 'closed' && size >= GLOW_MIN_SIZE;
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 100 100"
      role={label === undefined ? undefined : 'img'}
      aria-label={label}
      aria-hidden={label === undefined ? true : undefined}
      className={cx(styles.mark, motionClass[motion])}
    >
      <polygon points="50,25.52 82,44 50,62.48 18,44" className={styles.inside} />
      {glow && (
        <>
          <defs>
            <filter id={filterId} x="-50%" y="-50%" width="200%" height="200%">
              <feGaussianBlur stdDeviation="3.5" />
            </filter>
          </defs>
          <polygon
            points="50,32.5 70,44 50,55.5 30,44"
            className={styles.glow}
            filter={`url(#${filterId})`}
          />
        </>
      )}
      <polygon points="18,45.4 49.3,63.47 49.3,90.07 18,72" className={styles.left} />
      <polygon points="50.7,63.47 82,45.4 82,72 50.7,90.07" className={styles.right} />
      <g className={styles.lid}>
        <polygon points="18,28 50,46.48 50,49.48 18,31" className={styles.lidLeft} />
        <polygon points="50,46.48 82,28 82,31 50,49.48" className={styles.lidRight} />
        <polygon points="50,9.52 82,28 50,46.48 18,28" className={styles.lidTop} />
      </g>
    </svg>
  );
}

/** Mark plus word mark in Sora. `mark` is the visible spelling; screen readers hear `name`. */
export function Wordmark({
  name,
  mark = name,
  size = 'md',
  motion = 'static',
}: {
  name: string;
  mark?: string;
  size?: 'md' | 'lg';
  motion?: LogoMotion;
}) {
  return (
    <span className={cx(styles.wordmark, size === 'lg' && styles.large)}>
      <LogoMark size={size === 'lg' ? 48 : 32} motion={motion} />
      <span
        className={styles.name}
        role={mark === name ? undefined : 'img'}
        aria-label={mark === name ? undefined : name}
      >
        {mark}
      </span>
    </span>
  );
}
