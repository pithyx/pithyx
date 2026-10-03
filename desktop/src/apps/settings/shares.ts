import type { PithyxApp } from '@pithyx/sdk';
import { useQuery } from '@tanstack/react-query';
import { internalCall, type PoolList, type ShareAccess, type SubjectGrant } from '../sdk/internal';

/** Every cached answer of the Shared folders page; a change of the shares or the pools refreshes them. */
export const sharesKey = ['settings', 'shares'] as const;

export const shareListKey = [...sharesKey, 'list'] as const;

export function shareGrantsKey(shareId: number) {
  return [...sharesKey, 'grants', shareId] as const;
}

export const poolsKey = [...sharesKey, 'pools'] as const;

/** Every share with its space and a summary of the rights. */
export function useShares(app: PithyxApp) {
  return useQuery({
    queryKey: shareListKey,
    queryFn: () => internalCall(app, 'internal.admin.shares.list', {}),
  });
}

/** The grants of users and groups on one share, and every user's effective access. */
export function useShareGrants(app: PithyxApp, shareId: number) {
  return useQuery({
    queryKey: shareGrantsKey(shareId),
    queryFn: () => internalCall(app, 'internal.admin.shares.grants', { shareId }),
  });
}

export function usePools(app: PithyxApp) {
  return useQuery({
    queryKey: poolsKey,
    queryFn: () => internalCall(app, 'internal.admin.storage.pools', {}),
  });
}

/**
 * Pools that have a place for shared folders (`<pool>/pithyx/shares`, D100), sorted by name. Only for the pool
 * select: the daemon refuses every other pool with its own error.
 */
export function sharePools(pools: PoolList): string[] {
  const datasets = new Set(pools.datasets.map((dataset) => dataset.name));
  return pools.pools
    .map((pool) => pool.name)
    .filter((name) => datasets.has(`${name}/pithyx/shares`))
    .sort((a, b) => a.localeCompare(b, 'en'));
}

/** The grants with one user's or group's access changed; `none` removes its grant. */
export function withSubjectAccess(
  grants: readonly SubjectGrant[],
  subjectKind: SubjectGrant['subjectKind'],
  subjectId: number,
  access: ShareAccess,
): SubjectGrant[] {
  const others = grants.filter(
    (grant) => !(grant.subjectKind === subjectKind && grant.subjectId === subjectId),
  );
  return access === 'none' ? others : [...others, { subjectKind, subjectId, level: access }];
}

/** The access a grant list gives one subject directly. */
export function accessOf(
  grants: readonly SubjectGrant[],
  subjectKind: SubjectGrant['subjectKind'],
  subjectId: number,
): ShareAccess {
  const grant = grants.find(
    (entry) => entry.subjectKind === subjectKind && entry.subjectId === subjectId,
  );
  return grant === undefined ? 'none' : grant.level === 'readWrite' ? 'readWrite' : 'read';
}
