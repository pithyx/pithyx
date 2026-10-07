import type { DirectoryGroup, DirectoryUser, GroupRef } from '@pithyx/sdk';

/** A user of the mock desktop, as an app sees the signed-in user in its context. */
export interface MockUser {
  id: number;
  name: string;
  displayName: string | null;
  groups: GroupRef[];
  isAdmin: boolean;
  twoFactor: boolean;
}

/** The groups of the mock box, with gids from the box's range. */
export const MOCK_GROUPS: (GroupRef & { description: string })[] = [
  { id: 30000, name: 'family', description: 'Everyone at home' },
  { id: 30001, name: 'office', description: 'The home office' },
];

const [family, office] = MOCK_GROUPS.map(({ id, name }) => ({ id, name })) as [GroupRef, GroupRef];

/** Users to switch between: an administrator, a user in two groups, and one in none. */
export const MOCK_USERS: MockUser[] = [
  {
    id: 1,
    name: 'alice',
    displayName: 'Alice Admin',
    groups: [family],
    isAdmin: true,
    twoFactor: true,
  },
  {
    id: 2,
    name: 'bob',
    displayName: 'Bob',
    groups: [family, office],
    isAdmin: false,
    twoFactor: false,
  },
  { id: 3, name: 'carol', displayName: null, groups: [], isAdmin: false, twoFactor: false },
];

/** The users as `directory.users` lists them. */
export function directoryUsers(): DirectoryUser[] {
  return MOCK_USERS.map((user) => ({
    id: user.id,
    name: user.name,
    displayName: user.displayName,
    groups: user.groups.map((group) => group.id),
  }));
}

/** The groups as `directory.groups` lists them. */
export function directoryGroups(): DirectoryGroup[] {
  return MOCK_GROUPS.map(({ id, name, description }) => ({ id, name, description }));
}
