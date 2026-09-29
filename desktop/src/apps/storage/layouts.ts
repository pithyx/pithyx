import type { Pool } from '../sdk/internal';

/** Pool layouts the wizard compares; `mirrors` is striped mirror pairs (RAID 10). */
export type Layout = 'single' | 'stripe' | 'mirror' | 'mirrors' | 'raidz1' | 'raidz2' | 'raidz3';

/** How a disk is used in a layout, for the drawing: data, parity, or a copy of another disk (mirrors). */
export type DiskRole = 'data' | 'parity' | 'copy';

export interface LayoutOption {
  layout: Layout;
  /** One entry per disk; mirror pairs are grouped. */
  groups: DiskRole[][];
  /** Space for data, from the smallest disk: every disk counts as large as the smallest. */
  usableBytes: number;
  /** How many disks may fail without losing data: at least this many, and for mirror pairs up to `toleranceMax`. */
  tolerance: number;
  toleranceMax: number;
  /** Relative to one disk, 1 to 5. */
  read: number;
  write: number;
}

const level = (factor: number) => Math.max(1, Math.min(5, Math.round(factor)));

/** Every layout that works with these disks, safest-first within the same space. */
export function layoutOptions(sizes: readonly number[]): LayoutOption[] {
  const count = sizes.length;
  if (count === 0) {
    return [];
  }
  const smallest = Math.min(...sizes);
  const options: LayoutOption[] = [];
  const raidz = (parity: number): LayoutOption => ({
    layout: `raidz${String(parity)}` as Layout,
    groups: [
      Array.from({ length: count }, (_, index): DiskRole =>
        index < count - parity ? 'data' : 'parity',
      ),
    ],
    usableBytes: smallest * (count - parity),
    tolerance: parity,
    toleranceMax: parity,
    read: level(count - parity),
    // Parity is computed on every write, and a small write touches every disk of the vdev: a raidz writes about like
    // one disk until it is wide.
    write: level((count - parity) / 3),
  });
  if (count === 1) {
    options.push({
      layout: 'single',
      groups: [['data']],
      usableBytes: smallest,
      tolerance: 0,
      toleranceMax: 0,
      read: 1,
      write: 1,
    });
    return options;
  }
  options.push({
    layout: 'mirror',
    groups: [
      Array.from({ length: count }, (_, index): DiskRole => (index === 0 ? 'data' : 'copy')),
    ],
    usableBytes: smallest,
    tolerance: count - 1,
    toleranceMax: count - 1,
    read: level(count),
    write: 1,
  });
  if (count >= 4 && count % 2 === 0) {
    options.push({
      layout: 'mirrors',
      groups: Array.from({ length: count / 2 }, (): DiskRole[] => ['data', 'copy']),
      usableBytes: smallest * (count / 2),
      tolerance: 1,
      toleranceMax: count / 2,
      read: level(count),
      write: level(count / 2),
    });
  }
  for (const parity of [1, 2, 3]) {
    if (count >= parity + 2) {
      options.push(raidz(parity));
    }
  }
  options.push({
    layout: 'stripe',
    groups: [Array.from({ length: count }, (): DiskRole => 'data')],
    usableBytes: smallest * count,
    tolerance: 0,
    toleranceMax: 0,
    read: level(count),
    write: level(count),
  });
  return options;
}

/**
 * The layout recommended for a number of disks: one disk may fail from two disks on, two from six on, where a
 * rebuild takes long enough for a second failure to matter.
 */
export function recommendedLayout(count: number): Layout {
  if (count <= 1) {
    return 'single';
  }
  if (count === 2) {
    return 'mirror';
  }
  return count >= 6 ? 'raidz2' : 'raidz1';
}

/** Mixed sizes waste space: every disk counts as large as the smallest. */
export function wastedBytes(sizes: readonly number[]): number {
  if (sizes.length === 0) {
    return 0;
  }
  const smallest = Math.min(...sizes);
  return sizes.reduce((sum, size) => sum + size - smallest, 0);
}

/** Layouts of a vdev that can be added, with the fewest disks each needs. */
const ADD_LAYOUTS = [
  ['single', 1],
  ['mirror', 2],
  ['raidz1', 3],
  ['raidz2', 4],
  ['raidz3', 5],
] as const;
export type AddLayout = (typeof ADD_LAYOUTS)[number][0];

/** The layouts that work with this many disks: a single disk only alone, the others from their minimum. */
export function addLayouts(count: number): AddLayout[] {
  return ADD_LAYOUTS.filter(([layout, minimum]) =>
    layout === 'single' ? count === 1 : count >= minimum,
  ).map(([layout]) => layout);
}

/** The layout of the pool's data vdevs when they all share one, so the default adds a matching vdev. */
export function poolLayout(pool: Pool): AddLayout | null {
  const kinds = new Set(
    pool.vdevs
      .filter((vdev) => vdev.role === 'data')
      .map((vdev) =>
        vdev.type === 'mirror'
          ? 'mirror'
          : vdev.type === 'raidz'
            ? `raidz${String(vdev.parityDisks ?? 1)}`
            : 'single',
      ),
  );
  const [only] = kinds;
  return kinds.size === 1 && ADD_LAYOUTS.some(([layout]) => layout === only)
    ? (only as AddLayout)
    : null;
}
