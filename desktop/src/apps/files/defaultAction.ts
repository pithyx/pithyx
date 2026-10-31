import type { FileEntry } from '@pithyx/sdk';
import type { InstalledApp } from '../installed';
import { viewKindOf } from '../viewer/kinds';

/**
 * What a double click, Enter or a tap does with an entry: the one place that decides. A file opens the way the user
 * chose for its extension ("Always use this app"), else in the only app whose manifest names it as default, else in
 * the viewer if it shows the file, else it is downloaded. A symlink is opened like a folder; the app downloads it when
 * the daemon says it points to a file.
 */
export type DefaultAction =
  | { kind: 'openFolder' }
  | { kind: 'view' }
  | { kind: 'download' }
  | { kind: 'app'; id: string }
  | { kind: 'none' };

/** The ways "Open with" offers for a file: the viewer, a download, and an installed app by its id (`app:<id>`). */
export type OpenWith = 'viewer' | 'download' | `app:${string}`;

/** The user's choices by extension (lowercase, without the dot), as `GET /api/desktop` returns them. */
export type FileDefaults = Readonly<Record<string, string>>;

const EXTENSION = /^[a-z0-9]+([.+_-][a-z0-9]+)*$/;

/**
 * The extension a choice for this file is saved under: the part after the last dot, if the daemon accepts it.
 * `null` for a name without one, so "Always use this app" is not offered.
 */
export function choiceExtension(name: string): string | null {
  const dot = name.lastIndexOf('.');
  if (dot <= 0 || dot === name.length - 1) {
    return null;
  }
  const extension = name.slice(dot + 1).toLowerCase();
  return extension.length <= 32 && EXTENSION.test(extension) ? extension : null;
}

function handles(app: InstalledApp, name: string, onlyDefault: boolean): boolean {
  return app.fileHandlers.some(
    (handler) =>
      (!onlyDefault || handler.isDefault) &&
      handler.extensions.some((extension) => name.endsWith(`.${extension}`)),
  );
}

/** The installed apps whose `fileHandlers` name the extension of a file. */
export function appsFor(entry: FileEntry, apps: readonly InstalledApp[]): string[] {
  const name = entry.name.toLowerCase();
  return apps.filter((app) => handles(app, name, false)).map((app) => app.id);
}

export function openWithChoices(entry: FileEntry, apps: readonly InstalledApp[] = []): OpenWith[] {
  if (entry.kind !== 'file') {
    return [];
  }
  const name = entry.name.toLowerCase();
  // Apps that declare themselves the default for the type come first, then the other apps, the viewer and a download.
  const ids = appsFor(entry, apps);
  const preferred = new Set(apps.filter((app) => handles(app, name, true)).map((app) => app.id));
  return [
    ...ids.filter((id) => preferred.has(id)).map((id): OpenWith => `app:${id}`),
    ...ids.filter((id) => !preferred.has(id)).map((id): OpenWith => `app:${id}`),
    ...(viewKindOf(entry.name) === null ? [] : (['viewer'] as const)),
    'download',
  ];
}

/** The choice of "Open with" that a double click on the file makes now, to mark it in the list. */
export function currentChoice(
  entry: FileEntry,
  apps: readonly InstalledApp[] = [],
  choices: FileDefaults = {},
): OpenWith | null {
  const action = defaultAction(entry, apps, choices);
  switch (action.kind) {
    case 'view':
      return 'viewer';
    case 'download':
      return 'download';
    case 'app':
      return `app:${action.id}`;
    default:
      return null;
  }
}

function asAction(choice: OpenWith): DefaultAction {
  if (choice === 'viewer') {
    return { kind: 'view' };
  }
  if (choice === 'download') {
    return { kind: 'download' };
  }
  return { kind: 'app', id: choice.slice('app:'.length) };
}

export function defaultAction(
  entry: FileEntry,
  apps: readonly InstalledApp[] = [],
  choices: FileDefaults = {},
): DefaultAction {
  switch (entry.kind) {
    case 'folder':
    case 'symlink':
      return { kind: 'openFolder' };
    case 'other':
      return { kind: 'none' };
    case 'file':
      break;
  }
  const extension = choiceExtension(entry.name);
  const chosen = extension === null ? undefined : choices[extension];
  // A choice of an app that is gone or no longer handles the file counts as no choice.
  const offered = openWithChoices(entry, apps);
  const usable = offered.find((choice) => choice === chosen);
  if (usable !== undefined) {
    return asAction(usable);
  }
  const name = entry.name.toLowerCase();
  const defaults = apps.filter((app) => handles(app, name, true));
  if (defaults.length === 1 && defaults[0] !== undefined) {
    return { kind: 'app', id: defaults[0].id };
  }
  return viewKindOf(entry.name) === null ? { kind: 'download' } : { kind: 'view' };
}
