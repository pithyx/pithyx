import type { FileItems } from '@pithyx/sdk';

/**
 * The selection in a folder. `some` names the selected entries; `all` means every entry of the folder except some,
 * so selecting a folder of 100,000 entries needs no list of their names, locally or on the wire.
 */
export type Selection =
  | { kind: 'some'; names: ReadonlySet<string>; anchor: string | null }
  | { kind: 'all'; except: ReadonlySet<string> };

export const emptySelection: Selection = { kind: 'some', names: new Set(), anchor: null };

export function isSelected(selection: Selection, name: string): boolean {
  return selection.kind === 'some' ? selection.names.has(name) : !selection.except.has(name);
}

export function selectedCount(selection: Selection, total: number): number {
  return selection.kind === 'some'
    ? selection.names.size
    : Math.max(0, total - selection.except.size);
}

/** A click without modifiers: only this entry. */
export function selectOnly(name: string): Selection {
  return { kind: 'some', names: new Set([name]), anchor: name };
}

/** Ctrl or Cmd click, or Ctrl+Space: this entry in or out. */
export function toggle(selection: Selection, name: string): Selection {
  if (selection.kind === 'all') {
    const except = new Set(selection.except);
    if (except.has(name)) {
      except.delete(name);
    } else {
      except.add(name);
    }
    return { kind: 'all', except };
  }
  const names = new Set(selection.names);
  if (names.has(name)) {
    names.delete(name);
  } else {
    names.add(name);
  }
  return { kind: 'some', names, anchor: name };
}

/**
 * Shift click or Shift with an arrow key: everything between the anchor and this entry, in the order on screen.
 * `ordered` holds the names loaded so far; a range can only span loaded entries.
 */
export function selectRange(
  selection: Selection,
  ordered: readonly string[],
  name: string,
  additive = false,
): Selection {
  const anchor = selection.kind === 'some' ? (selection.anchor ?? name) : name;
  const from = ordered.indexOf(anchor);
  const to = ordered.indexOf(name);
  if (from < 0 || to < 0) {
    return selectOnly(name);
  }
  const range = ordered.slice(Math.min(from, to), Math.max(from, to) + 1);
  const names = new Set(
    additive && selection.kind === 'some' ? [...selection.names, ...range] : range,
  );
  return { kind: 'some', names, anchor };
}

export function selectAll(): Selection {
  return { kind: 'all', except: new Set() };
}

/** Names that were removed from the folder leave the selection. */
export function forget(selection: Selection, removed: readonly string[]): Selection {
  if (removed.length === 0) {
    return selection;
  }
  if (selection.kind === 'all') {
    const except = new Set(selection.except);
    for (const name of removed) {
      except.delete(name);
    }
    return { kind: 'all', except };
  }
  const names = new Set(selection.names);
  for (const name of removed) {
    names.delete(name);
  }
  return {
    kind: 'some',
    names,
    anchor:
      selection.anchor !== null && removed.includes(selection.anchor) ? null : selection.anchor,
  };
}

/** The selection as an operation sends it. */
export function toItems(selection: Selection): FileItems {
  return selection.kind === 'some'
    ? { names: [...selection.names] }
    : { all: true, except: [...selection.except] };
}

/** The one selected name, when exactly one entry is selected by name. */
export function single(selection: Selection): string | null {
  return selection.kind === 'some' && selection.names.size === 1
    ? ([...selection.names][0] ?? null)
    : null;
}
