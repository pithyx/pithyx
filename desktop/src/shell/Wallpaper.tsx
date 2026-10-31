import { useId, type CSSProperties } from 'react';
import styles from './Wallpaper.module.css';

interface Cube {
  x: number;
  y: number;
  scale: number;
  depth: number;
}

// Boxes drawn like the logo in the menu button: the geometry of ui/Logo.tsx in its 100x100 units, with the lid in its
// closed position, 13 units lower (as in ui/Logo.module.css), so only a narrow gap shows. Positions are in the 1600x1000
// viewBox: one large box right of the middle above the amber glow, smaller ones around it, away from the icon column
// at the left (D398); depth fades them into the background. The lower ones end above y 890, so a 16:9 screen, which
// cuts the viewBox to y 50 to 950, still shows them whole above the taskbar.
const CUBES: Cube[] = [
  { x: 1180, y: 420, scale: 6, depth: 1 },
  { x: 420, y: 710, scale: 3.4, depth: 0.6 },
  { x: 1460, y: 750, scale: 2.2, depth: 0.45 },
  { x: 660, y: 200, scale: 1.6, depth: 0.35 },
  { x: 360, y: 380, scale: 2.4, depth: 0.4 },
  { x: 880, y: 790, scale: 1.3, depth: 0.3 },
];

const LID_DROP = 13;

type Shape = [number, number][];

const SHAPES: {
  className: 'inside' | 'left' | 'right' | 'lidLeft' | 'lidRight' | 'lid';
  points: Shape;
  lid?: true;
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
    lid: true,
  },
  {
    className: 'lidRight',
    points: [
      [50, 46.48],
      [82, 28],
      [82, 31],
      [50, 49.48],
    ],
    lid: true,
  },
  {
    className: 'lid',
    points: [
      [50, 9.52],
      [82, 28],
      [50, 46.48],
      [18, 28],
    ],
    lid: true,
  },
];

function points(coordinates: Shape, cube: Cube, drop: number): string {
  return coordinates
    .map(
      ([x, y]) =>
        `${String(cube.x + (x - 50) * cube.scale)},${String(cube.y + (y + drop - 50) * cube.scale)}`,
    )
    .join(' ');
}

/** The wallpaper in the brand palette, with the Pithyx boxes or plain. Follows the theme. */
export function Wallpaper({ plain = false }: { plain?: boolean }) {
  const glow = `wallpaper-glow-${useId()}`;
  return (
    <svg
      className={styles.wallpaper}
      data-plain={plain || undefined}
      viewBox="0 0 1600 1000"
      preserveAspectRatio="xMidYMid slice"
      aria-hidden
    >
      <defs>
        <linearGradient id={`${glow}-base`} x1="0.25" y1="0" x2="0.75" y2="1">
          <stop offset="0" className={styles.baseTop} />
          <stop offset="1" className={styles.baseBottom} />
        </linearGradient>
        <radialGradient id={glow} cx="78%" cy="40%" r="45%">
          <stop offset="0" className={styles.glowInner} />
          <stop offset="0.6" className={styles.glowOuter} />
        </radialGradient>
      </defs>
      <rect width="1600" height="1000" fill={`url(#${glow}-base)`} />
      <rect className={styles.glow} width="1600" height="1000" fill={`url(#${glow})`} />
      {CUBES.map((cube) => (
        <g
          key={`${String(cube.x)}-${String(cube.y)}`}
          className={styles.cube}
          style={{ '--depth': cube.depth } as CSSProperties}
        >
          {SHAPES.map((shape) => (
            <polygon
              key={shape.className}
              className={styles[shape.className]}
              points={points(shape.points, cube, shape.lid === true ? LID_DROP : 0)}
            />
          ))}
        </g>
      ))}
    </svg>
  );
}
