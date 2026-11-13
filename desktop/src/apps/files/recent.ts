import { unwrap, type components } from '@pithyx/api-client';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useEffect } from 'react';
import { api } from '../../api/client';
import { realtime, useRealtime } from '../../realtime/connection';

/** The place of the Recent list in Files, saved like a share id; real share ids are numbers. */
export const RECENT_PLACE = 'recent';

/** A file the user opened recently, as the box lists it (#333). */
export type RecentFile = components['schemas']['RecentFile'];

/**
 * The signed-in user's recent files, newest first, for the Pithyx menu and the Recent place in Files; `undefined`
 * until the box answered, which hides both. The list is read again whenever the user's list (`files/recent`) or the
 * shares they can use (`shares/list`) change, so an entry that became unreadable goes away.
 */
export function useRecentFiles(): readonly RecentFile[] | undefined {
  // Subscribing is synchronization with the WebSocket, not data fetching.
  useEffect(() => {
    const stops = (['files/recent', 'shares/list'] as const).map((topic) =>
      realtime.subscribe(topic),
    );
    return () => {
      for (const stop of stops) {
        stop();
      }
    };
  }, []);
  const revision = useRealtime((state) => state.data['files/recent']?.revision);
  const shares = useRealtime((state) => state.data['shares/list']?.revision);
  const query = useQuery({
    queryKey: ['files', 'recent', revision, shares],
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/files/recent', { signal })),
    placeholderData: keepPreviousData,
  });
  return query.data;
}

/**
 * Tells the box that the user opened a file: a double click or "Open with" in Files, a file shortcut, an app's file
 * dialog. The list's topic brings the change to every window of the user.
 */
export function noteRecentFile(share: number, path: string): void {
  // A lost entry only leaves the list one file short; it is not worth an error dialog.
  void api.POST('/api/files/recent', { body: { share, path } }).catch(() => undefined);
}

/** Forgets the signed-in user's recent files. */
export async function clearRecentFiles(): Promise<void> {
  unwrap(await api.DELETE('/api/files/recent'));
}
