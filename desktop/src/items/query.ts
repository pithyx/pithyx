import { unwrap } from '@vyxis/api-client';
import { api } from '../api/client';
import { isSessionExpired } from '../errors/describe';

export const itemsKey = ['desktop', 'items'] as const;

/**
 * Writes of desktop items, one after another, so each names (If-Match) the versions the previous one produced; the
 * requests of one write, one per item, go out together. A refetch (for example when the tab gets focus again) waits
 * until none is pending, so it cannot undo a change on its way.
 */
export const itemWrites = { pending: 0, queue: Promise.resolve() };

/** The user's desktop items. The desktop starts this with its first render; the items layer loads next to it. */
export const itemsQuery = {
  queryKey: itemsKey,
  queryFn: async ({ signal }: { signal: AbortSignal }) =>
    unwrap(await api.GET('/api/desktop/items', { signal })),
  refetchOnWindowFocus: () => itemWrites.pending === 0,
  retry: (failures: number, error: Error) => !isSessionExpired(error) && failures < 3,
};

/** Waits for every write that is still on its way, for example before signing out. */
export function settleItemWrites(): Promise<void> {
  return itemWrites.queue;
}
