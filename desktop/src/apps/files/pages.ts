import type { InfiniteData } from '@tanstack/react-query';
import type { FileEntry, FolderChange, FolderPage, SortKey, SortOrder } from '@pithyx/sdk';
import { compareEntries } from './order';

export type FolderPages = InfiniteData<FolderPage, string | null>;

/**
 * Applies a live folder change to the pages loaded so far. Removed names leave; changed entries are updated in place
 * or, when they are new, inserted where the server's order puts them, as long as that place is within the loaded
 * range (an entry that belongs further down arrives with a later page). The total follows.
 */
export function applyChange(
  data: FolderPages,
  change: FolderChange,
  sort: SortKey,
  order: SortOrder,
): FolderPages {
  const removed = new Set(change.removed);
  const changed = new Map(change.changed.map((entry) => [entry.name, entry]));
  let total = data.pages[0]?.total ?? 0;
  const seen = new Set<string>();
  let pages = data.pages.map((page) => {
    const entries: FileEntry[] = [];
    for (const entry of page.entries) {
      if (removed.has(entry.name) && !changed.has(entry.name)) {
        total--;
        continue;
      }
      seen.add(entry.name);
      entries.push(changed.get(entry.name) ?? entry);
    }
    return { ...page, entries };
  });

  const fresh = [...changed.values()].filter((entry) => !seen.has(entry.name));
  const complete = pages.at(-1)?.nextCursor === null;
  for (const entry of fresh) {
    total++;
    pages = insert(pages, entry, sort, order, complete);
  }
  return { ...data, pages: pages.map((page) => ({ ...page, total })) };
}

function insert(
  pages: FolderPage[],
  entry: FileEntry,
  sort: SortKey,
  order: SortOrder,
  complete: boolean,
): FolderPage[] {
  for (let p = 0; p < pages.length; p++) {
    const page = pages[p];
    if (page === undefined) {
      continue;
    }
    const index = page.entries.findIndex((other) => compareEntries(entry, other, sort, order) < 0);
    if (index >= 0) {
      const entries = [...page.entries];
      entries.splice(index, 0, entry);
      return pages.map((other, i) => (i === p ? { ...other, entries } : other));
    }
  }
  // Past every loaded entry: it shows now only if nothing more is to come; otherwise a later page brings it.
  if (!complete || pages.length === 0) {
    return pages;
  }
  return pages.map((page, i) =>
    i === pages.length - 1 ? { ...page, entries: [...page.entries, entry] } : page,
  );
}

/** All loaded entries in order. */
export function loadedEntries(data: FolderPages | undefined): FileEntry[] {
  return data?.pages.flatMap((page) => page.entries) ?? [];
}
