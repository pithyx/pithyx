import type { PithyxApp } from '@pithyx/sdk';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import {
  onStore,
  STORE_CHANGED,
  storeCall,
  type CatalogInfo,
  type CatalogKind,
  type StoreState,
} from '../sdk/store';

export const storeKey = ['store', 'state'] as const;

/** The Store's settings, catalogs and updates; the topic `store/admin` keeps them current. */
export function useStoreState(app: PithyxApp) {
  const queryClient = useQueryClient();
  // The desktop forwards the topic to this window: syncing the query cache with the SDK's events.
  useEffect(
    () =>
      onStore(app, STORE_CHANGED, (data) => {
        queryClient.setQueryData<StoreState>(storeKey, data as StoreState);
        // Fetched catalogs and installed apps change what the tabs and app pages show.
        void queryClient.invalidateQueries({ queryKey: ['store', 'search'] });
        void queryClient.invalidateQueries({ queryKey: ['store', 'details'] });
      }),
    [app, queryClient],
  );
  return useQuery({
    queryKey: storeKey,
    queryFn: () => storeCall(app, 'internal.admin.store.state', {}),
  });
}

/** The tab a catalog's apps appear in: the project's two by kind, every custom catalog under Own. */
export type CatalogTab = 'official' | 'community' | 'own';

export function tabOf(kind: CatalogKind): CatalogTab {
  return kind === 'custom' ? 'own' : kind;
}

/** The enabled catalogs of a tab. */
export function catalogsOf(catalogs: readonly CatalogInfo[], tab: CatalogTab): CatalogInfo[] {
  return catalogs.filter((catalog) => catalog.enabled && tabOf(catalog.kind) === tab);
}

/** A catalog's name in the user's language, else its id or URL. */
export function catalogName(catalog: CatalogInfo, language: string): string {
  const names = catalog.name;
  if (names !== null) {
    return names[language] ?? names[language.slice(0, 2)] ?? names.en ?? catalog.url;
  }
  return catalog.id ?? catalog.url;
}
