import { currentAppId } from '../ids';
import type { LogFilter, LogLevelName } from '../sdk/logs';

export const LEVELS: readonly LogLevelName[] = ['debug', 'info', 'warn', 'error'];
export const RANGES = ['hour', 'day', 'week', 'month', 'all'] as const;
export type Range = (typeof RANGES)[number];

const RANGE_MS: Record<Exclude<Range, 'all'>, number> = {
  hour: 3_600_000,
  day: 86_400_000,
  week: 7 * 86_400_000,
  month: 30 * 86_400_000,
};

/** What the toolbar shows; the daemon query is derived from it. */
export interface LogView {
  levels: readonly LogLevelName[];
  sources: readonly string[];
  range: Range;
  search: string;
}

/** Everything except debug, the last day, every source. */
export const DEFAULT_VIEW: LogView = {
  levels: ['info', 'warn', 'error'],
  sources: [],
  range: 'day',
  search: '',
};

/** The daemon filter for a view at `now`; all levels or no source chosen means no filter. */
export function filterOf(view: LogView, now: number): LogFilter {
  const filter: LogFilter = {};
  if (view.range !== 'all') {
    filter.from = new Date(now - RANGE_MS[view.range]).toISOString();
  }
  if (view.levels.length > 0 && view.levels.length < LEVELS.length) {
    filter.level = [...view.levels];
  }
  if (view.sources.length > 0) {
    filter.source = [...view.sources];
  }
  const search = view.search.trim();
  if (search !== '') {
    filter.search = search;
  }
  return filter;
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

/** Sources by group, the daemon first, then system services, then apps, each by name. */
export function groupSources(sources: readonly string[]): Record<SourceGroup, string[]> {
  const groups: Record<SourceGroup, string[]> = { daemon: [], system: [], app: [] };
  for (const source of [...sources].sort()) {
    groups[describeSource(source).group].push(source);
  }
  return groups;
}

/** Turns a level on or off; at least one level stays on. */
export function toggleLevel(levels: readonly LogLevelName[], level: LogLevelName): LogLevelName[] {
  const next = levels.includes(level)
    ? levels.filter((item) => item !== level)
    : [...levels, level];
  return next.length === 0 ? [...levels] : LEVELS.filter((item) => next.includes(item));
}
