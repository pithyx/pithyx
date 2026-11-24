import { cx } from '@pithyx/ui';
import { useId } from 'react';
import { formatBytes } from '../../desktop/format';
import { useFormatLocale } from '../../i18n';
import { IconChartBar } from '../../ui/icons';
import styles from './Backup.module.css';

/** The smallest of 1, 2, 2.5 and 5 times a power of ten at or above `value`, so the axis reads in round numbers. */
export function roundUp(value: number): number {
  if (value <= 0) {
    return 1;
  }
  const power = 10 ** Math.floor(Math.log10(value));
  return ([1, 2, 2.5, 5, 10].find((step) => step * power >= value) ?? 10) * power;
}

/** One place on the chart: a bar, and a point of the line that is missing where nothing was measured. */
export interface ChartPoint {
  key: string;
  /** The day or time the place stands for, for the ticks below. */
  at: Date;
  bar: number;
  line: number | null;
}

/**
 * Bytes as bars and a line, each on its own scale with its axis: the data added per day and what the repositories
 * take on the overview, the data each version added and its size on a backup's versions.
 */
export function BytesChart({
  title,
  barLegend,
  lineLegend,
  label,
  points,
}: {
  title: string;
  barLegend: string;
  lineLegend: string;
  /** What the chart shows in words, for screen readers. */
  label: string;
  points: readonly ChartPoint[];
}) {
  const locale = useFormatLocale();
  const titleId = useId();
  const count = Math.max(1, points.length);
  const width = count * 10;
  const height = 100;
  const barMax = roundUp(Math.max(0, ...points.map((point) => point.bar)));
  const lines = points.map((point) => point.line);
  const lineMax = roundUp(Math.max(0, ...lines.map((value) => value ?? 0)));
  // A place without a measurement breaks the line.
  const line = lines
    .map((value, index) =>
      value === null
        ? ''
        : `${index === 0 || lines[index - 1] === null ? 'M' : 'L'}${String(index * 10 + 5)},${String(height - (value / lineMax) * height)}`,
    )
    .join('');
  // Few places stand apart in time, so their dates are longer apart and get fewer ticks.
  const ticks = (count < 15 ? [0, 0.5, 1] : [0, 0.25, 0.5, 0.75, 1]).map((share) =>
    Math.round(share * (count - 1)),
  );
  const tickDate = new Intl.DateTimeFormat(locale, { day: 'numeric', month: 'short' });
  const zero = new Intl.NumberFormat(locale).format(0);
  return (
    <section className={styles.panel} aria-labelledby={titleId}>
      <div className={styles.panelHead}>
        <IconChartBar size={18} stroke={1.75} aria-hidden />
        <h2 id={titleId} className={styles.panelTitle}>
          {title}
        </h2>
        <span className={cx(styles.legend, styles.panelEnd)} aria-hidden>
          <span data-state="added">{barLegend}</span>
          <span data-state="size">{lineLegend}</span>
        </span>
      </div>
      <div className={styles.chart}>
        <span className={styles.axis} aria-hidden>
          <span>{formatBytes(barMax, locale)}</span>
          <span>{formatBytes(barMax / 2, locale)}</span>
          <span>{zero}</span>
        </span>
        <svg
          className={styles.chartSvg}
          viewBox={`0 0 ${String(width)} ${String(height)}`}
          preserveAspectRatio="none"
          role="img"
          aria-label={label}
        >
          {[0, 0.5, 1].map((share) => (
            <line
              key={share}
              className={styles.chartGrid}
              x1={0}
              x2={width}
              y1={height * share}
              y2={height * share}
              vectorEffect="non-scaling-stroke"
            />
          ))}
          {points.map((point, index) => {
            const barHeight = (point.bar / barMax) * height;
            return (
              <rect
                key={point.key}
                className={styles.chartBar}
                x={index * 10 + 2}
                y={height - barHeight}
                width={6}
                height={barHeight}
              />
            );
          })}
          <path className={styles.chartLine} d={line} vectorEffect="non-scaling-stroke" />
        </svg>
        <span className={cx(styles.axis, styles.axisEnd)} aria-hidden>
          <span>{formatBytes(lineMax, locale)}</span>
          <span>{formatBytes(lineMax / 2, locale)}</span>
          <span>{zero}</span>
        </span>
        <span className={styles.ticks} aria-hidden>
          {[...new Set(ticks)].map((index) => {
            const point = points[index];
            return point === undefined ? null : (
              <span key={point.key} style={{ left: `${String(((index + 0.5) / count) * 100)}%` }}>
                {tickDate.format(point.at)}
              </span>
            );
          })}
        </span>
      </div>
    </section>
  );
}
