import type { FileEntry } from '@vyxis/sdk';

/**
 * What a double click, Enter or a tap does with an entry. Files download for now; once viewers exist, this is the one
 * place that decides to open a file in one instead. A symlink is opened like a folder; the app downloads it when the
 * daemon says it points to a file.
 */
export type DefaultAction = { kind: 'openFolder' } | { kind: 'download' } | { kind: 'none' };

export function defaultAction(entry: FileEntry): DefaultAction {
  switch (entry.kind) {
    case 'folder':
    case 'symlink':
      return { kind: 'openFolder' };
    case 'file':
      return { kind: 'download' };
    case 'other':
      return { kind: 'none' };
  }
}
