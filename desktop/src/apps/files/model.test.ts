import type { FileEntry, FolderPage } from '@pithyx/sdk';
import { describe, expect, it } from 'vitest';
import type { InstalledApp } from '../installed';
import { choiceExtension, currentChoice, defaultAction, openWithChoices } from './defaultAction';
import { itemsToDrop, parseDragged } from './drop';
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
    expect(defaultAction(entry('a.pdf'))).toEqual({ kind: 'view' });
    expect(defaultAction(entry('page.html'))).toEqual({ kind: 'view' });
    expect(defaultAction(entry('a.zip'))).toEqual({ kind: 'download' });
    expect(openWithChoices(entry('a.zip'))).toEqual(['download']);
    expect(openWithChoices(entry('a.png'))).toEqual(['viewer', 'download']);
    expect(openWithChoices(entry('dir', 0, 'folder'))).toEqual([]);
    const app = (id: string, extensions: string[]): InstalledApp => ({
      id,
      version: '1.0.0',
      name: { en: id },
      description: null,
      entry: '/apps/x/index.html',
      icon: '',
      window: {},
      permissions: [],
      logLevel: 'info',
      fileHandlers: [{ extensions, isDefault: false }],
      fileActions: [],
    });
    const apps = [app('com.example.draw', ['png', 'tar.gz']), app('com.example.text', ['txt'])];
    expect(openWithChoices(entry('a.PNG'), apps)).toEqual([
      'app:com.example.draw',
      'viewer',
      'download',
    ]);
    expect(openWithChoices(entry('a.tar.gz'), apps)).toEqual(['app:com.example.draw', 'download']);
    expect(openWithChoices(entry('a.zip'), apps)).toEqual(['download']);
    expect(openWithChoices(entry('png', 0, 'folder'), apps)).toEqual([]);
    expect(defaultAction(entry('dev', 0, 'other'))).toEqual({ kind: 'none' });
  });

  const handler = (id: string, extensions: string[], isDefault: boolean): InstalledApp => ({
    id,
    version: '1.0.0',
    name: { en: id },
    description: null,
    entry: '/apps/x/index.html',
    icon: '',
    window: {},
    permissions: [],
    logLevel: 'info',
    fileHandlers: [{ extensions, isDefault }],
    fileActions: [],
  });

  it('opens a file in the only app that is the default for it, else as before', () => {
    const reader = handler('com.example.reader', ['pdf'], true);
    const other = handler('com.example.other', ['pdf'], true);
    const plain = handler('com.example.plain', ['zip'], false);
    expect(defaultAction(entry('a.PDF'), [reader, plain])).toEqual({
      kind: 'app',
      id: 'com.example.reader',
    });
    expect(defaultAction(entry('a.pdf'), [reader, other])).toEqual({ kind: 'view' });
    expect(defaultAction(entry('a.zip'), [plain])).toEqual({ kind: 'download' });
  });

  it("follows the user's choice while it can still be used", () => {
    const reader = handler('com.example.reader', ['pdf'], true);
    const editor = handler('com.example.editor', ['pdf'], false);
    const apps = [reader, editor];
    expect(defaultAction(entry('a.pdf'), apps, { pdf: 'app:com.example.editor' })).toEqual({
      kind: 'app',
      id: 'com.example.editor',
    });
    expect(defaultAction(entry('a.pdf'), apps, { pdf: 'viewer' })).toEqual({ kind: 'view' });
    expect(defaultAction(entry('a.pdf'), apps, { pdf: 'download' })).toEqual({
      kind: 'download',
    });
    // An app that is gone, or the viewer for a file it cannot show, counts as no choice.
    expect(defaultAction(entry('a.pdf'), [reader], { pdf: 'app:com.example.editor' })).toEqual({
      kind: 'app',
      id: 'com.example.reader',
    });
    expect(defaultAction(entry('a.zip'), [], { zip: 'viewer' })).toEqual({ kind: 'download' });
    expect(defaultAction(entry('dir.pdf', 0, 'folder'), apps, { pdf: 'viewer' })).toEqual({
      kind: 'openFolder',
    });
  });

  it('saves a choice under the last extension the daemon accepts', () => {
    expect(choiceExtension('Report.PDF')).toBe('pdf');
    expect(choiceExtension('backup.tar.gz')).toBe('gz');
    expect(choiceExtension('README')).toBeNull();
    expect(choiceExtension('.bashrc')).toBeNull();
    expect(choiceExtension('trailing.')).toBeNull();
    expect(choiceExtension('odd.ex~t')).toBeNull();
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

describe('drop targets', () => {
  const dragged = { share: 's1', folder: 'a', items: { names: ['b', 'c'] } };

  it('moves entries to another folder or another share', () => {
    expect(itemsToDrop(dragged, { share: 's1', path: '' })).toEqual({ names: ['b', 'c'] });
    expect(itemsToDrop(dragged, { share: 's2', path: 'a' })).toEqual({ names: ['b', 'c'] });
  });

  it('does nothing on the folder the entries are in', () => {
    expect(itemsToDrop(dragged, { share: 's1', path: 'a' })).toBeNull();
    expect(itemsToDrop({ ...dragged, share: null }, { share: 's1', path: '' })).toBeNull();
  });

  it('never moves a folder into itself or below it, but still its siblings', () => {
    expect(itemsToDrop(dragged, { share: 's1', path: 'a/b' })).toEqual({ names: ['c'] });
    expect(itemsToDrop(dragged, { share: 's1', path: 'a/b/deep' })).toEqual({ names: ['c'] });
    expect(
      itemsToDrop({ ...dragged, items: { names: ['b'] } }, { share: 's1', path: 'a/b' }),
    ).toBeNull();
    expect(itemsToDrop(dragged, { share: 's1', path: 'ab' })).toEqual({ names: ['b', 'c'] });
    expect(
      itemsToDrop(
        { ...dragged, folder: '', items: { all: true, except: [] } },
        { share: 's1', path: 'x/y' },
      ),
    ).toEqual({ all: true, except: ['x'] });
  });

  it('reads only the data of a drag of entries', () => {
    expect(parseDragged(JSON.stringify(dragged))).toEqual(dragged);
    expect(parseDragged('not json')).toBeNull();
    expect(parseDragged('{}')).toBeNull();
  });
});

describe('open with', () => {
  const app = (id: string, extensions: string[], isDefault: boolean): InstalledApp => ({
    id,
    version: '1.0.0',
    name: { en: id },
    description: null,
    entry: '/apps/x/index.html',
    icon: '',
    window: {},
    permissions: [],
    logLevel: 'info',
    fileHandlers: [{ extensions, isDefault }],
    fileActions: [],
  });

  it('lists the app that is the default for the type first and marks what a double click does', () => {
    const plain = app('com.example.plain', ['md'], false);
    const editor = app('org.pithyx.editor', ['md'], true);
    expect(openWithChoices(entry('notes.md'), [plain, editor])).toEqual([
      'app:org.pithyx.editor',
      'app:com.example.plain',
      'viewer',
      'download',
    ]);
    expect(currentChoice(entry('notes.md'), [plain, editor])).toBe('app:org.pithyx.editor');
    expect(currentChoice(entry('notes.md'), [plain, editor], { md: 'download' })).toBe('download');
    expect(currentChoice(entry('notes.md'))).toBe('viewer');
    expect(currentChoice(entry('dir', 0, 'folder'))).toBeNull();
  });
});
