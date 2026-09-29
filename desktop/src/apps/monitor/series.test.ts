import { describe, expect, it } from 'vitest';
import type { ResourceHistory, ResourceSample } from '../sdk/monitor';
import {
  append,
  deviceNames,
  MAX_DRAWN,
  pathOf,
  peak,
  pointFromSample,
  pointsFromHistory,
  seriesOf,
  type Point,
} from './series';

const T0 = Date.parse('2026-09-29T01:00:00Z');

function point(seconds: number, cpu: number, disks: Point['disks'] = {}): Point {
  return { t: T0 + seconds * 1000, cpu, memoryUsed: 0, arc: null, network: {}, disks };
}

describe('pointsFromHistory', () => {
  it('turns the buckets into points and leaves devices out where they were missing', () => {
    const history: ResourceHistory = {
      bucketSeconds: 5,
      times: ['2026-09-29T01:00:05Z', '2026-09-29T01:00:10Z'],
      cpuPercent: [10, 20],
      memoryUsedBytes: [100, 200],
      arcBytes: [null, 50],
      network: [{ name: 'eth0', in: [1, 2], out: [3, 4] }],
      disks: [{ name: 'sda', in: [null, 7], out: [null, 8] }],
    };

    const points = pointsFromHistory(history);

    expect(points).toEqual([
      { t: T0 + 5000, cpu: 10, memoryUsed: 100, arc: null, network: { eth0: [1, 3] }, disks: {} },
      {
        t: T0 + 10_000,
        cpu: 20,
        memoryUsed: 200,
        arc: 50,
        network: { eth0: [2, 4] },
        disks: { sda: [7, 8] },
      },
    ]);
  });
});

describe('pointFromSample', () => {
  it('keeps the rates of every interface and disk', () => {
    const sample = {
      at: '2026-09-29T01:00:01Z',
      cpu: { percent: 12.5, cores: [12.5] },
      memory: { usedBytes: 5, arcBytes: 6 },
      network: [{ name: 'eth0', receiveBytesPerSecond: 1, sendBytesPerSecond: 2 }],
      disks: [{ name: 'sda', readBytesPerSecond: 3, writeBytesPerSecond: 4, busyPercent: 0 }],
    } as unknown as ResourceSample;

    expect(pointFromSample(sample)).toEqual({
      t: T0 + 1000,
      cpu: 12.5,
      memoryUsed: 5,
      arc: 6,
      network: { eth0: [1, 2] },
      disks: { sda: [3, 4] },
    });
  });
});

describe('append', () => {
  it('drops points older than an hour and ignores points that are not newer', () => {
    let points: Point[] = [];
    for (const seconds of [0, 1800, 3600, 3601]) {
      points = append(points, point(seconds, seconds));
    }
    points = append(points, point(3601, 99));

    expect(points.map((candidate) => candidate.cpu)).toEqual([1800, 3600, 3601]);
  });
});

describe('seriesOf', () => {
  it('returns the points of the range one by one while they fit', () => {
    const points = [0, 1, 2, 3].map((seconds) => point(seconds, seconds * 10));

    const series = seriesOf(points, T0 + 3000, 60, (candidate) => candidate.cpu);

    expect(series).toEqual({ t: points.map((candidate) => candidate.t), v: [0, 10, 20, 30] });
  });

  it('averages a long range down to at most MAX_DRAWN values', () => {
    const points = Array.from({ length: 3600 }, (_, seconds) =>
      point(seconds, seconds % 2 === 0 ? 0 : 100),
    );

    const series = seriesOf(points, T0 + 3599_000, 3600, (candidate) => candidate.cpu);

    expect(series.v.length).toBeLessThanOrEqual(MAX_DRAWN + 1);
    expect(series.v.every((value) => value !== null && value > 40 && value < 60)).toBe(true);
  });

  it('breaks the line where nobody watched', () => {
    const points = [point(0, 1), point(1, 2), point(100, 3), point(101, 4)];

    const series = seriesOf(points, T0 + 101_000, 600, (candidate) => candidate.cpu);

    expect(series.v).toEqual([1.5, null, 3.5]);
  });

  it('is null where a device has no value', () => {
    const points = [point(0, 0, { sda: [1, 2] }), point(1, 0), point(2, 0, { sda: [3, 4] })];

    const series = seriesOf(points, T0 + 2000, 60, (candidate) => candidate.disks.sda?.[0] ?? null);

    expect(series.v).toEqual([1, null, 3]);
  });
});

describe('peak', () => {
  it('is the largest value, but never below the floor', () => {
    expect(
      peak(
        [
          { t: [1, 2], v: [5, null] },
          { t: [1], v: [9] },
        ],
        1,
      ),
    ).toBe(9);
    expect(peak([{ t: [1], v: [0.5] }], 1000)).toBe(1000);
  });
});

describe('deviceNames', () => {
  it('lists every device in the order it first appeared', () => {
    const points = [point(0, 0, { sdb: [0, 0] }), point(1, 0, { sda: [0, 0], sdb: [0, 0] })];

    expect(deviceNames(points, (candidate) => candidate.disks)).toEqual(['sdb', 'sda']);
  });
});

describe('pathOf', () => {
  it('draws time left to right and values bottom to top, with a break at null', () => {
    const d = pathOf({ t: [0, 50, 60, 100], v: [0, 100, null, 50] }, 0, 100, 100, 100, 10);

    expect(d).toBe('M0.0 10.0 L50.0 0.0 M100.0 5.0');
  });

  it('closes an area down to the bottom', () => {
    expect(pathOf({ t: [0, 100], v: [100, 100] }, 0, 100, 100, 100, 10, true)).toBe(
      'M0.0 0.0 L100.0 0.0 L100.0 10 L0.0 10 Z',
    );
  });

  it('clips values above the maximum', () => {
    expect(pathOf({ t: [0], v: [500] }, 0, 100, 100, 100, 10)).toBe('M0.0 0.0');
  });
});
