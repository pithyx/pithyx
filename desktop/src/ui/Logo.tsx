import { cx } from './cx';
import styles from './Logo.module.css';

/** The Vyxis mark: an isometric cube with an amber top face. Decorative unless a label is given. */
export function LogoMark({ size = 32, label }: { size?: number; label?: string }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 100 100"
      role={label === undefined ? undefined : 'img'}
      aria-label={label}
      aria-hidden={label === undefined ? true : undefined}
      className={styles.mark}
    >
      <polygon points="50,32 82,50 50,68 18,50" className={styles.deep} />
      <polygon points="18,50 49,68 49,96 18,78" className={styles.left} />
      <polygon points="51,68 82,50 82,78 51,96" className={styles.right} />
      <polygon points="50,6 82,24 50,42 18,24" className={styles.top} />
    </svg>
  );
}

/** Mark plus word mark in Sora. */
export function Wordmark({ name, size = 'md' }: { name: string; size?: 'md' | 'lg' }) {
  return (
    <span className={cx(styles.wordmark, size === 'lg' && styles.large)}>
      <LogoMark size={size === 'lg' ? 44 : 28} />
      <span className={styles.name}>{name}</span>
    </span>
  );
}
