import type { InstalledApp } from '../installed';

export type AppFileAction = InstalledApp['fileActions'][number];

/** An entry of an app for the context menu of the selection (D375). */
export interface OfferedAction {
  app: string;
  action: AppFileAction;
}

/** What the menu knows of a selected item. */
export interface SelectedItem {
  name: string;
  kind: string;
}

function matches(action: AppFileAction, item: SelectedItem): boolean {
  if (item.kind === 'folder') {
    return action.folders;
  }
  if (item.kind !== 'file') {
    return false;
  }
  const name = item.name.toLowerCase();
  return action.extensions.some((extension) => name.endsWith(`.${extension}`));
}

/**
 * The file actions of the installed apps that apply to every selected item: an action for one item only when exactly
 * one is selected, one that writes only where the user may change files, and only of apps that still hold
 * `files.shares` (an administrator may have revoked it). In the apps' order, then the manifest's.
 */
export function actionsFor(
  items: readonly SelectedItem[],
  apps: readonly InstalledApp[],
  writable: boolean,
): OfferedAction[] {
  if (items.length === 0) {
    return [];
  }
  return apps
    .filter((app) => app.permissions.includes('files.shares'))
    .flatMap((app) =>
      app.fileActions
        .filter(
          (action) =>
            (action.multiple || items.length === 1) &&
            (!action.writes || writable) &&
            items.every((item) => matches(action, item)),
        )
        .map((action) => ({ app: app.id, action })),
    );
}
