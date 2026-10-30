import { currentAppId } from '../ids';
import type { LogCount, LogEntry, LogFilter, LogLevelName } from '../sdk/logs';

export const RANGES = ['hour', 'day', 'week', 'month', 'all'] as const;
export type Range = (typeof RANGES)[number];

/** The level filter of the toolbar (D394): everything but debug, warnings, errors, or every level with debug. */
export const LEVEL_CHOICES = ['all', 'warnings', 'errors', 'debug'] as const;
export type LevelChoice = (typeof LEVEL_CHOICES)[number];

const LEVELS_OF: Record<LevelChoice, readonly LogLevelName[] | null> = {
  all: ['info', 'warn', 'error'],
  warnings: ['warn'],
  errors: ['error'],
  debug: null,
};

const RANGE_MS: Record<Exclude<Range, 'all'>, number> = {
  hour: 3_600_000,
  day: 86_400_000,
  week: 7 * 86_400_000,
  month: 30 * 86_400_000,
};

/** What the navigation and the toolbar show; the daemon query is derived from it. */
export interface LogView {
  level: LevelChoice;
  /** One source, or null for every source. */
  source: string | null;
  range: Range;
  search: string;
}

/** Everything except debug, the last day, every source. */
export const DEFAULT_VIEW: LogView = { level: 'all', source: null, range: 'day', search: '' };

/** Where the range starts at `now`, or undefined for all time. */
export function rangeStart(range: Range, now: number): string | undefined {
  return range === 'all' ? undefined : new Date(now - RANGE_MS[range]).toISOString();
}

/** The daemon filter for a view at `now`; every level or every source means no filter. */
export function filterOf(view: LogView, now: number): LogFilter {
  const filter: LogFilter = {};
  const from = rangeStart(view.range, now);
  if (from !== undefined) {
    filter.from = from;
  }
  const levels = LEVELS_OF[view.level];
  if (levels !== null) {
    filter.level = [...levels];
  }
  if (view.source !== null) {
    filter.source = [view.source];
  }
  const search = view.search.trim();
  if (search !== '') {
    filter.search = search;
  }
  return filter;
}

/**
 * Whether a live entry belongs in a view, as the daemon would decide for a query: its level, its source (a core
 * app's short source of before migration 0022 counts as the app) and the search in its message or fields.
 */
export function matches(view: LogView, entry: LogEntry): boolean {
  const levels = LEVELS_OF[view.level];
  if (levels !== null && !levels.includes(entry.level)) {
    return false;
  }
  if (view.source !== null && currentSource(entry.source) !== currentSource(view.source)) {
    return false;
  }
  const search = view.search.trim().toLowerCase();
  return (
    search === '' ||
    entry.message.toLowerCase().includes(search) ||
    Object.values(entry.data ?? {}).some((value) => value.toLowerCase().includes(search))
  );
}

export type SourceGroup = 'daemon' | 'system' | 'app';

/**
 * Which group a source belongs to and its name within the group. A core app's entries from before migration 0022 keep
 * their short source (`app/files`) and are named after the app's current id.
 */
export function describeSource(source: string): { group: SourceGroup; name: string } {
  if (source.startsWith('system/')) {
    return { group: 'system', name: source.slice('system/'.length) };
  }
  if (source.startsWith('app/')) {
    return { group: 'app', name: currentAppId(source.slice('app/'.length)) };
  }
  return { group: 'daemon', name: source };
}

/** The source a core app writes to now, also for its entries from before migration 0022 (`app/files`). */
export function currentSource(source: string): string {
  const { group, name } = describeSource(source);
  return group === 'app' ? `app/${name}` : source;
}

/** Sources by group, the daemon first, then system services, then apps, each by name. */
export function groupSources(sources: readonly string[]): Record<SourceGroup, string[]> {
  const groups: Record<SourceGroup, string[]> = { daemon: [], system: [], app: [] };
  for (const source of [...sources].sort()) {
    groups[describeSource(source).group].push(source);
  }
  return groups;
}

/** The sources with the ones of live entries added, as the daemon lists them. */
export function addSources(sources: readonly string[], entries: readonly LogEntry[]): string[] {
  const known = new Set(sources);
  const added = entries
    .map((entry) => currentSource(entry.source))
    .filter((source) => !known.has(source));
  return added.length === 0 ? [...sources] : [...new Set([...sources, ...added])].sort();
}

/** Warnings and errors of one source, or of every source for null. */
export function countOf(
  counts: readonly LogCount[],
  source: string | null,
): { warnings: number; errors: number } {
  return counts
    .filter((count) => source === null || count.source === source)
    .reduce(
      (sum, count) => ({
        warnings: sum.warnings + count.warnings,
        errors: sum.errors + count.errors,
      }),
      { warnings: 0, errors: 0 },
    );
}

/** The counts after live entries arrived: each warning and error adds to its source. */
export function addCounts(counts: readonly LogCount[], entries: readonly LogEntry[]): LogCount[] {
  const next = new Map(counts.map((count) => [count.source, { ...count }]));
  for (const entry of entries) {
    if (entry.level !== 'warn' && entry.level !== 'error') {
      continue;
    }
    const source = currentSource(entry.source);
    const count = next.get(source) ?? { source, warnings: 0, errors: 0 };
    next.set(source, {
      ...count,
      warnings: count.warnings + (entry.level === 'warn' ? 1 : 0),
      errors: count.errors + (entry.level === 'error' ? 1 : 0),
    });
  }
  return [...next.values()];
}

/** The local calendar day of an entry, as a key for the day headings of the list. */
export function dayOf(time: string): string {
  const date = new Date(time);
  return `${String(date.getFullYear())}-${String(date.getMonth() + 1)}-${String(date.getDate())}`;
}
