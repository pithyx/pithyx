import {
  useInfiniteQuery,
  useQueryClient,
  type Query,
  type QueryClient,
} from '@tanstack/react-query';
import type { FolderChange, FolderPage, SortKey, SortOrder, PithyxApp } from '@pithyx/sdk';
import { useEffect, useEffectEvent } from 'react';
import { applyChange, loadedEntries, type FolderPages } from './pages';

/** Entries per page. Large enough that most folders arrive in one call, small enough for a quick first page. */
export const PAGE_SIZE = 200;

export function folderKey(share: string, path: string) {
  return ['files', share, 'folder', path] as const;
}

function pagesKey(share: string, path: string, sort: SortKey, order: SortOrder, query: string) {
  return [...folderKey(share, path), sort, order, query] as const;
}

/** Whether a name is kept by a search, as the box decides it: the name contains the query, ignoring case. */
export function matchesQuery(name: string, query: string): boolean {
  const wanted = query.trim().toLowerCase();
  return wanted === '' || name.toLowerCase().includes(wanted);
}

/** A prefetched folder younger than this is not read again when the pointer rests on it once more. */
const PREFETCH_FRESH = 30_000;

function listPage(
  app: PithyxApp,
  share: string,
  path: string,
  sort: SortKey,
  order: SortOrder,
  query: string,
  cursor: string | null,
): Promise<FolderPage> {
  return app.files.list({ share }, path, {
    limit: PAGE_SIZE,
    sort,
    order,
    ...(cursor === null ? {} : { cursor }),
    ...(query.trim() === '' ? {} : { query: query.trim() }),
  });
}

/**
 * Reads the first page of a folder ahead, when the pointer rests on it, so opening it shows the cached rows at once
 * and revalidates them as for a visited folder.
 */
export function prefetchFolder(
  client: QueryClient,
  app: PithyxApp,
  share: string,
  path: string,
  sort: SortKey,
  order: SortOrder,
): void {
  // A failed read ahead changes nothing: opening the folder reads it again and shows the error there.
  client
    .infiniteQuery({
      queryKey: pagesKey(share, path, sort, order, ''),
      queryFn: ({ pageParam }) => listPage(app, share, path, sort, order, '', pageParam),
      initialPageParam: null as string | null,
      staleTime: PREFETCH_FRESH,
    })
    .catch(() => undefined);
}

/**
 * Applies a change to every cached order and search of a folder, so switching the sort column shows it too. A search
 * takes in only the entries whose name matches it. A removed name past the loaded pages cannot be counted, so such a
 * cache is read again (a deletion within a search often removes names the folder's own list has not loaded yet).
 */
export function changeFolder(
  client: QueryClient,
  share: string,
  path: string,
  change: Omit<FolderChange, 'watch' | 'resync' | 'gone'>,
): void {
  const full: FolderChange = { ...change, watch: '', resync: false, gone: false };
  // A first page still loading may have been read before this change (a folder created right after Files opened):
  // read it again, so the change is not lost when it arrives. Invalidating would only wait for the old read.
  const loading = {
    queryKey: folderKey(share, path),
    predicate: (query: Query) => query.state.data === undefined,
  };
  void client.cancelQueries(loading).then(() => client.refetchQueries(loading));
  for (const [key, data] of client.getQueriesData<FolderPages>({
    queryKey: folderKey(share, path),
  })) {
    if (data !== undefined) {
      const [, , , , sort, order, query] = key as ReturnType<typeof pagesKey>;
      const kept =
        query === ''
          ? full
          : { ...full, changed: full.changed.filter((entry) => matchesQuery(entry.name, query)) };
      client.setQueryData(key, applyChange(data, kept, sort, order));
      if (removesUnloaded(data, kept, query)) {
        void client.invalidateQueries({ queryKey: key, exact: true });
      }
    }
  }
}

function removesUnloaded(data: FolderPages, change: FolderChange, query: string): boolean {
  if (data.pages.at(-1)?.nextCursor === null) {
    return false;
  }
  const loaded = new Set(loadedEntries(data).map((entry) => entry.name));
  return change.removed.some((name) => matchesQuery(name, query) && !loaded.has(name));
}

/**
 * Everything in a folder but some names is gone: what a job on a whole folder shows at once, until the folder is
 * listed again.
 */
export function keepOnly(
  client: QueryClient,
  share: string,
  path: string,
  names: readonly string[],
): void {
  const kept = new Set(names);
  for (const [key, data] of client.getQueriesData<FolderPages>({
    queryKey: folderKey(share, path),
  })) {
    if (data !== undefined) {
      const [, , , , , , query] = key as ReturnType<typeof pagesKey>;
      const total = [...kept].filter((name) => matchesQuery(name, query)).length;
      const pages = data.pages.map((page) => ({
        ...page,
        total,
        entries: page.entries.filter((entry) => kept.has(entry.name)),
      }));
      client.setQueryData(key, { ...data, pages });
    }
  }
}

/**
 * One folder: its pages from the cache first (a visited folder shows at once and is revalidated in the background),
 * further pages as the view scrolls, and live changes while it is on screen.
 */
export function useFolder(
  app: PithyxApp,
  share: string | null,
  path: string,
  sort: SortKey,
  order: SortOrder,
  search: string,
  handlers: { onRemoved: (names: string[]) => void; onGone: () => void },
) {
  const client = useQueryClient();
  const query = useInfiniteQuery<
    FolderPage,
    Error,
    FolderPages,
    ReturnType<typeof pagesKey>,
    string | null
  >({
    queryKey: pagesKey(share ?? '', path, sort, order, search.trim()),
    queryFn: ({ pageParam }) =>
      listPage(app, share ?? '', path, sort, order, search.trim(), pageParam),
    initialPageParam: null as string | null,
    getNextPageParam: (page) => page.nextCursor,
    // While a search in this folder changes, the rows of the last one stay until the new ones arrive.
    placeholderData: (previous, previousQuery) =>
      previousQuery?.queryKey[1] === share && previousQuery.queryKey[3] === path
        ? previous
        : undefined,
    enabled: share !== null,
  });

  const onChange = useEffectEvent((change: FolderChange) => {
    if (share === null) {
      return;
    }
    if (change.gone) {
      handlers.onGone();
      return;
    }
    if (change.resync) {
      void client.invalidateQueries({ queryKey: folderKey(share, path) });
      return;
    }
    changeFolder(client, share, path, change);
    if (change.removed.length > 0) {
      handlers.onRemoved(change.removed);
    }
  });

  // A watch follows the folder on screen: synchronization with the daemon's inotify topic, not data fetching.
  useEffect(() => {
    if (share === null) {
      return;
    }
    let watch: string | null = null;
    let active = true;
    const stop = app.on('folderChanged', (change) => {
      if (change.watch === watch) {
        onChange(change);
      }
    });
    app.files.watch({ share }, path).then(
      (id) => {
        if (active) {
          watch = id;
        } else {
          void app.files.unwatch(id);
        }
      },
      () => {
        // Without live changes the folder still works; the next visit revalidates it.
      },
    );
    return () => {
      active = false;
      stop();
      if (watch !== null) {
        void app.files.unwatch(watch);
      }
    };
  }, [app, share, path]);

  const pages = query.data;
  return {
    entries: loadedEntries(pages),
    total: pages?.pages[0]?.total ?? 0,
    loading: query.isPending || query.isFetchingNextPage,
    error: query.error,
    hasMore: query.hasNextPage,
    loadMore: () => {
      void query.fetchNextPage();
    },
    refetch: () => {
      void query.refetch();
    },
  };
}

/**
 * The subfolders of a folder for the tree in the navigation column, from the first pages of its name order: the same
 * cache as the folder itself, so a folder seen in the list shows its subfolders at once, and the other way round.
 */
export function useSubfolders(app: PithyxApp, share: string, path: string, enabled: boolean) {
  const query = useInfiniteQuery<
    FolderPage,
    Error,
    FolderPages,
    ReturnType<typeof pagesKey>,
    string | null
  >({
    queryKey: pagesKey(share, path, 'name', 'asc', ''),
    queryFn: ({ pageParam }) => listPage(app, share, path, 'name', 'asc', '', pageParam),
    initialPageParam: null as string | null,
    getNextPageParam: (page) => page.nextCursor,
    staleTime: PREFETCH_FRESH,
    enabled,
  });
  return {
    folders: loadedEntries(query.data).filter((entry) => entry.kind === 'folder'),
    loaded: query.data !== undefined,
    more: query.hasNextPage,
  };
}
