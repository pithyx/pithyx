import { Fragment, useId } from 'react';
import { cx } from '@pithyx/ui';
import styles from './Logo.module.css';

/**
 * - `static`: lid shut, no glow (small sizes, app icons, favicon); the default
 * - `closed`: lid shut, the same as `static`
 * - `hover`: shut; opens while the surrounding button or link is hovered, focused or expanded
 * - `welcome`: opens once
 * - `loop`: opens and closes continuously (setup, installer)
 * - `loading`: open, the lid breathes (waiting for the daemon)
 */
export type LogoMotion = 'static' | 'closed' | 'hover' | 'welcome' | 'loop' | 'loading';

const motionClass: Record<LogoMotion, string | undefined> = {
  static: styles.closed,
  closed: styles.closed,
  hover: styles.hover,
  welcome: styles.welcome,
  loop: styles.loop,
  loading: styles.loading,
};

// A shut mark never moves, so it is centred like desktop/public/logo.svg: the closed lid and the body span y 22.52 to
// 90.07, so the view moves down by half the empty space below.
const CLOSED_OFFSET = 6.3;

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
  const shut = motion === 'static' || motion === 'closed';
  const glow = !shut && size >= GLOW_MIN_SIZE;
  return (
    <svg
      width={size}
      height={size}
      viewBox={shut ? `0 ${String(CLOSED_OFFSET)} 100 100` : '0 0 100 100'}
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
        <BrandText text={mark} />
      </span>
    </span>
  );
}

// Sora has no Greek, so the theta is drawn: Sora SemiBold's O (outline taken from the shipped font, units per em 1000)
// with a bar as thick and as high as the bar of its H.
const THETA_PATH =
  'M433 -19Q334 -19 261 16Q188 51 140.5 107.5Q93 164 70 229.5Q47 295 47 356L47 378Q47 445 71 511Q95 577 143.5 631.5' +
  'Q192 686 264 718.5Q336 751 433 751Q528 751 600.5 718.5Q673 686 721 631.5Q769 577 793.5 511Q818 445 818 378L818 356' +
  'Q818 295 794.5 229.5Q771 164 723.5 107.5Q676 51 603.5 16Q531 -19 433 -19ZM433 113Q507 113 561 147.5Q615 182 644.5 240' +
  'Q674 298 674 366Q674 438 644.5 495.5Q615 553 561 586Q507 619 433 619Q358 619 304 586Q250 553 220.5 495.5' +
  'Q191 438 191 366Q191 298 220 240Q249 182 303.5 147.5Q358 113 433 113Z' +
  'M180 303L685 303L685 428L180 428Z';

/**
 * Text in the display font with every capital theta drawn in Sora's style. The real character lies transparent over
 * the drawing, so selecting and copying the text gives the Greek capital theta. Screen readers get the text from the caller.
 */
export function BrandText({ text }: { text: string }) {
  return text.split('Θ').map((part, index) => (
    <Fragment key={index}>
      {index > 0 && (
        <span className={styles.thetaGlyph}>
          <svg className={styles.theta} viewBox="0 -751 865 770" aria-hidden>
            <path d={THETA_PATH} transform="scale(1 -1)" />
          </svg>
          <span className={styles.thetaText}>Θ</span>
        </span>
      )}
      {part}
    </Fragment>
  ));
}
