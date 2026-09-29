import type { PithyxApp } from '@pithyx/sdk';
import { useQuery } from '@tanstack/react-query';
import {
  internalCall,
  type PoolList,
  type ShareAccess,
  type ShareUserAccess,
} from '../sdk/internal';

/** Every cached answer of the Shared folders page; a change of the shares or the pools refreshes them. */
export const sharesKey = ['settings', 'shares'] as const;

export const shareListKey = [...sharesKey, 'list'] as const;

export function shareAccessKey(shareId: number) {
  return [...sharesKey, 'access', shareId] as const;
}

export const poolsKey = [...sharesKey, 'pools'] as const;

/** Every share with its space and a summary of the rights. */
export function useShares(app: PithyxApp) {
  return useQuery({
    queryKey: shareListKey,
    queryFn: () => internalCall(app, 'internal.admin.shares.list', {}),
  });
}

/** Every user with their access to one share. */
export function useShareAccess(app: PithyxApp, shareId: number) {
  return useQuery({
    queryKey: shareAccessKey(shareId),
    queryFn: () => internalCall(app, 'internal.admin.shares.access', { shareId }),
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

/** The users with one user's access changed, for an optimistic update. */
export function withAccess(
  users: readonly ShareUserAccess[],
  userId: number,
  access: ShareAccess,
): ShareUserAccess[] {
  return users.map((user) => (user.userId === userId ? { ...user, access } : user));
}
