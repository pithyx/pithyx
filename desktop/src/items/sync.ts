import { useQuery, type QueryClient } from '@tanstack/react-query';
import { unwrap, PithyxApiError, type components } from '@pithyx/api-client';
import { api } from '../api/client';
import { isSessionExpired } from '../errors/describe';
import { showError } from '../errors/errorStore';
import { notify } from '../shell/toasts';
import { itemsKey, itemsQuery, itemWrites } from './query';

export type DesktopItem = components['schemas']['DesktopItem'];
export type NewDesktopItem = components['schemas']['NewDesktopItem'];

/** What the user changed on one item. Fields left out stay. */
export interface ItemChange {
  id: number;
  name?: string;
  text?: string;
  column?: number;
  row?: number;
}

// Items created here get a negative id until the daemon answers; later writes look up the real one.
let nextTemporaryId = -1;
const createdIds = new Map<number, number>();
const temporaryIds = new Map<number, number>();

/** A key that stays the same when a new item gets its real id, so React keeps its element (and focus). */
export function renderKey(id: number): number {
  return temporaryIds.get(id) ?? id;
}

function realId(id: number): number | undefined {
  return id < 0 ? createdIds.get(id) : id;
}

// A change of an item that is still being created goes out after the item exists.
function enqueue(write: () => Promise<void>): Promise<void> {
  itemWrites.pending += 1;
  const run = itemWrites.queue.then(write).finally(() => {
    itemWrites.pending -= 1;
  });
  itemWrites.queue = run.catch(() => undefined);
  return run;
}

function readItems(client: QueryClient): DesktopItem[] {
  return client.getQueryData<DesktopItem[]>(itemsKey) ?? [];
}

function writeItems(client: QueryClient, update: (items: DesktopItem[]) => DesktopItem[]): void {
  client.setQueryData<DesktopItem[]>(itemsKey, (items) => update(items ?? []));
}

export function useDesktopItems() {
  return useQuery(itemsQuery);
}

function reportFailure(client: QueryClient, error: unknown): void {
  // The daemon's state is the truth again; an ended session leads to the sign-in screen anyway.
  void client.invalidateQueries({ queryKey: itemsKey });
  if (!isSessionExpired(error)) {
    showError(error);
  }
}

function codeOf(error: unknown): string | undefined {
  return error instanceof PithyxApiError ? error.code : undefined;
}

/** Puts a new item on the desktop right away and saves it. Returns its (temporary) id. */
export function createItem(client: QueryClient, item: NewDesktopItem): number {
  const id = nextTemporaryId--;
  void client.cancelQueries({ queryKey: itemsKey });
  writeItems(client, (items) => [
    ...items,
    {
      id,
      kind: item.kind,
      app: item.app ?? null,
      name: item.name ?? null,
      text: item.text ?? null,
      column: item.column,
      row: item.row,
      version: 0,
    },
  ]);
  void enqueue(async () => {
    try {
      const created = unwrap(await api.POST('/api/desktop/items', { body: item }));
      createdIds.set(id, created.id);
      temporaryIds.set(created.id, id);
      // Changes made while the item was being created stay; they are queued after this write.
      writeItems(client, (items) =>
        items.map((entry) =>
          entry.id === id ? { ...entry, id: created.id, version: created.version } : entry,
        ),
      );
    } catch (error) {
      reportFailure(client, error);
    }
  });
  return id;
}

function applyChange(item: DesktopItem, change: ItemChange): DesktopItem {
  return {
    ...item,
    ...(change.name === undefined
      ? {}
      : { name: change.name.trim() === '' ? null : change.name.trim() }),
    ...(change.text === undefined ? {} : { text: change.text }),
    ...(change.column === undefined ? {} : { column: change.column }),
    ...(change.row === undefined ? {} : { row: change.row }),
  };
}

interface SendResult {
  saved: DesktopItem[];
  /** Changes the daemon refused because the item changed or disappeared elsewhere. */
  refused: ItemChange[];
  /** The first of those refusals, if any. */
  refusal?: unknown;
  /** The first other failure, if any. */
  failure?: unknown;
}

function isRefusal(error: unknown): boolean {
  const code = codeOf(error);
  return code === 'desktop.item.conflict' || code === 'desktop.item.not_found';
}

async function patch(id: number, change: ItemChange, version: number): Promise<DesktopItem> {
  return unwrap(
    await api.PATCH('/api/desktop/items/{id}', {
      params: { path: { id }, header: { 'If-Match': `"${String(version)}"` } },
      // Null leaves a field as it is.
      body: {
        name: change.name ?? null,
        text: change.text ?? null,
        column: change.column ?? null,
        row: change.row ?? null,
      },
    }),
  );
}

/** Saves every change as its own request, all at once, each naming the version of its item that this device knows. */
async function send(
  changes: readonly ItemChange[],
  versions: ReadonlyMap<number, number>,
): Promise<SendResult> {
  const result: SendResult = { saved: [], refused: [] };
  await Promise.all(
    changes.map(async (change) => {
      const id = realId(change.id);
      const version = id === undefined ? undefined : versions.get(id);
      if (id === undefined || version === undefined) {
        return;
      }
      try {
        result.saved.push(await patch(id, change, version));
      } catch (error) {
        if (isRefusal(error)) {
          result.refused.push(change);
          result.refusal ??= error;
        } else if (result.failure === undefined) {
          result.failure = error;
        }
      }
    }),
  );
  return result;
}

function versionsOf(items: readonly DesktopItem[]): Map<number, number> {
  return new Map(items.map((item) => [item.id, item.version]));
}

// Only the version: a later change of the same item is already shown and still queued.
function takeVersions(client: QueryClient, saved: readonly DesktopItem[]): void {
  const versions = versionsOf(saved);
  writeItems(client, (items) =>
    items.map((item) => {
      const version = versions.get(item.id);
      return version === undefined ? item : { ...item, version };
    }),
  );
}

/**
 * Text written on a note that changed or disappeared on another device in the meantime is never dropped: it goes onto
 * a new note next to it, and a toast says so.
 */
function keepNoteText(
  client: QueryClient,
  lost: { text: string; column: number; row: number }[],
): void {
  for (const note of lost) {
    createItem(client, { kind: 'note', text: note.text, column: note.column + 1, row: note.row });
  }
  if (lost.length > 0) {
    notify({ code: 'desktop.noteKept', params: { count: lost.length } });
  }
}

/**
 * Changes items right away and saves the change, one request per item. If another device changed or deleted an item in
 * between (412 or 404), the daemon's items are loaded and the refused changes are saved on top of them once more: the
 * device in use wins, except that note text from the other device is kept (see keepNoteText).
 */
export function changeItems(client: QueryClient, changes: ItemChange[]): Promise<void> {
  const before = new Map(readItems(client).map((item) => [item.id, item]));
  void client.cancelQueries({ queryKey: itemsKey });
  const byId = new Map(changes.map((change) => [change.id, change]));
  writeItems(client, (items) =>
    items.map((item) => {
      const change = byId.get(item.id);
      return change === undefined ? item : applyChange(item, change);
    }),
  );
  return enqueue(async () => {
    const { saved, refused, failure } = await send(changes, versionsOf(readItems(client)));
    takeVersions(client, saved);
    if (refused.length > 0) {
      await retryOnTop(client, refused, before);
    }
    if (failure !== undefined) {
      reportFailure(client, failure);
    }
  });
}

export interface RetryPlan {
  /** Changes to save again on top of the daemon's items. */
  again: ItemChange[];
  /** Note text that would overwrite text written elsewhere, or whose note is gone, with the place of that note. */
  lost: { text: string; column: number; row: number }[];
}

/**
 * Decides what happens to changes the daemon refused because items changed elsewhere. Moves and new names are saved
 * again on top (the device in use wins). New note text is saved again only if the note's text did not change
 * elsewhere; otherwise it is lost for that note and kept on a new one. Changes of deleted items are dropped.
 */
export function planRetry(
  changes: readonly ItemChange[],
  before: ReadonlyMap<number, DesktopItem>,
  current: readonly DesktopItem[],
  resolve: (id: number) => number | undefined = realId,
): RetryPlan {
  const currentById = new Map(current.map((item) => [item.id, item]));
  const plan: RetryPlan = { again: [], lost: [] };
  for (const change of changes) {
    const id = resolve(change.id);
    const now = id === undefined ? undefined : currentById.get(id);
    const base = before.get(change.id);
    if (change.text !== undefined && (now === undefined || now.text !== base?.text)) {
      plan.lost.push({
        text: change.text,
        column: now?.column ?? base?.column ?? 0,
        row: now?.row ?? base?.row ?? 0,
      });
      const rest = { ...change };
      delete rest.text;
      if (now !== undefined && Object.keys(rest).length > 1) {
        plan.again.push(rest);
      }
    } else if (now !== undefined) {
      plan.again.push(change);
    }
  }
  return plan;
}

async function retryOnTop(
  client: QueryClient,
  changes: ItemChange[],
  before: ReadonlyMap<number, DesktopItem>,
): Promise<void> {
  try {
    const current = unwrap(await api.GET('/api/desktop/items'));
    const { again, lost } = planRetry(changes, before, current);
    client.setQueryData<DesktopItem[]>(
      itemsKey,
      current.map((item) => {
        const change = again.find((entry) => realId(entry.id) === item.id);
        return change === undefined ? item : applyChange(item, change);
      }),
    );
    const { saved, refusal, failure } = await send(again, versionsOf(current));
    takeVersions(client, saved);
    keepNoteText(client, lost);
    // Refused a second time: the daemon's items are the truth.
    const error = failure ?? refusal;
    if (error !== undefined) {
      reportFailure(client, error);
    }
  } catch (error) {
    reportFailure(client, error);
  }
}

/** Removes items right away and deletes them on the daemon. */
export function deleteItems(client: QueryClient, ids: number[]): void {
  const removed = new Set(ids);
  void client.cancelQueries({ queryKey: itemsKey });
  writeItems(client, (items) => items.filter((item) => !removed.has(item.id)));
  void enqueue(async () => {
    const real = ids.flatMap((id) => {
      const resolved = realId(id);
      return resolved === undefined ? [] : [resolved];
    });
    if (real.length === 0) {
      return;
    }
    const results = await Promise.allSettled(
      real.map(async (id) => {
        unwrap(await api.DELETE('/api/desktop/items/{id}', { params: { path: { id } } }));
      }),
    );
    const failed = results.find((result) => result.status === 'rejected');
    if (failed !== undefined) {
      reportFailure(client, failed.reason);
    }
  });
}
