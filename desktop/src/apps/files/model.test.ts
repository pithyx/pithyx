import type { FileEntry, FolderPage } from '@vyxis/sdk';
import { describe, expect, it } from 'vitest';
import { defaultAction } from './defaultAction';
import { compareEntries, naturalKey } from './order';
import { applyChange, loadedEntries, type FolderPages } from './pages';
import { crumbsOf, freeName, joinPath, parentOf } from './paths';
import {
  emptySelection,
  forget,
  isSelected,
  selectAll,
  selectedCount,
  selectOnly,
  selectRange,
  toggle,
  toItems,
} from './selection';

const sorted = (...names: string[]) =>
  [...names].sort((a, b) =>
    naturalKey(a) < naturalKey(b) ? -1 : naturalKey(a) > naturalKey(b) ? 1 : 0,
  );

function entry(name: string, size = 1, kind: FileEntry['kind'] = 'file'): FileEntry {
  return { name, kind, size, modified: '2026-09-27T10:00:00Z' };
}

function pages(...chunks: string[][]): FolderPages {
  const total = chunks.flat().length;
  const list: FolderPage[] = chunks.map((names, index) => ({
    path: '',
    folderId: 'f',
    total,
    entries: names.map((name) => entry(name)),
    nextCursor: index === chunks.length - 1 ? null : `c${String(index)}`,
  }));
  return {
    pages: list,
    pageParams: list.map((_, index) => (index === 0 ? null : `c${String(index - 1)}`)),
  };
}

describe('natural order', () => {
  // The same cases as the daemon's NaturalSortTests, so both sides sort alike.
  it('matches the daemon', () => {
    expect(
      sorted(
        'file100.txt',
        'file10.txt',
        'Image 2',
        'File2.txt',
        'image',
        'file1.txt',
        'file010b.txt',
      ),
    ).toEqual([
      'file1.txt',
      'File2.txt',
      'file10.txt',
      'file010b.txt',
      'file100.txt',
      'image',
      'Image 2',
    ]);
    expect(sorted('a', '1 a')).toEqual(['1 a', 'a']);
    expect(sorted('99999999999999999999999', '2')).toEqual(['2', '99999999999999999999999']);
    expect(naturalKey('7')).not.toBe(naturalKey('007'));
  });

  it('orders by size and date with the name as tie-break', () => {
    expect(compareEntries(entry('b', 5), entry('a', 5), 'size', 'asc')).toBeGreaterThan(0);
    expect(compareEntries(entry('a', 1), entry('b', 9), 'size', 'desc')).toBeGreaterThan(0);
    expect(compareEntries(entry('dir', 0, 'folder'), entry('a', 0), 'size', 'asc')).toBeLessThan(0);
  });
});

describe('selection', () => {
  it('selects one, toggles and selects ranges in screen order', () => {
    const order = ['a', 'b', 'c', 'd'];
    let selection = selectOnly('b');
    selection = selectRange(selection, order, 'd');
    expect(['a', 'b', 'c', 'd'].map((name) => isSelected(selection, name))).toEqual([
      false,
      true,
      true,
      true,
    ]);
    selection = toggle(selection, 'c');
    expect(isSelected(selection, 'c')).toBe(false);
    expect(toItems(selection)).toEqual({ names: ['b', 'd'] });
  });

  it('selects a whole folder without naming its entries', () => {
    let selection = toggle(selectAll(), 'keep.txt');
    expect(selectedCount(selection, 100_000)).toBe(99_999);
    expect(isSelected(selection, 'any')).toBe(true);
    expect(toItems(selection)).toEqual({ all: true, except: ['keep.txt'] });
    selection = forget(selection, ['keep.txt']);
    expect(toItems(selection)).toEqual({ all: true, except: [] });
    expect(selectedCount(emptySelection, 10)).toBe(0);
  });
});

describe('live changes on loaded pages', () => {
  it('removes, updates and inserts where the server order puts an entry', () => {
    const data = pages(['a', 'c'], ['e', 'g']);
    const next = applyChange(
      data,
      {
        watch: 'w',
        changed: [entry('d'), entry('c', 99)],
        removed: ['a'],
        resync: false,
        gone: false,
      },
      'name',
      'asc',
    );
    expect(loadedEntries(next).map((item) => item.name)).toEqual(['c', 'd', 'e', 'g']);
    expect(loadedEntries(next)[0]?.size).toBe(99);
    expect(next.pages[0]?.total).toBe(4);
  });

  it('leaves an entry past the loaded range to a later page', () => {
    const [first] = pages(['a', 'b']).pages;
    if (first === undefined) {
      throw new Error('fixture has no page');
    }
    const data = { pages: [{ ...first, nextCursor: 'more' }], pageParams: [null] };
    const next = applyChange(
      data,
      { watch: 'w', changed: [entry('z')], removed: [], resync: false, gone: false },
      'name',
      'asc',
    );
    expect(loadedEntries(next).map((item) => item.name)).toEqual(['a', 'b']);
    expect(next.pages[0]?.total).toBe(3);
  });
});

describe('default action', () => {
  it('opens folders and downloads files for now', () => {
    expect(defaultAction(entry('dir', 0, 'folder'))).toEqual({ kind: 'openFolder' });
    expect(defaultAction(entry('a.pdf'))).toEqual({ kind: 'download' });
    expect(defaultAction(entry('dev', 0, 'other'))).toEqual({ kind: 'none' });
  });
});

describe('paths', () => {
  it('walks up and down from the share root', () => {
    expect(joinPath('', 'Photos')).toBe('Photos');
    expect(joinPath('Photos', '2026')).toBe('Photos/2026');
    expect(parentOf('Photos/2026')).toBe('Photos');
    expect(parentOf('Photos')).toBe('');
    expect(crumbsOf('')).toEqual([]);
    expect(crumbsOf('a/b')).toEqual([
      { name: 'a', path: 'a' },
      { name: 'b', path: 'a/b' },
    ]);
  });

  it('picks the name the daemon would pick to keep both', () => {
    const taken = new Set(['Report (2).pdf']);
    expect(freeName('Report.pdf', (name) => taken.has(name))).toBe('Report (3).pdf');
    expect(freeName('New folder', () => false)).toBe('New folder (2)');
    expect(freeName('.env', () => false)).toBe('.env (2)');
  });
});
