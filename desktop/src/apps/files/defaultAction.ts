import type { FileEntry } from '@pithyx/sdk';
import type { InstalledApp } from '../installed';
import { viewKindOf } from '../viewer/kinds';

/**
 * What a double click, Enter or a tap does with an entry: the one place that decides. Files the viewer shows open
 * in it; every other file is downloaded. A symlink is opened like a folder; the app downloads it when the daemon
 * says it points to a file.
 */
export type DefaultAction =
  { kind: 'openFolder' } | { kind: 'view' } | { kind: 'download' } | { kind: 'none' };

export function defaultAction(entry: FileEntry): DefaultAction {
  switch (entry.kind) {
    case 'folder':
    case 'symlink':
      return { kind: 'openFolder' };
    case 'file':
      return viewKindOf(entry.name) === null ? { kind: 'download' } : { kind: 'view' };
    case 'other':
      return { kind: 'none' };
  }
}

/** The ways "Open with" offers for a file: the viewer, a download, and an installed app by its id (`app:<id>`). */
export type OpenWith = 'viewer' | 'download' | `app:${string}`;

/** The installed apps whose `fileHandlers` name the extension of a file. */
export function appsFor(entry: FileEntry, apps: readonly InstalledApp[]): string[] {
  const name = entry.name.toLowerCase();
  return apps
    .filter((app) =>
      app.fileHandlers.some((handler) =>
        handler.extensions.some((extension) => name.endsWith(`.${extension}`)),
      ),
    )
    .map((app) => app.id);
}

export function openWithChoices(entry: FileEntry, apps: readonly InstalledApp[] = []): OpenWith[] {
  if (entry.kind !== 'file') {
    return [];
  }
  return [
    ...(viewKindOf(entry.name) === null ? [] : (['viewer'] as const)),
    'download',
    ...appsFor(entry, apps).map((id): OpenWith => `app:${id}`),
  ];
}
