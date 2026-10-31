import { describe, expect, it } from 'vitest';
import type { InstalledApp } from '../installed';
import { actionsFor, type AppFileAction } from './fileActions';

function action(id: string, rule: Partial<AppFileAction>): AppFileAction {
  return {
    id,
    label: { en: id },
    extensions: [],
    folders: false,
    multiple: false,
    writes: false,
    ...rule,
  };
}

function app(
  id: string,
  fileActions: AppFileAction[],
  permissions = ['files.shares'],
): InstalledApp {
  return {
    id,
    version: '1.0.0',
    name: { en: id },
    description: null,
    entry: `/apps/${id}/index.html`,
    icon: `/api/apps/${id}/icon`,
    window: {},
    permissions,
    logLevel: 'info',
    fileHandlers: [],
    fileActions,
  };
}

const archiver = app('com.example.archiver', [
  action('extract', { extensions: ['zip', 'tar.gz'], writes: true }),
  action('compress', { extensions: ['txt', 'md'], folders: true, multiple: true, writes: true }),
  action('inspect', { extensions: ['zip'] }),
]);

const ids = (offered: ReturnType<typeof actionsFor>) => offered.map((entry) => entry.action.id);

describe('file actions', () => {
  it('offers an action for one matching file, by extension and case-insensitive', () => {
    expect(ids(actionsFor([{ name: 'Backup.ZIP', kind: 'file' }], [archiver], true))).toEqual([
      'extract',
      'inspect',
    ]);
    expect(ids(actionsFor([{ name: 'site.tar.gz', kind: 'file' }], [archiver], true))).toEqual([
      'extract',
    ]);
    expect(ids(actionsFor([{ name: 'zip', kind: 'file' }], [archiver], true))).toEqual([]);
  });

  it('takes several items only for actions that say so, and every item must match', () => {
    const mixed = [
      { name: 'notes.md', kind: 'file' },
      { name: 'Photos', kind: 'folder' },
    ];
    expect(ids(actionsFor(mixed, [archiver], true))).toEqual(['compress']);
    expect(ids(actionsFor([...mixed, { name: 'a.zip', kind: 'file' }], [archiver], true))).toEqual(
      [],
    );
    expect(
      ids(
        actionsFor(
          [
            { name: 'a.zip', kind: 'file' },
            { name: 'b.zip', kind: 'file' },
          ],
          [archiver],
          true,
        ),
      ),
    ).toEqual([]);
  });

  it('hides actions that write where the user may only read, and apps without files.shares', () => {
    expect(ids(actionsFor([{ name: 'a.zip', kind: 'file' }], [archiver], false))).toEqual([
      'inspect',
    ]);
    const revoked = app('com.example.revoked', [action('extract', { extensions: ['zip'] })], []);
    expect(actionsFor([{ name: 'a.zip', kind: 'file' }], [revoked], true)).toEqual([]);
    expect(actionsFor([], [archiver], true)).toEqual([]);
  });
});
