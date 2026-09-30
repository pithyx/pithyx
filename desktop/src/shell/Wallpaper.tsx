import { useId } from 'react';
import styles from './Wallpaper.module.css';

interface Cube {
  x: number;
  y: number;
  scale: number;
  depth: number;
}

// Open boxes with a floating lid, drawn exactly like the logo (desktop/public/logo.svg: the lid floats 16 units above
// the box) in its 100x100 units. Positions are in the 1600x1000 viewBox; depth fades the smaller boxes into the
// background.
const CUBES: Cube[] = [
  { x: 1180, y: 430, scale: 7.2, depth: 1 },
  { x: 300, y: 760, scale: 3.4, depth: 0.6 },
  { x: 1460, y: 900, scale: 2.2, depth: 0.45 },
  { x: 610, y: 190, scale: 1.6, depth: 0.35 },
  { x: 90, y: 250, scale: 2.6, depth: 0.4 },
  { x: 880, y: 860, scale: 1.3, depth: 0.3 },
];

type Shape = [number, number][];

const SHAPES: {
  className: 'inside' | 'left' | 'right' | 'lidLeft' | 'lidRight' | 'lid';
  points: Shape;
}[] = [
  {
    className: 'inside',
    points: [
      [50, 25.52],
      [82, 44],
      [50, 62.48],
      [18, 44],
    ],
  },
  {
    className: 'left',
    points: [
      [18, 45.4],
      [49.3, 63.47],
      [49.3, 90.07],
      [18, 72],
    ],
  },
  {
    className: 'right',
    points: [
      [50.7, 63.47],
      [82, 45.4],
      [82, 72],
      [50.7, 90.07],
    ],
  },
  {
    className: 'lidLeft',
    points: [
      [18, 28],
      [50, 46.48],
      [50, 49.48],
      [18, 31],
    ],
  },
  {
    className: 'lidRight',
    points: [
      [50, 46.48],
      [82, 28],
      [82, 31],
      [50, 49.48],
    ],
  },
  {
    className: 'lid',
    points: [
      [50, 9.52],
      [82, 28],
      [50, 46.48],
      [18, 28],
    ],
  },
];

function points(coordinates: Shape, cube: Cube): string {
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
          {SHAPES.map((shape) => (
            <polygon
              key={shape.className}
              className={styles[shape.className]}
              points={points(shape.points, cube)}
            />
          ))}
        </g>
      ))}
    </svg>
  );
}
