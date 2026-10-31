import type { PithyxApp } from '@pithyx/sdk';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
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

/** The apps of the enabled catalogs whose id, name, description, author or keywords contain the query; all for ''. */
export function useCatalogApps(app: PithyxApp, query: string) {
  return useQuery({
    queryKey: ['store', 'search', query],
    queryFn: () => storeCall(app, 'internal.admin.store.search', query === '' ? {} : { query }),
  });
}

/** A catalog's name in the user's language, else its id or URL. */
export function catalogName(catalog: CatalogInfo, language: string): string {
  const names = catalog.name;
  if (names !== null) {
    return names[language] ?? names[language.slice(0, 2)] ?? names.en ?? catalog.url;
  }
  return catalog.id ?? catalog.url;
}

/**
 * Where an app comes from, in a few words: "Official catalog" and "Community catalog" for the project's two, a custom
 * catalog by its name, and a package file.
 */
export function useSourceLabel(catalogs: readonly CatalogInfo[]) {
  const { t, i18n } = useTranslation('store');
  return (source: { catalog: string | null; kind?: CatalogKind }) => {
    if (source.catalog === null) {
      return t('card.sideload');
    }
    const catalog = catalogs.find((entry) => entry.id === source.catalog);
    const kind = source.kind ?? catalog?.kind ?? 'custom';
    if (kind !== 'custom') {
      return t(`card.${kind}`);
    }
    return t('card.custom', {
      name: catalog === undefined ? source.catalog : catalogName(catalog, i18n.language),
    });
  };
}
