import { useId } from 'react';
import styles from './Wallpaper.module.css';

interface Cube {
  x: number;
  y: number;
  scale: number;
  depth: number;
}

// Exploded isometric cubes after the logo: a floating lid above an open box. Positions are in the 1600x1000
// viewBox; depth fades the smaller cubes into the background.
const CUBES: Cube[] = [
  { x: 1180, y: 430, scale: 7.2, depth: 1 },
  { x: 300, y: 760, scale: 3.4, depth: 0.6 },
  { x: 1460, y: 900, scale: 2.2, depth: 0.45 },
  { x: 610, y: 190, scale: 1.6, depth: 0.35 },
  { x: 90, y: 250, scale: 2.6, depth: 0.4 },
  { x: 880, y: 860, scale: 1.3, depth: 0.3 },
];

function points(coordinates: [number, number][], cube: Cube): string {
  return coordinates
    .map(
      ([x, y]) =>
        `${String(cube.x + (x - 50) * cube.scale)},${String(cube.y + (y - 50) * cube.scale)}`,
    )
    .join(' ');
}

/** The default wallpaper, drawn in the brand palette. Follows the theme. */
export function Wallpaper() {
  const glow = `wallpaper-glow-${useId()}`;
  return (
    <svg
      className={styles.wallpaper}
      viewBox="0 0 1600 1000"
      preserveAspectRatio="xMidYMid slice"
      aria-hidden
    >
      <defs>
        <radialGradient id={glow} cx="72%" cy="38%" r="70%">
          <stop offset="0" className={styles.glowInner} />
          <stop offset="1" className={styles.glowOuter} />
        </radialGradient>
      </defs>
      <rect width="1600" height="1000" className={styles.base} />
      <rect width="1600" height="1000" fill={`url(#${glow})`} />
      {CUBES.map((cube) => (
        <g key={`${String(cube.x)}-${String(cube.y)}`} opacity={cube.depth}>
          <polygon
            className={styles.top}
            points={points(
              [
                [50, 32],
                [82, 50],
                [50, 68],
                [18, 50],
              ],
              cube,
            )}
          />
          <polygon
            className={styles.left}
            points={points(
              [
                [18, 50],
                [49, 68],
                [49, 96],
                [18, 78],
              ],
              cube,
            )}
          />
          <polygon
            className={styles.right}
            points={points(
              [
                [51, 68],
                [82, 50],
                [82, 78],
                [51, 96],
              ],
              cube,
            )}
          />
          <polygon
            className={styles.lid}
            points={points(
              [
                [50, 6],
                [82, 24],
                [50, 42],
                [18, 24],
              ],
              cube,
            )}
          />
        </g>
      ))}
    </svg>
  );
}
