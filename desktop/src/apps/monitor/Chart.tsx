import styles from './Monitor.module.css';
import { pathOf, type Series } from './series';

const WIDTH = 300;
const HEIGHT = 80;

/**
 * A small time chart drawn as SVG paths: the first series as a filled area with its line, the second as a dashed
 * line. The picture is decorative for screen readers; `label` says what it shows and the current values, which the
 * card also prints as text.
 */
export function Chart({
  main,
  second,
  start,
  end,
  max,
  label,
  maxLabel,
}: {
  main: Series;
  second?: Series;
  start: number;
  end: number;
  max: number;
  label: string;
  maxLabel: string;
}) {
  return (
    <figure className={styles.chart}>
      <svg
        className={styles.chartSvg}
        viewBox={`0 0 ${String(WIDTH)} ${String(HEIGHT)}`}
        preserveAspectRatio="none"
        role="img"
        aria-label={label}
      >
        {[0.25, 0.5, 0.75].map((share) => (
          <line
            key={share}
            x1={0}
            x2={WIDTH}
            y1={HEIGHT * share}
            y2={HEIGHT * share}
            vectorEffect="non-scaling-stroke"
          />
        ))}
        <path className={styles.areaMain} d={pathOf(main, start, end, max, WIDTH, HEIGHT, true)} />
        <path
          className={styles.lineMain}
          d={pathOf(main, start, end, max, WIDTH, HEIGHT)}
          vectorEffect="non-scaling-stroke"
        />
        {second !== undefined && (
          <path
            className={styles.lineSecond}
            d={pathOf(second, start, end, max, WIDTH, HEIGHT)}
            vectorEffect="non-scaling-stroke"
          />
        )}
      </svg>
      <span className={styles.chartMax} aria-hidden="true">
        {maxLabel}
      </span>
    </figure>
  );
}
