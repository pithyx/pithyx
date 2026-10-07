import type { AppAccess, PoolList, SubjectGrant } from '../sdk/store';

/** The permissions of manifest v1 the Store explains; any other shows its name with a general text. */
export const KNOWN_PERMISSIONS = [
  'files.read',
  'files.write',
  'files.shares',
  'notifications',
  'directory.read',
  'network.internet',
] as const;

export type KnownPermission = (typeof KNOWN_PERMISSIONS)[number];

export function isKnownPermission(permission: string): permission is KnownPermission {
  return (KNOWN_PERMISSIONS as readonly string[]).includes(permission);
}

/** One storage request of a manifest: a folder the app asks for. */
export interface StorageRequest {
  id: string;
  name: Record<string, string>;
  access: 'read' | 'readWrite';
  required: boolean;
}

/** What the Store shows of a manifest; the daemon checked it against the schema, so only shapes are read here. */
export interface ManifestFacts {
  name: Record<string, string>;
  description: Record<string, string> | null;
  author: string | null;
  homepage: string | null;
  license: string | null;
  permissions: string[];
  storage: StorageRequest[];
  /** The app brings containers. */
  backend: boolean;
  /** A container runs as root (unprivileged on the host through userns, D304). */
  root: boolean;
  /** Elevated rights of `backend.elevated` (D71); A16 asks for them separately. */
  elevated: string[];
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

const texts = (value: unknown): Record<string, string> | null =>
  isRecord(value)
    ? Object.fromEntries(
        Object.entries(value).filter(
          (entry): entry is [string, string] => typeof entry[1] === 'string',
        ),
      )
    : null;

const strings = (value: unknown): string[] =>
  Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string') : [];

export function manifestFacts(manifest: unknown): ManifestFacts {
  const root = isRecord(manifest) ? manifest : {};
  const backend = isRecord(root.backend) ? root.backend : null;
  const author =
    isRecord(root.author) && typeof root.author.name === 'string' ? root.author.name : null;
  return {
    name: texts(root.name) ?? {},
    description: texts(root.description),
    author,
    homepage: typeof root.homepage === 'string' ? root.homepage : null,
    license: typeof root.license === 'string' ? root.license : null,
    permissions: strings(root.permissions),
    storage: (Array.isArray(root.storage) ? root.storage : []).filter(isRecord).map((request) => ({
      id: typeof request.id === 'string' ? request.id : '',
      name: texts(request.name) ?? {},
      access: request.access === 'readWrite' ? 'readWrite' : 'read',
      required: request.required === true,
    })),
    backend: backend !== null,
    root: backend?.root === true,
    elevated: strings(backend?.elevated),
  };
}

/** The pools the first install may put the app data on: online and writable; the daemon names any other problem. */
export function appPools(pools: PoolList): string[] {
  return pools.pools
    .filter((pool) => (pool.state === 'ONLINE' || pool.state === 'DEGRADED') && !pool.readOnly)
    .map((pool) => pool.name)
    .sort((a, b) => a.localeCompare(b, 'en'));
}

/** Who may use the app as the access choice shows it: everybody is a grant to the built-in group `users`. */
export function accessOf(
  grants: readonly SubjectGrant[],
  everybodyGroup: number,
): AppAccess | 'some' {
  if (grants.some((grant) => grant.subjectKind === 'group' && grant.subjectId === everybodyGroup)) {
    return 'everybody';
  }
  return grants.length === 0 ? 'admins' : 'some';
}

/** The grants with one user's or group's use of the app switched on or off. */
export function withSubject(
  grants: readonly SubjectGrant[],
  subjectKind: SubjectGrant['subjectKind'],
  subjectId: number,
  allowed: boolean,
): SubjectGrant[] {
  const others = grants.filter(
    (grant) => !(grant.subjectKind === subjectKind && grant.subjectId === subjectId),
  );
  return allowed ? [...others, { subjectKind, subjectId, level: 'allow' }] : others;
}

/**
 * A folder path in a shared folder as the daemon wants it: relative, with `/` between names, without empty names.
 * Leading and trailing slashes and backslashes from a pasted Windows path are dropped.
 */
export function folderPath(input: string): string {
  return input
    .trim()
    .split(/[\\/]+/)
    .filter((part) => part !== '')
    .join('/');
}

/** The icon URL of an installed app, as the topic `apps` names it: it changes with every package. */
export function iconUrl(app: { id: string; sha256: string }): string {
  return `/api/apps/${app.id}/icon?v=${app.sha256.slice(0, 12)}`;
}
