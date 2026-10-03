import type { PithyxApp } from '@pithyx/sdk';
import { useQuery } from '@tanstack/react-query';
import { usersCall } from '../sdk/users';

export const usersKey = ['users'] as const;
export const groupsKey = ['groups'] as const;

export function useUsers(app: PithyxApp) {
  return useQuery({
    queryKey: usersKey,
    queryFn: () => usersCall(app, 'internal.admin.users.list', {}),
  });
}

export function useGroups(app: PithyxApp) {
  return useQuery({
    queryKey: groupsKey,
    queryFn: () => usersCall(app, 'internal.admin.groups.list', {}),
  });
}
