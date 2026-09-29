import type { ResourceHistory, ResourceSample } from '../sdk/monitor';

/** One moment of the charts: the history's buckets and the live samples share this shape. */
export interface Point {
  /** Milliseconds since the epoch. */
  t: number;
  cpu: number;
  memoryUsed: number;
  arc: number | null;
  /** Received and sent bytes per second by interface. */
  network: Record<string, readonly [number, number]>;
  /** Read and written bytes per second by disk. */
  disks: Record<string, readonly [number, number]>;
}

/** How far back the charts reach. */
export const RANGES = [60, 600, 3600] as const;
export type Range = (typeof RANGES)[number];

/** The daemon's ring buffer holds one hour; so does the app. */
const SPAN_MS = 3_600_000;

/** More points than a chart is wide in pixels only cost time; longer ranges are averaged down to this. */
export const MAX_DRAWN = 300;

export function pointsFromHistory(history: ResourceHistory): Point[] {
  return history.times.map((time, index) => {
    const devices = (series: ResourceHistory['network']) =>
      Object.fromEntries(
        series.flatMap((device) => {
          const received = device.in[index];
          const sent = device.out[index];
          return received === null || received === undefined || sent === null || sent === undefined
            ? []
            : [[device.name, [received, sent] as const]];
        }),
      );
    return {
      t: Date.parse(time),
      cpu: history.cpuPercent[index] ?? 0,
      memoryUsed: history.memoryUsedBytes[index] ?? 0,
      arc: history.arcBytes[index] ?? null,
      network: devices(history.network),
      disks: devices(history.disks),
    };
  });
}

export function pointFromSample(sample: ResourceSample): Point {
  return {
    t: Date.parse(sample.at),
    cpu: sample.cpu.percent,
    memoryUsed: sample.memory.usedBytes,
    arc: sample.memory.arcBytes,
    network: Object.fromEntries(
      sample.network.map((rate) => [
        rate.name,
        [rate.receiveBytesPerSecond, rate.sendBytesPerSecond] as const,
      ]),
    ),
    disks: Object.fromEntries(
      sample.disks.map((rate) => [
        rate.name,
        [rate.readBytesPerSecond, rate.writeBytesPerSecond] as const,
      ]),
    ),
  };
}

/** Adds a live point and drops what is older than an hour before it. A point that is not newer is ignored. */
export function append(points: readonly Point[], point: Point): Point[] {
  const last = points.at(-1);
  if (last !== undefined && point.t <= last.t) {
    return [...points];
  }
  const start = point.t - SPAN_MS;
  const first = points.findIndex((candidate) => candidate.t > start);
  return [...(first < 0 ? [] : points.slice(first)), point];
}

/** A value over time; null where it is unknown, which breaks the line. */
export interface Series {
  t: number[];
  v: (number | null)[];
}

/**
 * The points of the last `range` seconds before `end`, one value each, averaged into at most `MAX_DRAWN` buckets.
 * The ring only holds seconds somebody watched: a gap longer than `maxGap` becomes a null, so the line breaks
 * instead of pretending the machine was idle.
 */
export function seriesOf(
  points: readonly Point[],
  end: number,
  range: Range,
  value: (point: Point) => number | null,
  maxGap = 12_000,
): Series {
  const start = end - range * 1000;
  const inRange = points.filter((point) => point.t >= start && point.t <= end);
  const bucket = (range * 1000) / MAX_DRAWN;
  const series: Series = { t: [], v: [] };
  let sum = 0;
  let count = 0;
  let bucketStart = Number.NaN;
  let previous = Number.NaN;
  const flush = (t: number) => {
    series.t.push(t);
    series.v.push(count === 0 ? null : sum / count);
    sum = 0;
    count = 0;
  };
  for (const point of inRange) {
    if (!Number.isNaN(previous) && point.t - previous > maxGap) {
      flush(previous);
      series.t.push(previous + 1);
      series.v.push(null);
      bucketStart = Number.NaN;
    }
    if (!Number.isNaN(bucketStart) && point.t - bucketStart >= bucket) {
      flush(previous);
      bucketStart = Number.NaN;
    }
    if (Number.isNaN(bucketStart)) {
      bucketStart = point.t;
    }
    const current = value(point);
    if (current !== null) {
      sum += current;
      count++;
    }
    previous = point.t;
  }
  if (!Number.isNaN(bucketStart)) {
    flush(previous);
  }
  return series;
}

/** The largest value of the series, at least `floor`, so an idle link does not fill the chart with noise. */
export function peak(series: readonly Series[], floor: number): number {
  let result = floor;
  for (const { v } of series) {
    for (const value of v) {
      if (value !== null && value > result) {
        result = value;
      }
    }
  }
  return result;
}

/** The names of the interfaces or disks that appear in the points, in the order they first did. */
export function deviceNames(
  points: readonly Point[],
  pick: (point: Point) => Record<string, unknown>,
): string[] {
  const names = new Set<string>();
  for (const point of points) {
    for (const name of Object.keys(pick(point))) {
      names.add(name);
    }
  }
  return [...names];
}

/**
 * An SVG path for the series in a `width` by `height` box: time from `start` to `end` left to right, 0 to `max`
 * bottom to top. Nulls break the line. With `area`, each run is closed down to the bottom for a filled area.
 */
export function pathOf(
  series: Series,
  start: number,
  end: number,
  max: number,
  width: number,
  height: number,
  area = false,
): string {
  const span = Math.max(end - start, 1);
  const x = (t: number) => (((t - start) / span) * width).toFixed(1);
  const y = (v: number) => (height - (Math.min(v, max) / (max || 1)) * height).toFixed(1);
  const parts: string[] = [];
  let run: { t: number; v: number }[] = [];
  const close = () => {
    const first = run[0];
    const last = run.at(-1);
    if (first !== undefined && last !== undefined) {
      const line = run.map(
        (point, index) => `${index === 0 ? 'M' : 'L'}${x(point.t)} ${y(point.v)}`,
      );
      parts.push(
        area
          ? `${line.join(' ')} L${x(last.t)} ${String(height)} L${x(first.t)} ${String(height)} Z`
          : line.join(' '),
      );
    }
    run = [];
  };
  series.t.forEach((t, index) => {
    const v = series.v[index];
    if (v === null || v === undefined) {
      close();
    } else {
      run.push({ t, v });
    }
  });
  close();
  return parts.join(' ');
}
