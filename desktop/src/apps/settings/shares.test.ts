import { describe, expect, it } from 'vitest';
import type { Dataset, Pool, PoolList, ShareUserAccess } from '../sdk/internal';
import { sharePools, withAccess } from './shares';

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

describe('withAccess', () => {
  it('changes one user and leaves the others as they are', () => {
    const users: ShareUserAccess[] = [
      { userId: 1, username: 'admin', displayName: null, administrator: true, access: 'none' },
      { userId: 2, username: 'anna', displayName: 'Anna', administrator: false, access: 'none' },
    ];
    const changed = withAccess(users, 2, 'readWrite');
    expect(changed.map((user) => user.access)).toEqual(['none', 'readWrite']);
    expect(users[1]?.access).toBe('none');
  });
});
