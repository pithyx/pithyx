import type { components } from '@pithyx/api-client';

type GrantEntry = components['schemas']['GrantEntry'];
type InheritedGrant = components['schemas']['InheritedGrant'];
export type Access = 'none' | 'read' | 'readWrite';

/** The resource of a shared folder's grant. */
export function shareResource(shareId: number): string {
  return `share:${String(shareId)}`;
}

/** Whether the grants allow a right, protocol or app. */
export function allows(grants: readonly GrantEntry[], resource: string): boolean {
  return grants.some((grant) => grant.resource === resource);
}

/** The grants with a right, protocol or app given or taken. */
export function withAllowed(
  grants: readonly GrantEntry[],
  resource: string,
  on: boolean,
): GrantEntry[] {
  const others = grants.filter((grant) => grant.resource !== resource);
  return on ? [...others, { resource, level: 'allow' }] : others;
}

/** The access the grants give to a shared folder. */
export function shareAccess(grants: readonly GrantEntry[], shareId: number): Access {
  const level = grants.find((grant) => grant.resource === shareResource(shareId))?.level;
  return level === 'read' || level === 'readWrite' ? level : 'none';
}

/** The grants with one shared folder's access changed; `none` removes its grant. */
export function withShareAccess(
  grants: readonly GrantEntry[],
  shareId: number,
  access: Access,
): GrantEntry[] {
  const resource = shareResource(shareId);
  const others = grants.filter((grant) => grant.resource !== resource);
  return access === 'none' ? others : [...others, { resource, level: access }];
}

/** The groups that give a resource, each named once, for "through" hints. */
export function givenBy(inherited: readonly InheritedGrant[], resource: string): string[] {
  return [
    ...new Set(
      inherited.filter((grant) => grant.resource === resource).map((grant) => grant.groupName),
    ),
  ];
}
