import type { FileEntry } from '@pithyx/sdk';
import { createStore } from 'zustand/vanilla';
import type { ThumbnailSize, ThumbnailState, ThumbnailsReady } from '../sdk/filesInternal';
import { hasThumbnail } from '../viewer/kinds';

export interface Thumbnail {
  status: ThumbnailState['status'];
  version: string | null;
}

/** At most this many names per request, as the daemon allows. */
export const BATCH = 200;

/** A thumbnail still pending after this long is asked for again, in case its event came before the subscription. */
export const RECHECK_MS = 3000;

/**
 * One state per version of a file: a changed entry (other size or time) is another key, so its old thumbnail is
 * never shown for it.
 */
export function thumbnailKey(
  share: string,
  folder: string,
  size: ThumbnailSize,
  entry: Pick<FileEntry, 'name' | 'size' | 'modified'>,
): string {
  return [share, folder, size, entry.name, String(entry.size), entry.modified].join('\n');
}

export type Ask = (
  share: string,
  folder: string,
  names: string[],
  size: ThumbnailSize,
) => Promise<ThumbnailState[]>;

/**
 * The thumbnails of the entries a Files window shows. Only entries that were on screen are ever asked for, and
 * each version of a file once; the answers and the daemon's later events land in one store.
 */
export function createThumbnails(
  ask: Ask,
  schedule: (run: () => void, ms: number) => void = (run, ms) => {
    setTimeout(run, ms);
  },
) {
  const store = createStore<{ byKey: Readonly<Record<string, Thumbnail>> }>(() => ({ byKey: {} }));
  // Which key a name of a folder stands for right now, to place events that only carry the name.
  const current = new Map<string, string>();
  const nameKey = (share: string, folder: string, size: ThumbnailSize, name: string) =>
    [share, folder, size, name].join('\n');

  function set(updates: Record<string, Thumbnail>) {
    store.setState((state) => ({ byKey: { ...state.byKey, ...updates } }));
  }

  async function send(
    share: string,
    folder: string,
    size: ThumbnailSize,
    entries: FileEntry[],
    again: number,
  ) {
    let states: ThumbnailState[];
    try {
      states = await ask(
        share,
        folder,
        entries.map((entry) => entry.name),
        size,
      );
    } catch {
      // No thumbnails is no error: the icons stay.
      set(
        Object.fromEntries(
          entries.map((entry) => [
            thumbnailKey(share, folder, size, entry),
            { status: 'none', version: null },
          ]),
        ),
      );
      return;
    }
    const byName = new Map(states.map((state) => [state.name, state]));
    const pending: FileEntry[] = [];
    const updates: Record<string, Thumbnail> = {};
    for (const entry of entries) {
      const state = byName.get(entry.name);
      const key = thumbnailKey(share, folder, size, entry);
      if (current.get(nameKey(share, folder, size, entry.name)) !== key) {
        continue;
      }
      updates[key] = { status: state?.status ?? 'none', version: state?.version ?? null };
      if (state?.status === 'pending') {
        pending.push(entry);
      }
    }
    set(updates);
    if (pending.length > 0 && again > 0) {
      schedule(() => {
        const still = pending.filter(
          (entry) =>
            store.getState().byKey[thumbnailKey(share, folder, size, entry)]?.status === 'pending',
        );
        if (still.length > 0) {
          void send(share, folder, size, still, again - 1);
        }
      }, RECHECK_MS);
    }
  }

  return {
    store,
    /** Asks for the thumbnails of these entries that were never asked for in this version. */
    request: (
      share: string,
      folder: string,
      entries: readonly FileEntry[],
      size: ThumbnailSize,
    ) => {
      const known = store.getState().byKey;
      const wanted = entries.filter((entry) => {
        if (entry.kind !== 'file' || !hasThumbnail(entry.name)) {
          return false;
        }
        const key = thumbnailKey(share, folder, size, entry);
        current.set(nameKey(share, folder, size, entry.name), key);
        return known[key] === undefined;
      });
      if (wanted.length === 0) {
        return;
      }
      set(
        Object.fromEntries(
          wanted.map((entry) => [
            thumbnailKey(share, folder, size, entry),
            { status: 'pending', version: null },
          ]),
        ),
      );
      for (let start = 0; start < wanted.length; start += BATCH) {
        void send(share, folder, size, wanted.slice(start, start + BATCH), 3);
      }
    },
    /** An event of the daemon: thumbnails of the folder that are ready or refused now. */
    receive: (ready: ThumbnailsReady) => {
      const updates: Record<string, Thumbnail> = {};
      for (const item of ready.items) {
        const key = current.get(nameKey(ready.share, ready.folder, ready.size, item.name));
        if (key !== undefined) {
          updates[key] = { status: item.status, version: item.version };
        }
      }
      set(updates);
    },
  };
}

export type Thumbnails = ReturnType<typeof createThumbnails>;
