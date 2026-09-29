import { describe, expect, it } from 'vitest';
import type { Pool, Vdev } from '../sdk/internal';
import { addLayouts, layoutOptions, poolLayout, recommendedLayout, wastedBytes } from './layouts';

const TB = 1e12;

describe('pool layouts', () => {
  it('offer only a single disk for one disk', () => {
    expect(layoutOptions([4 * TB]).map((option) => option.layout)).toEqual(['single']);
  });

  it('compare raidz1 with two mirror pairs for four disks', () => {
    const options = layoutOptions([4 * TB, 4 * TB, 4 * TB, 4 * TB]);
    expect(options.map((option) => option.layout)).toEqual([
      'mirror',
      'mirrors',
      'raidz1',
      'raidz2',
      'stripe',
    ]);
    const raidz1 = options.find((option) => option.layout === 'raidz1');
    const mirrors = options.find((option) => option.layout === 'mirrors');
    expect(raidz1).toMatchObject({ usableBytes: 12 * TB, tolerance: 1, toleranceMax: 1 });
    expect(raidz1?.groups).toEqual([['data', 'data', 'data', 'parity']]);
    expect(mirrors).toMatchObject({ usableBytes: 8 * TB, tolerance: 1, toleranceMax: 2 });
    expect(mirrors?.groups).toEqual([
      ['data', 'copy'],
      ['data', 'copy'],
    ]);
    expect(mirrors?.write).toBeGreaterThan(raidz1?.write ?? 0);
  });

  it('count every disk as large as the smallest', () => {
    const [mirror] = layoutOptions([4 * TB, 2 * TB]);
    expect(mirror).toMatchObject({ layout: 'mirror', usableBytes: 2 * TB, tolerance: 1 });
    expect(wastedBytes([4 * TB, 2 * TB])).toBe(2 * TB);
  });

  it('never let a stripe or single disk survive a failure, and let raidz3 lose three', () => {
    const options = layoutOptions(Array.from({ length: 6 }, () => TB));
    expect(options.find((option) => option.layout === 'stripe')?.tolerance).toBe(0);
    expect(options.find((option) => option.layout === 'raidz3')).toMatchObject({
      tolerance: 3,
      usableBytes: 3 * TB,
    });
    expect(options.find((option) => option.layout === 'mirror')).toMatchObject({
      tolerance: 5,
      usableBytes: TB,
    });
  });

  it('recommend a layout that survives a failure from two disks on', () => {
    expect([1, 2, 3, 4, 5, 6, 8].map(recommendedLayout)).toEqual([
      'single',
      'mirror',
      'raidz1',
      'raidz1',
      'raidz1',
      'raidz2',
      'raidz2',
    ]);
    for (const count of [2, 3, 4, 5, 6, 8, 12]) {
      const option = layoutOptions(Array.from({ length: count }, () => TB)).find(
        (candidate) => candidate.layout === recommendedLayout(count),
      );
      expect(option?.tolerance, String(count)).toBeGreaterThanOrEqual(1);
    }
  });
});

function vdev(type: string, parityDisks: number | null = null, role = 'data'): Vdev {
  return { type, parityDisks, role, children: [] } as unknown as Vdev;
}

function pool(...vdevs: Vdev[]): Pool {
  return { vdevs } as unknown as Pool;
}

describe('adding a vdev', () => {
  it('offers a single disk only alone and every other layout from its minimum', () => {
    expect(addLayouts(0)).toEqual([]);
    expect(addLayouts(1)).toEqual(['single']);
    expect(addLayouts(2)).toEqual(['mirror']);
    expect(addLayouts(3)).toEqual(['mirror', 'raidz1']);
    expect(addLayouts(5)).toEqual(['mirror', 'raidz1', 'raidz2', 'raidz3']);
  });

  it('matches the layout of the pool when all its data vdevs share one', () => {
    expect(poolLayout(pool(vdev('mirror'), vdev('mirror'), vdev('disk', null, 'spare')))).toBe(
      'mirror',
    );
    expect(poolLayout(pool(vdev('raidz', 2)))).toBe('raidz2');
    expect(poolLayout(pool(vdev('disk')))).toBe('single');
    expect(poolLayout(pool(vdev('mirror'), vdev('raidz', 1)))).toBeNull();
  });
});
