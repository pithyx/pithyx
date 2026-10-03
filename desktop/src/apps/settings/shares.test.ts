import { describe, expect, it } from 'vitest';
import type { Dataset, Pool, PoolList, SubjectGrant } from '../sdk/internal';
import { accessOf, sharePools, withSubjectAccess } from './shares';

function pool(name: string): Pool {
  return { name } as Pool;
}

function dataset(name: string): Dataset {
  return { name, pool: name.split('/')[0] } as Dataset;
}

describe('sharePools', () => {
  it('offers only pools with a place for shared folders, by name', () => {
    const pools: PoolList = {
      pools: [pool('tank'), pool('bare'), pool('fresh'), pool('archive')],
      datasets: [
        dataset('tank'),
        dataset('tank/pithyx'),
        dataset('tank/pithyx/shares'),
        dataset('bare'),
        dataset('bare/pithyx/shares-old'),
        dataset('fresh/pithyx'),
        dataset('archive/pithyx/shares'),
      ],
    };
    expect(sharePools(pools)).toEqual(['archive', 'tank']);
  });
});

describe('withSubjectAccess', () => {
  it('changes one subject and leaves the others as they are', () => {
    const grants: SubjectGrant[] = [
      { subjectKind: 'user', subjectId: 2, level: 'read' },
      { subjectKind: 'group', subjectId: 2, level: 'readWrite' },
    ];
    const changed = withSubjectAccess(grants, 'user', 2, 'readWrite');
    expect(accessOf(changed, 'user', 2)).toBe('readWrite');
    expect(accessOf(changed, 'group', 2)).toBe('readWrite');
    expect(accessOf(grants, 'user', 2)).toBe('read');
    const removed = withSubjectAccess(changed, 'group', 2, 'none');
    expect(removed).toEqual([{ subjectKind: 'user', subjectId: 2, level: 'readWrite' }]);
    expect(accessOf(removed, 'group', 3)).toBe('none');
  });
});
