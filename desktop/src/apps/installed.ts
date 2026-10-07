import type { components } from '@pithyx/api-client';
import { create } from 'zustand';

export type InstalledApp = components['schemas']['AppList']['apps'][number];

interface InstalledApps {
  /** The third-party apps the signed-in user may use, from the topic `apps`; undefined until its first snapshot. */
  apps: readonly InstalledApp[] | undefined;
}

/** Outside React too: opening and restoring windows look apps up here. */
export const useInstalledApps = create<InstalledApps>(() => ({ apps: undefined }));

export function setInstalledApps(apps: readonly InstalledApp[] | undefined): void {
  useInstalledApps.setState({ apps });
}

export function installedApp(id: string): InstalledApp | undefined {
  return useInstalledApps.getState().apps?.find((app) => app.id === id);
}

/** A text of the manifest in the user's language: the exact language, then its base language, then English. */
export function manifestText(texts: Readonly<Record<string, string>>, language: string): string {
  return texts[language] ?? texts[language.split('-')[0] ?? ''] ?? texts.en ?? '';
}
