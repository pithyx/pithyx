import type { ProcessInfo } from '../sdk/monitor';

export type SortKey = 'name' | 'pid' | 'user' | 'cpu' | 'memory';
export type Direction = 'ascending' | 'descending';

/** Numbers start high first, names alphabetically. */
export function defaultDirection(key: SortKey): Direction {
  return key === 'cpu' || key === 'memory' ? 'descending' : 'ascending';
}

/**
 * The processes whose name or user contains `filter` (case-insensitive), sorted by `key`. Ties are broken by the
 * pid, so rows do not jump around between two lists that are equal for the sort key. An unknown CPU share (the
 * first list) sorts as zero.
 */
export function sortProcesses(
  processes: readonly ProcessInfo[],
  key: SortKey,
  direction: Direction,
  filter: string,
  locale: string,
): ProcessInfo[] {
  const needle = filter.trim().toLocaleLowerCase(locale);
  const collator = new Intl.Collator(locale, { numeric: true, sensitivity: 'base' });
  const compare = (a: ProcessInfo, b: ProcessInfo): number => {
    switch (key) {
      case 'name':
        return collator.compare(a.name, b.name);
      case 'user':
        return collator.compare(a.user, b.user);
      case 'pid':
        return a.pid - b.pid;
      case 'cpu':
        return (a.cpuPercent ?? 0) - (b.cpuPercent ?? 0);
      case 'memory':
        return a.memoryBytes - b.memoryBytes;
    }
  };
  const sign = direction === 'ascending' ? 1 : -1;
  return processes
    .filter(
      (process) =>
        needle === '' ||
        process.name.toLocaleLowerCase(locale).includes(needle) ||
        process.user.toLocaleLowerCase(locale).includes(needle),
    )
    .sort((a, b) => sign * compare(a, b) || a.pid - b.pid);
}

/**
 * The rows of a list with fixed row height that a viewport of `height` pixels scrolled to `scrollTop` shows, with a
 * margin of `overscan` rows on both sides; `last` is exclusive.
 */
export function visibleRows(
  count: number,
  rowHeight: number,
  scrollTop: number,
  height: number,
  overscan = 10,
): { first: number; last: number } {
  const first = Math.max(0, Math.floor(scrollTop / rowHeight) - overscan);
  const last = Math.min(count, Math.ceil((scrollTop + height) / rowHeight) + overscan);
  return { first, last: Math.max(first, last) };
}
