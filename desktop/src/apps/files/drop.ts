import type { FileItems } from '@pithyx/sdk';

/** What a drag of entries carries between Files windows. */
export interface DraggedEntries {
  share: string | null;
  folder: string;
  items: FileItems;
}

/** A folder entries can be dropped into: one in the list, a breadcrumb segment or a share's root in the sidebar. */
export interface DropTarget {
  share: string;
  path: string;
}

/** The items without one name, whether they are named one by one or a whole folder. */
export function withoutName(items: FileItems, name: string): FileItems {
  return 'names' in items
    ? { names: items.names.filter((other) => other !== name) }
    : { all: true, except: [...items.except, name] };
}

export function isEmpty(items: FileItems): boolean {
  return 'names' in items && items.names.length === 0;
}

/** Reads the data of a drag of entries; null for anything else. */
export function parseDragged(data: string): DraggedEntries | null {
  try {
    const dragged = JSON.parse(data) as DraggedEntries;
    return typeof dragged.folder === 'string' && typeof dragged.items === 'object' ? dragged : null;
  } catch {
    return null;
  }
}

/**
 * The items a drop moves or copies into the target, or null when it would do nothing: entries dropped on the folder
 * they are in, or a folder dropped into itself or below it (it leaves the drop, its siblings still go).
 */
export function itemsToDrop(dragged: DraggedEntries, target: DropTarget): FileItems | null {
  if (dragged.share === null) {
    return null;
  }
  let items = dragged.items;
  if (dragged.share === target.share) {
    if (target.path === dragged.folder) {
      return null;
    }
    const prefix = dragged.folder === '' ? '' : `${dragged.folder}/`;
    if (target.path.startsWith(prefix)) {
      items = withoutName(items, target.path.slice(prefix.length).split('/')[0] ?? '');
    }
  }
  return isEmpty(items) ? null : items;
}
