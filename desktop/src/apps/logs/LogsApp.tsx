import {
  useInfiniteQuery,
  useQuery,
  useQueryClient,
  type InfiniteData,
} from '@tanstack/react-query';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useEffect, useId, useRef, useState, type KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import {
  AppFrame,
  Button,
  Menu,
  MenuItem,
  MenuPopover,
  MenuTrigger,
  NavList,
  SearchField,
  Select,
  StatusBadge,
  Toolbar,
  ToolbarSeparator,
  type NavGroup,
} from '@pithyx/ui';
import {
  IconApps,
  IconDownload,
  IconListDetails,
  IconPlayerPause,
  IconPlayerPlay,
  IconServer2,
  IconSettings,
} from '../../ui/icons';
import { useAppName } from '../names';
import type { SdkAppProps } from '../sdk/CoreSdkApp';
import {
  describeProblem,
  logsCall,
  onLogs,
  type LogCount,
  type LogEntry,
  type LogPage,
} from '../sdk/logs';
import {
  addCounts,
  countOf,
  dayOf,
  DEFAULT_VIEW,
  describeSource,
  filterOf,
  groupSources,
  LEVEL_CHOICES,
  matches,
  RANGES,
  rangeStart,
  type LogView,
} from './filters';
import styles from './Logs.module.css';
import { LogSettingsDialog } from './LogSettingsDialog';
import { useFormatLocale } from '../../i18n';

const PAGE = 200;
const ROW_HEIGHT = 32;
const DAY_HEIGHT = 30;
const SEARCH_DELAY = 300;
const ALL = 'all';

// Request and trace ids stay out of the UI, also when an app put one into its fields.
const HIDDEN_FIELD = /^(request|trace|span)[_-]?id$/i;

const levelTones = { debug: 'neutral', info: 'info', warn: 'warning', error: 'danger' } as const;

/**
 * The Log Center (D394): the sources in the navigation with their warnings and errors, a toolbar with the level, the
 * time range, the search, live or paused, export and retention, and the entries grouped by day with the chosen one's
 * fields on the right. Entries of the daemon, the system services and the apps come from the system disk and the
 * archive together; while live, new ones arrive with the topic `system/logs`. Administrators only.
 */
export function LogsApp({ app }: SdkAppProps) {
  const { t } = useTranslation('logs');
  const queryClient = useQueryClient();
  const locale = useFormatLocale();
  const [view, setView] = useState<LogView>(DEFAULT_VIEW);
  const [now, setNow] = useState(() => Date.now());
  const [searchText, setSearchText] = useState('');
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [live, setLive] = useState(true);
  const [frameView, setFrameView] = useState<'navigation' | 'page'>('page');
  const sourceName = useSourceName();
  const filter = filterOf(view, now);
  const entriesKey = ['logs', 'entries', filter] as const;
  const from = rangeStart(view.range, now);
  const countsKey = ['logs', 'counts', from ?? ALL] as const;

  // Typing filters after a short pause, so every key does not ask the daemon: syncing with a timer.
  useEffect(() => {
    const timer = setTimeout(() => {
      setView((current) =>
        current.search === searchText ? current : { ...current, search: searchText },
      );
    }, SEARCH_DELAY);
    return () => {
      clearTimeout(timer);
    };
  }, [searchText]);

  const pages = useInfiniteQuery({
    queryKey: entriesKey,
    queryFn: ({ pageParam }) =>
      logsCall(app, 'internal.admin.logs.query', {
        ...filter,
        limit: PAGE,
        ...(pageParam === undefined ? {} : { cursor: pageParam }),
      }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
  const sources = useQuery({
    queryKey: ['logs', 'sources', now],
    queryFn: () => logsCall(app, 'internal.admin.logs.sources', {}),
  });
  const counts = useQuery({
    queryKey: countsKey,
    // All time counts from the start of the current logs.
    queryFn: () =>
      logsCall(app, 'internal.admin.logs.counts', { from: from ?? new Date(0).toISOString() }),
  });
  const settings = useQuery({
    queryKey: ['logs', 'settings'],
    queryFn: () => logsCall(app, 'internal.admin.logs.settings', {}),
  });

  // While live, the window follows `system/logs`: an external source, so this is syncing, not deriving state. The
  // events carry the entries; asking the daemon again would log the request and feed itself.
  const viewKey = JSON.stringify([view, now]);
  useEffect(() => {
    if (!live) {
      return undefined;
    }
    const [current, at] = JSON.parse(viewKey) as [LogView, number];
    const key = ['logs', 'entries', filterOf(current, at)];
    const countKey = ['logs', 'counts', rangeStart(current.range, at) ?? ALL];
    void logsCall(app, 'internal.admin.logs.watch', { watch: true });
    const stop = onLogs(app, (added) => {
      if (added.truncated) {
        void queryClient.invalidateQueries({ queryKey: key, exact: true });
        void queryClient.invalidateQueries({ queryKey: countKey, exact: true });
        return;
      }
      if (added.entries.length === 0) {
        return;
      }
      queryClient.setQueryData<LogCount[]>(countKey, (previous) =>
        previous === undefined ? previous : addCounts(previous, added.entries),
      );
      const fresh = added.entries.filter((entry) => matches(current, entry)).reverse();
      if (fresh.length > 0) {
        queryClient.setQueryData<InfiniteData<LogPage, string | undefined>>(key, (previous) =>
          previous === undefined ? previous : prepend(previous, fresh),
        );
      }
    });
    return () => {
      stop();
      void logsCall(app, 'internal.admin.logs.watch', { watch: false });
    };
  }, [app, live, viewKey, queryClient]);

  const entries = pages.data?.pages.flatMap((page) => page.entries) ?? [];
  const selected = entries.find((entry) => keyOf(entry) === selectedId) ?? null;
  const allCounts = counts.data ?? [];
  const shown = countOf(allCounts, view.source);

  const change = (next: Partial<LogView>) => {
    setView((current) => ({ ...current, ...next }));
    setNow(Date.now());
    setSelectedId(null);
  };
  const groups = groupSources(sources.data ?? []);
  const item = (source: string) => {
    const { warnings, errors } = countOf(allCounts, source);
    return {
      id: source,
      label: sourceName(source),
      icon:
        describeSource(source).group === 'app' ? (
          <IconApps size={18} stroke={1.75} aria-hidden />
        ) : (
          <IconServer2 size={18} stroke={1.75} aria-hidden />
        ),
      count: warnings + errors,
      countLabel: t('counts.problems', { count: warnings + errors }),
    };
  };
  const navGroups: NavGroup[] = [
    {
      id: 'start',
      items: [
        {
          id: ALL,
          label: t('sources.all'),
          icon: <IconListDetails size={18} stroke={1.75} aria-hidden />,
        },
      ],
    },
    {
      id: 'system',
      label: t('groups.system'),
      items: [...groups.daemon, ...groups.system].map(item),
    },
    ...(groups.app.length === 0
      ? []
      : [{ id: 'apps', label: t('groups.apps'), items: groups.app.map(item) }]),
  ];
  const levelCount = (choice: (typeof LEVEL_CHOICES)[number]) =>
    choice === 'warnings' ? shown.warnings : choice === 'errors' ? shown.errors : 0;

  return (
    <>
      <AppFrame
        className={styles.frame}
        scroll="content"
        view={frameView}
        onViewChange={setFrameView}
        navigation={
          <NavList
            aria-label={t('navigation')}
            searchPlaceholder={t('sources.search')}
            groups={navGroups}
            selectedId={view.source ?? ALL}
            onSelect={(id) => {
              change({ source: id === ALL ? null : id });
              setFrameView('page');
            }}
          />
        }
        title={view.source === null ? t('sources.all') : sourceName(view.source)}
        toolbar={
          <Toolbar
            aria-label={t('toolbar')}
            end={
              <SearchField
                label={t('search.label')}
                placeholder={t('search.placeholder')}
                className={styles.search}
                value={searchText}
                onChange={setSearchText}
              />
            }
          >
            <div className={styles.levels} role="group" aria-label={t('levels.label')}>
              {LEVEL_CHOICES.map((choice) => (
                <button
                  key={choice}
                  type="button"
                  className={styles.segment}
                  data-choice={choice}
                  aria-pressed={view.level === choice}
                  onClick={() => {
                    change({ level: choice });
                  }}
                >
                  {t(`levels.${choice}`)}
                  {levelCount(choice) > 0 && (
                    <span className={styles.segmentCount}>
                      {new Intl.NumberFormat(locale).format(levelCount(choice))}
                    </span>
                  )}
                </button>
              ))}
            </div>
            <Select
              aria-label={t('range.label')}
              className={styles.range}
              options={RANGES.map((range) => ({ id: range, label: t(`range.${range}`) }))}
              value={view.range}
              onChange={(value) => {
                const range = RANGES.find((entry) => entry === value);
                if (range !== undefined) {
                  change({ range });
                }
              }}
            />
            <ToolbarSeparator />
            <Button
              onPress={() => {
                if (!live) {
                  // Back to live: read what came while paused, then follow.
                  setNow(Date.now());
                }
                setLive(!live);
              }}
            >
              {live ? (
                <IconPlayerPause size={18} stroke={1.75} aria-hidden />
              ) : (
                <IconPlayerPlay size={18} stroke={1.75} aria-hidden />
              )}
              {live ? t('live.pause') : t('live.resume')}
            </Button>
            <MenuTrigger>
              <Button>
                <IconDownload size={18} stroke={1.75} aria-hidden />
                {t('export.label')}
              </Button>
              <MenuPopover placement="bottom end">
                <Menu
                  aria-label={t('export.label')}
                  onAction={(format) => {
                    void logsCall(app, 'internal.admin.logs.export', {
                      ...filter,
                      format: format === 'text' ? 'text' : 'jsonl',
                    });
                  }}
                >
                  <MenuItem id="text">{t('export.text')}</MenuItem>
                  <MenuItem id="jsonl">{t('export.jsonl')}</MenuItem>
                </Menu>
              </MenuPopover>
            </MenuTrigger>
            <Button
              onPress={() => {
                setSettingsOpen(true);
              }}
            >
              <IconSettings size={18} stroke={1.75} aria-hidden />
              {t('settings.open')}
            </Button>
          </Toolbar>
        }
        statusBar={
          <div className={styles.status}>
            <span>{t('status.entries', { count: entries.length })}</span>
            <span>{live ? t('status.live') : t('status.paused')}</span>
            {settings.data !== undefined && (
              <span className={styles.statusEnd}>
                {t('status.kept', { count: settings.data.settings.retentionDays })}
              </span>
            )}
          </div>
        }
      >
        {pages.isError ? (
          <div className={styles.notice}>
            <ErrorNotice error={describeProblem(pages.error)} />
          </div>
        ) : (
          <div className={styles.bodyFrame}>
            <div className={styles.body}>
              <EntryList
                entries={entries}
                loading={pages.isPending || pages.isFetchingNextPage}
                hasMore={pages.hasNextPage}
                loadMore={() => {
                  void pages.fetchNextPage();
                }}
                selectedId={selectedId}
                onSelect={setSelectedId}
                sourceName={sourceName}
              />
              <EntryDetails entry={selected} sourceName={sourceName} />
            </div>
          </div>
        )}
      </AppFrame>
      {settingsOpen && (
        <LogSettingsDialog
          app={app}
          onClose={() => {
            setSettingsOpen(false);
            void queryClient.invalidateQueries({ queryKey: ['logs', 'settings'] });
          }}
        />
      )}
    </>
  );
}

/** Live entries, newest first, before the first page, without the ones it already has. */
function prepend(
  data: InfiniteData<LogPage, string | undefined>,
  fresh: readonly LogEntry[],
): InfiniteData<LogPage, string | undefined> {
  const [first, ...rest] = data.pages;
  if (first === undefined) {
    return data;
  }
  const known = new Set(first.entries.map(keyOf));
  const added = fresh.filter((entry) => !known.has(keyOf(entry)));
  return { ...data, pages: [{ ...first, entries: [...added, ...first.entries] }, ...rest] };
}

function keyOf(entry: LogEntry): string {
  return `${entry.archived ? 'a' : 'c'}${String(entry.id)}`;
}

/** Readable source names: "Pithyx daemon", "Audit log" (D311), the system service, or the app's name. */
function useSourceName(): (source: string) => string {
  const { t } = useTranslation('logs');
  const appName = useAppName();
  return (source) => {
    const { group, name } = describeSource(source);
    if (source === 'audit') {
      return t('sources.audit');
    }
    if (source.startsWith('proxy/')) {
      return t('sources.proxy', { host: source.slice('proxy/'.length) });
    }
    if (group === 'daemon') {
      return source === 'daemon' ? t('sources.daemon') : source;
    }
    if (group === 'system') {
      return name;
    }
    const translated = appName(name);
    return translated === `apps.${name}` ? name : translated;
  };
}

type Row = { kind: 'day'; day: string; time: string } | { kind: 'entry'; entry: LogEntry };

/** The entries with a heading before each day, newest first. */
function rowsOf(entries: readonly LogEntry[]): Row[] {
  const rows: Row[] = [];
  let day = '';
  for (const entry of entries) {
    const next = dayOf(entry.time);
    if (next !== day) {
      day = next;
      rows.push({ kind: 'day', day, time: entry.time });
    }
    rows.push({ kind: 'entry', entry });
  }
  return rows;
}

function EntryList({
  entries,
  loading,
  hasMore,
  loadMore,
  selectedId,
  onSelect,
  sourceName,
}: {
  entries: LogEntry[];
  loading: boolean;
  hasMore: boolean;
  loadMore: () => void;
  selectedId: string | null;
  onSelect: (id: string) => void;
  sourceName: (source: string) => string;
}) {
  const { t } = useTranslation('logs');
  const locale = useFormatLocale();
  const scroll = useRef<HTMLDivElement>(null);
  const listId = useId();
  const [active, setActive] = useState(0);
  const rows = rowsOf(entries);
  const rowOfEntry: number[] = [];
  rows.forEach((row, index) => {
    if (row.kind === 'entry') {
      rowOfEntry.push(index);
    }
  });
  // The compiler skips this component; the virtualizer's functions are only used here.
  // eslint-disable-next-line react-hooks/incompatible-library
  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scroll.current,
    estimateSize: (index) => (rows[index]?.kind === 'day' ? DAY_HEIGHT : ROW_HEIGHT),
    overscan: 20,
  });
  const items = virtualizer.getVirtualItems();
  const lastVisible = items.at(-1)?.index ?? 0;

  // Older pages follow the scroll position: syncing with the virtualizer, not deriving state.
  useEffect(() => {
    if (hasMore && !loading && lastVisible >= rows.length - 40) {
      loadMore();
    }
  }, [hasMore, loading, loadMore, lastVisible, rows.length]);

  const time = new Intl.DateTimeFormat(locale, {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  });
  const date = new Intl.DateTimeFormat(locale, { dateStyle: 'full' });
  const today = dayOf(new Date().toISOString());
  const yesterday = dayOf(new Date(Date.now() - 86_400_000).toISOString());
  const dayLabel = (row: Extract<Row, { kind: 'day' }>) => {
    const text = date.format(new Date(row.time));
    return row.day === today
      ? t('day.today', { date: text })
      : row.day === yesterday
        ? t('day.yesterday', { date: text })
        : text;
  };
  const activeIndex = Math.min(active, Math.max(0, entries.length - 1));
  const activeEntry = entries[activeIndex];

  function moveTo(index: number) {
    const next = Math.max(0, Math.min(entries.length - 1, index));
    setActive(next);
    virtualizer.scrollToIndex(rowOfEntry[next] ?? 0, { align: 'auto' });
    const entry = entries[next];
    if (entry !== undefined) {
      onSelect(keyOf(entry));
    }
  }

  function onKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    const page = Math.max(1, Math.floor((scroll.current?.clientHeight ?? 320) / ROW_HEIGHT) - 1);
    const moves: Record<string, number> = {
      ArrowDown: activeIndex + 1,
      ArrowUp: activeIndex - 1,
      PageDown: activeIndex + page,
      PageUp: activeIndex - page,
      Home: 0,
      End: entries.length - 1,
    };
    const target = moves[event.key];
    if (target !== undefined) {
      event.preventDefault();
      moveTo(target);
    }
  }

  if (entries.length === 0) {
    return (
      <div className={styles.list} role="status">
        <p className={styles.empty}>{loading ? t('loading') : t('empty')}</p>
      </div>
    );
  }
  let entryIndex = -1;
  const indexOfRow = new Map<number, number>();
  rows.forEach((row, index) => {
    if (row.kind === 'entry') {
      entryIndex += 1;
      indexOfRow.set(index, entryIndex);
    }
  });
  return (
    <div className={styles.listFrame}>
      <div className={styles.head} aria-hidden>
        <span>{t('columns.time')}</span>
        <span>{t('columns.level')}</span>
        <span className={styles.sourceColumn}>{t('columns.source')}</span>
        <span>{t('columns.message')}</span>
      </div>
      <div
        ref={scroll}
        className={styles.list}
        role="listbox"
        tabIndex={0}
        aria-label={t('list')}
        aria-activedescendant={
          activeEntry === undefined ? undefined : `${listId}-${keyOf(activeEntry)}`
        }
        onKeyDown={onKeyDown}
      >
        <div className={styles.rows} style={{ height: virtualizer.getTotalSize() }}>
          {items.map((item) => {
            const row = rows[item.index];
            if (row === undefined) {
              return null;
            }
            if (row.kind === 'day') {
              // The day is also in each entry's name; the heading is for the eyes.
              return (
                <div
                  key={`day-${row.day}`}
                  className={styles.day}
                  role="presentation"
                  aria-hidden
                  style={{ transform: `translateY(${String(item.start)}px)` }}
                >
                  {dayLabel(row)}
                </div>
              );
            }
            const { entry } = row;
            const key = keyOf(entry);
            const index = indexOfRow.get(item.index) ?? 0;
            return (
              <div
                key={key}
                id={`${listId}-${key}`}
                role="option"
                aria-selected={key === selectedId}
                className={styles.row}
                data-active={index === activeIndex ? true : undefined}
                style={{ transform: `translateY(${String(item.start)}px)` }}
                onClick={() => {
                  setActive(index);
                  onSelect(key);
                }}
              >
                <span className={styles.time}>
                  <span className="visually-hidden">{date.format(new Date(entry.time))} </span>
                  {time.format(new Date(entry.time))}
                </span>
                <span className={styles.level}>
                  <StatusBadge tone={levelTones[entry.level]}>
                    {t(`level.${entry.level}`)}
                  </StatusBadge>
                </span>
                <span className={styles.source}>{sourceName(entry.source)}</span>
                <span className={styles.message}>{entry.message}</span>
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );
}

/** The chosen entry's fields. Never the request id: it goes to the console and the logs, not the UI (CLAUDE.md). */
function EntryDetails({
  entry,
  sourceName,
}: {
  entry: LogEntry | null;
  sourceName: (source: string) => string;
}) {
  const { t } = useTranslation('logs');
  const locale = useFormatLocale();
  if (entry === null) {
    return (
      <aside className={styles.details} aria-label={t('details.label')}>
        <p className={styles.muted}>{t('details.none')}</p>
      </aside>
    );
  }
  const time = new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'medium' });
  const fields: [string, string][] = [
    [t('details.time'), time.format(new Date(entry.time))],
    [t('details.level'), t(`level.${entry.level}`)],
    [t('details.source'), sourceName(entry.source)],
    ...(entry.user === null ? [] : [[t('details.user'), entry.user] as [string, string]]),
    ...(entry.archived ? [[t('details.stored'), t('details.archive')] as [string, string]] : []),
  ];
  return (
    <aside className={styles.details} aria-label={t('details.label')}>
      <p className={styles.detailMessage}>{entry.message}</p>
      <dl className={styles.fields}>
        {[
          ...fields,
          ...Object.entries(entry.data ?? {}).filter(([name]) => !HIDDEN_FIELD.test(name)),
        ].map(([name, value]) => (
          <div key={name} className={styles.field}>
            <dt>{name}</dt>
            <dd>{value}</dd>
          </div>
        ))}
      </dl>
    </aside>
  );
}
