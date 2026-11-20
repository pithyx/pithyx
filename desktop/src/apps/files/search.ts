import { unwrap, type components } from '@pithyx/api-client';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { api } from '../../api/client';

/** The place of a search over every place in Files, saved like a share id with the search as its path (D413). */
export const SEARCH_PLACE = 'search';

/** A file or folder whose name matched, as the box checked it on disk. */
export type SearchHit = components['schemas']['SearchHit'];

export type SearchResults = components['schemas']['SearchResults'];

/** Waits this long after the last key before the box is asked. */
const TYPING_DELAY = 200;

/** `value`, once it stayed the same for `delay` milliseconds. */
function useSettled(value: string, delay: number): string {
  const [settled, setSettled] = useState(value);
  // A timer is synchronization with something outside React.
  useEffect(() => {
    const timer = setTimeout(() => {
      setSettled(value);
    }, delay);
    return () => {
      clearTimeout(timer);
    };
  }, [value, delay]);
  return settled;
}

/**
 * The names in the shared folders the user can read and their own home that contain every word of `query`, best
 * matches first, for the Pithyx menu and the search in Files; `undefined` until the box answered for the first search.
 * Typing keeps the previous hits until the next answer.
 */
export function useFileSearch(
  query: string,
  { limit, enabled = true }: { limit: number; enabled?: boolean },
): SearchResults | undefined {
  const settled = useSettled(query.trim(), TYPING_DELAY);
  const result = useQuery({
    queryKey: ['files', 'search', settled, limit],
    queryFn: async ({ signal }) =>
      unwrap(
        await api.GET('/api/files/search', { params: { query: { q: settled, limit } }, signal }),
      ),
    enabled: enabled && settled !== '',
    placeholderData: keepPreviousData,
  });
  return settled === '' ? undefined : result.data;
}
