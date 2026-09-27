import { useInfiniteQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import type {
  FileLocation,
  FolderChange,
  FolderPage,
  SortKey,
  SortOrder,
  PithyxApp,
} from '@pithyx/sdk';
import { useEffect, useEffectEvent } from 'react';
import { applyChange, loadedEntries, type FolderPages } from './pages';

/** Entries per page. Large enough that most folders arrive in one call, small enough for a quick first page. */
export const PAGE_SIZE = 200;

export function folderKey(share: string, path: string) {
  return ['files', share, 'folder', path] as const;
}

function pagesKey(share: string, path: string, sort: SortKey, order: SortOrder) {
  return [...folderKey(share, path), sort, order] as const;
}

/** Applies a change to every cached order of a folder, so switching the sort column shows it too. */
export function changeFolder(
  client: QueryClient,
  share: string,
  path: string,
  change: Omit<FolderChange, 'watch' | 'resync' | 'gone'>,
): void {
  const full: FolderChange = { ...change, watch: '', resync: false, gone: false };
  for (const [key, data] of client.getQueriesData<FolderPages>({
    queryKey: folderKey(share, path),
  })) {
    if (data !== undefined) {
      const [, , , , sort, order] = key as ReturnType<typeof pagesKey>;
      client.setQueryData(key, applyChange(data, full, sort, order));
    }
  }
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
      const pages = data.pages.map((page) => ({
        ...page,
        total: kept.size,
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
  handlers: { onRemoved: (names: string[]) => void; onGone: () => void },
) {
  const client = useQueryClient();
  const location: FileLocation = { share: share ?? '' };
  const query = useInfiniteQuery<
    FolderPage,
    Error,
    FolderPages,
    ReturnType<typeof pagesKey>,
    string | null
  >({
    queryKey: pagesKey(share ?? '', path, sort, order),
    queryFn: ({ pageParam }) =>
      app.files.list(location, path, {
        limit: PAGE_SIZE,
        sort,
        order,
        ...(pageParam === null ? {} : { cursor: pageParam }),
      }),
    initialPageParam: null as string | null,
    getNextPageParam: (page) => page.nextCursor,
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
