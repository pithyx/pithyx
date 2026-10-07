import { describe, expect, it } from 'vitest';
import type { PoolList } from '../sdk/store';
import { accessOf, appPools, folderPath, manifestFacts, withSubject } from './model';

describe('manifestFacts', () => {
  it('reads what the install dialog shows', () => {
    const facts = manifestFacts({
      name: { en: 'Chat', de: 'Chat' },
      author: { name: 'Jan Mueller', url: 'https://mueller.example' },
      permissions: ['notifications', 'directory.read'],
      storage: [
        { id: 'attachments', name: { en: 'Attachments' }, access: 'readWrite', required: true },
      ],
      backend: { root: true, elevated: ['device:/dev/dri/renderD128'] },
    });
    expect(facts).toMatchObject({
      author: 'Jan Mueller',
      permissions: ['notifications', 'directory.read'],
      storage: [{ id: 'attachments', access: 'readWrite', required: true }],
      backend: true,
      root: true,
      elevated: ['device:/dev/dri/renderD128'],
    });
  });

  it('treats a frontend-only manifest as one without backend, root or rights', () => {
    const facts = manifestFacts({ name: { en: 'Notes' }, permissions: [] });
    expect(facts).toMatchObject({
      backend: false,
      root: false,
      elevated: [],
      storage: [],
      author: null,
    });
    expect(manifestFacts(null).permissions).toEqual([]);
  });
});

describe('accessOf', () => {
  it('says everybody for the built-in group, administrators for no grant, some otherwise', () => {
    expect(accessOf([{ subjectKind: 'group', subjectId: 100, level: 'allow' }], 100)).toBe(
      'everybody',
    );
    expect(accessOf([], 100)).toBe('admins');
    expect(accessOf([{ subjectKind: 'user', subjectId: 7, level: 'allow' }], 100)).toBe('some');
  });
});

describe('withSubject', () => {
  it('adds and removes one grant and keeps the others', () => {
    const rita = withSubject([], 'user', 7, true);
    expect(rita).toEqual([{ subjectKind: 'user', subjectId: 7, level: 'allow' }]);
    const both = withSubject(rita, 'group', 7, true);
    expect(both).toHaveLength(2);
    expect(withSubject(both, 'user', 7, false)).toEqual([
      { subjectKind: 'group', subjectId: 7, level: 'allow' },
    ]);
    expect(withSubject(rita, 'user', 7, true)).toEqual(rita);
  });
});

describe('appPools', () => {
  it('offers the online writable pools by name', () => {
    const pool = (name: string, state: string, readOnly = false) =>
      ({ name, state, readOnly }) as PoolList['pools'][number];
    const pools = {
      pools: [
        pool('tank', 'ONLINE'),
        pool('backup', 'DEGRADED'),
        pool('old', 'FAULTED'),
        pool('ro', 'ONLINE', true),
      ],
      datasets: [],
    } as unknown as PoolList;
    expect(appPools(pools)).toEqual(['backup', 'tank']);
  });
});

describe('folderPath', () => {
  it('normalizes what an administrator types into a relative path', () => {
    expect(folderPath('/Photos/2026/')).toBe('Photos/2026');
    expect(folderPath('Photos\\2026')).toBe('Photos/2026');
    expect(folderPath('  ')).toBe('');
    expect(folderPath('a//b')).toBe('a/b');
  });
});
