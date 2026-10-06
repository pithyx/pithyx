import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useEffect, useId, useRef, useState, type KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import {
  Button,
  IconButton,
  Menu,
  MenuItem,
  MenuPopover,
  MenuTrigger,
  SearchField,
} from '@pithyx/ui';
import { IconDownload, IconFilter, IconRefresh, IconSettings } from '../../ui/icons';
import { useAppName } from '../names';
import type { SdkAppProps } from '../sdk/CoreSdkApp';
import { describeProblem, logsCall, type LogEntry } from '../sdk/logs';
import {
  DEFAULT_VIEW,
  describeSource,
  filterOf,
  groupSources,
  LEVELS,
  RANGES,
  toggleLevel,
  type LogView,
} from './filters';
import styles from './Logs.module.css';
import { LogSettingsDialog } from './LogSettingsDialog';
import { useFormatLocale } from '../../i18n';

const PAGE = 200;
const ROW_HEIGHT = 32;
const SEARCH_DELAY = 300;

/** The time a filter is relative to; taken when the user changes a filter or refreshes. */
function currentTime() {
  return Date.now();
}

/**
 * The Log Center: the logs of the daemon, the system services and the apps, from the system disk and the archive
 * together, filtered by level, source, time range and text, with export and retention settings. Administrators only.
 */
export function LogsApp({ app }: SdkAppProps) {
  const { t } = useTranslation('logs');
  const [view, setView] = useState<LogView>(DEFAULT_VIEW);
  const [now, setNow] = useState(() => Date.now());
  const [searchText, setSearchText] = useState('');
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const sourceName = useSourceName();
  const filter = filterOf(view, now);

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
    queryKey: ['logs', 'entries', filter],
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
  const entries = pages.data?.pages.flatMap((page) => page.entries) ?? [];
  const selected = entries.find((entry) => keyOf(entry) === selectedId) ?? null;

  const change = (next: Partial<LogView>) => {
    setView((current) => ({ ...current, ...next }));
    setNow(currentTime());
  };
  const groups = groupSources(sources.data ?? []);
  const allSources = [...groups.daemon, ...groups.system, ...groups.app];

  return (
    <div className={styles.app}>
      <div className={styles.toolbar}>
        <SearchField
          label={t('search.label')}
          placeholder={t('search.placeholder')}
          className={styles.search}
          value={searchText}
          onChange={setSearchText}
        />
        <div className={styles.levels} role="group" aria-label={t('levels.label')}>
          {LEVELS.map((level) => (
            <button
              key={level}
              type="button"
              className={styles.segment}
              data-level={level}
              aria-pressed={view.levels.includes(level)}
              onClick={() => {
                change({ levels: toggleLevel(view.levels, level) });
              }}
            >
              {t(`levels.${level}`)}
            </button>
          ))}
        </div>
        <label className={styles.rangeField}>
          <span className="visually-hidden">{t('range.label')}</span>
          <select
            className={styles.select}
            value={view.range}
            onChange={(event) => {
              change({ range: event.target.value as LogView['range'] });
            }}
          >
            {RANGES.map((range) => (
              <option key={range} value={range}>
                {t(`range.${range}`)}
              </option>
            ))}
          </select>
        </label>
        <MenuTrigger>
          <Button variant="secondary" size="sm">
            <IconFilter size={16} stroke={1.75} aria-hidden />
            {view.sources.length === 0
              ? t('sources.all')
              : t('sources.some', { count: view.sources.length })}
          </Button>
          <MenuPopover placement="bottom end">
            <Menu
              aria-label={t('sources.label')}
              selectionMode="multiple"
              selectedKeys={new Set(view.sources)}
              onSelectionChange={(keys) => {
                change({ sources: keys === 'all' ? [] : [...keys].map(String) });
              }}
              renderEmptyState={() => <p className={styles.menuEmpty}>{t('sources.none')}</p>}
            >
              {allSources.map((source) => (
                <MenuItem key={source} id={source} textValue={sourceName(source)}>
                  {sourceName(source)}
                </MenuItem>
              ))}
            </Menu>
          </MenuPopover>
        </MenuTrigger>
        <div className={styles.actions}>
          <IconButton
            label={t('refresh')}
            size="sm"
            onPress={() => {
              setNow(Date.now());
            }}
          >
            <IconRefresh size={16} stroke={1.75} aria-hidden />
          </IconButton>
          <MenuTrigger>
            <IconButton label={t('export.label')} size="sm" tooltip={false}>
              <IconDownload size={16} stroke={1.75} aria-hidden />
            </IconButton>
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
          <IconButton
            label={t('settings.open')}
            size="sm"
            onPress={() => {
              setSettingsOpen(true);
            }}
          >
            <IconSettings size={16} stroke={1.75} aria-hidden />
          </IconButton>
        </div>
      </div>
      {pages.isError ? (
        <div className={styles.notice}>
          <ErrorNotice error={describeProblem(pages.error)} />
        </div>
      ) : (
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
      )}
      {settingsOpen && (
        <LogSettingsDialog
          app={app}
          onClose={() => {
            setSettingsOpen(false);
          }}
        />
      )}
    </div>
  );
}

function keyOf(entry: LogEntry): string {
  return `${entry.archived ? 'a' : 'c'}${String(entry.id)}`;
}

/** Readable source names: "Pithyx daemon", "System: kernel", or the app's name. */
function useSourceName(): (source: string) => string {
  const { t } = useTranslation('logs');
  const appName = useAppName();
  return (source) => {
    const { group, name } = describeSource(source);
    if (group === 'daemon') {
      return t('sources.daemon');
    }
    if (group === 'system') {
      return t('sources.system', { name });
    }
    const translated = appName(name);
    return t('sources.app', { name: translated === `apps.${name}` ? name : translated });
  };
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
  // The compiler skips this component; the virtualizer's functions are only used here.
  // eslint-disable-next-line react-hooks/incompatible-library
  const virtualizer = useVirtualizer({
    count: entries.length,
    getScrollElement: () => scroll.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 20,
  });
  const items = virtualizer.getVirtualItems();
  const lastVisible = items.at(-1)?.index ?? 0;

  // Older pages follow the scroll position: syncing with the virtualizer, not deriving state.
  useEffect(() => {
    if (hasMore && !loading && lastVisible >= entries.length - 40) {
      loadMore();
    }
  }, [hasMore, loading, loadMore, lastVisible, entries.length]);

  const time = new Intl.DateTimeFormat(locale, {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  });
  const activeIndex = Math.min(active, Math.max(0, entries.length - 1));
  const activeEntry = entries[activeIndex];

  function moveTo(index: number) {
    const next = Math.max(0, Math.min(entries.length - 1, index));
    setActive(next);
    virtualizer.scrollToIndex(next, { align: 'auto' });
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
  return (
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
          const entry = entries[item.index];
          if (entry === undefined) {
            return null;
          }
          const key = keyOf(entry);
          return (
            <div
              key={key}
              id={`${listId}-${key}`}
              role="option"
              aria-selected={key === selectedId}
              className={styles.row}
              data-level={entry.level}
              data-active={item.index === activeIndex ? true : undefined}
              style={{ transform: `translateY(${String(item.start)}px)` }}
              onClick={() => {
                setActive(item.index);
                onSelect(key);
              }}
            >
              <span className={styles.time}>{time.format(new Date(entry.time))}</span>
              <span className={styles.level}>{t(`levels.${entry.level}`)}</span>
              <span className={styles.source}>{sourceName(entry.source)}</span>
              <span className={styles.message}>{entry.message}</span>
            </div>
          );
        })}
      </div>
    </div>
  );
}

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
    [t('details.level'), t(`levels.${entry.level}`)],
    [t('details.source'), sourceName(entry.source)],
    ...(entry.user === null ? [] : [[t('details.user'), entry.user] as [string, string]]),
    ...(entry.requestId === null
      ? []
      : [[t('details.requestId'), entry.requestId] as [string, string]]),
    ...(entry.archived ? [[t('details.stored'), t('details.archive')] as [string, string]] : []),
  ];
  return (
    <aside className={styles.details} aria-label={t('details.label')}>
      <p className={styles.detailMessage}>{entry.message}</p>
      <dl className={styles.fields}>
        {[...fields, ...Object.entries(entry.data ?? {})].map(([name, value]) => (
          <div key={name} className={styles.field}>
            <dt>{name}</dt>
            <dd>{value}</dd>
          </div>
        ))}
      </dl>
    </aside>
  );
}
