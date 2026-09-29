import type { FileEntry } from '@pithyx/sdk';
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

/** The ways "Open with" offers for a file; later apps that open files add themselves here. */
export type OpenWith = 'viewer' | 'download';

export function openWithChoices(entry: FileEntry): OpenWith[] {
  return entry.kind !== 'file'
    ? []
    : viewKindOf(entry.name) === null
      ? ['download']
      : ['viewer', 'download'];
}
