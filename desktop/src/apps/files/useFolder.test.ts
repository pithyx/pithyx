import type { FileEntry, FolderPage } from '@pithyx/sdk';
import { InfiniteQueryObserver, QueryClient } from '@tanstack/react-query';
import { describe, expect, it } from 'vitest';
import type { FolderPages } from './pages';
import { changeFolder, folderKey } from './useFolder';

const created: FileEntry = {
  name: 'New folder',
  kind: 'folder',
  size: 0,
  modified: '2026-10-30T20:00:00Z',
};

const page = (entries: FileEntry[]): FolderPage =>
  ({ entries, total: entries.length, nextCursor: null }) as unknown as FolderPage;

describe('changeFolder', () => {
  it('reads a folder again when a change arrives while its first page is still loading', async () => {
    const client = new QueryClient();
    // The first read left before the folder was created; only a second read sees it.
    const reads: ((page: FolderPage) => void)[] = [];
    const observer = new InfiniteQueryObserver<
      FolderPage,
      Error,
      FolderPages,
      readonly unknown[],
      string | null
    >(client, {
      queryKey: [...folderKey('1', ''), 'name', 'asc', ''],
      queryFn: () =>
        new Promise<FolderPage>((resolve) => {
          reads.push(resolve);
        }),
      initialPageParam: null,
      getNextPageParam: () => null,
    });
    const stop = observer.subscribe(() => undefined);
    await expect.poll(() => reads.length).toBe(1);

    changeFolder(client, '1', '', { changed: [created], removed: [] });
    reads[0]?.(page([]));
    await expect.poll(() => reads.length).toBe(2);
    reads[1]?.(page([created]));
    await expect
      .poll(() => observer.getCurrentResult().data?.pages[0]?.entries.map((entry) => entry.name))
      .toEqual(['New folder']);
    stop();
  });
});
