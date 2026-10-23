import { unwrap } from '@pithyx/api-client';
import { create } from 'zustand';
import { api } from '../api/client';
import { isAppAllowed } from '../apps/access';
import { findApp } from '../apps/registry';
import { isAfterSignIn, takeSignIn } from './signIn';
import { openApp, useDesktop } from './store';

/** At most this many apps open after signing in (the daemon's limit). */
export const MAX_STARTUP_APPS = 10;

interface StartupAppsState {
  /** The apps the user opens after signing in, in order, from `GET /api/desktop`; the same on every device. */
  apps: readonly string[];
}

export const useStartupApps = create<StartupAppsState>(() => ({ apps: [] }));

export function applyStartupApps(apps: readonly string[]): void {
  useStartupApps.setState({ apps });
}

/** Whether the startup apps still wait for the topic `apps`, because one of them is not known yet. */
export function startupWaitsForApps(appsKnown: boolean): boolean {
  return (
    isAfterSignIn() &&
    !appsKnown &&
    useStartupApps.getState().apps.some((id) => findApp(id) === undefined)
  );
}

/**
 * Opens the startup apps once after a sign-in, after the saved layout was restored: an app that already has a window
 * stays as it is, one that is gone or not allowed is skipped.
 */
export function openStartupApps(): void {
  if (!takeSignIn()) {
    return;
  }
  for (const id of useStartupApps.getState().apps) {
    const open = Object.values(useDesktop.getState().windows).some((window) => window.app === id);
    if (!open && isAppAllowed(id)) {
      openApp(id);
    }
  }
}

/** Saves the whole list at once; a refused save puts the previous list back and throws. */
export async function saveStartupApps(apps: readonly string[]): Promise<void> {
  const previous = useStartupApps.getState().apps;
  useStartupApps.setState({ apps });
  try {
    unwrap(await api.PUT('/api/desktop/startup-apps', { body: { apps: [...apps] } }));
  } catch (error) {
    useStartupApps.setState({ apps: previous });
    throw error;
  }
}
