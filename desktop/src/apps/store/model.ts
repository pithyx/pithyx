import type { AppAccess, PoolList, SubjectGrant } from '../sdk/store';

/** The permissions of manifest v1 the Store explains; any other shows its name with a general text. */
export const KNOWN_PERMISSIONS = [
  'files.read',
  'files.write',
  'files.shares',
  'notifications',
  'notifications.external',
  'directory.read',
  'network.internet',
  'network.lan',
] as const;

export type KnownPermission = (typeof KNOWN_PERMISSIONS)[number];

export function isKnownPermission(permission: string): permission is KnownPermission {
  return (KNOWN_PERMISSIONS as readonly string[]).includes(permission);
}

/** The device classes of D387 by their path: a right on one stands for every node of the class that is plugged in. */
export const DEVICE_CLASSES = {
  '/dev/dri': 'dri',
  '/dev/serial': 'serial',
  '/dev/net/tun': 'tun',
} as const;

export type DeviceClass = (typeof DEVICE_CLASSES)[keyof typeof DEVICE_CLASSES];

/**
 * What kind of elevated right a manifest names (D311, D387), for its title and risk text; the value is its device or
 * capability, and a device class has its name.
 */
export type ElevatedKind =
  | { kind: 'hostNetwork' }
  | { kind: 'hostPid' }
  | { kind: 'dockerSocket' }
  | { kind: 'dockerSocketRaw' }
  | { kind: 'device'; value: string; deviceClass?: DeviceClass }
  | { kind: 'capability'; value: string }
  | { kind: 'unknown'; value: string };

export function elevatedKind(right: string): ElevatedKind {
  if (right === 'host.network') {
    return { kind: 'hostNetwork' };
  }
  if (right === 'host.pid') {
    return { kind: 'hostPid' };
  }
  if (right === 'docker.socket') {
    return { kind: 'dockerSocket' };
  }
  if (right === 'docker.socket.raw') {
    return { kind: 'dockerSocketRaw' };
  }
  if (right.startsWith('device:')) {
    const value = right.slice('device:'.length);
    const deviceClass = (DEVICE_CLASSES as Record<string, DeviceClass | undefined>)[value];
    return deviceClass === undefined
      ? { kind: 'device', value }
      : { kind: 'device', value, deviceClass };
  }
  if (right.startsWith('capability:')) {
    return { kind: 'capability', value: right.slice('capability:'.length) };
  }
  return { kind: 'unknown', value: right };
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
  /** Elevated rights of `backend.elevated` (D311), granted only after a step-up. */
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

/** The texts under a device right in the Store. */
export type DeviceNoteKey =
  | 'elevated.deviceOptional'
  | 'elevated.deviceMissing'
  | 'elevated.deviceMissingOptional'
  | 'elevated.deviceAbsent'
  | 'elevated.deviceAbsentOptional';

/** A device of an app with whether it is optional and plugged in now, as the daemon reports it (D387). */
export interface DeviceState {
  path: string;
  optional: boolean;
  present: boolean;
}

/**
 * The line under a device right: in the install dialog a missing required device stops the install and a missing
 * optional one is only a hint; on an installed app's page a missing one says what its background service does.
 */
export function deviceNote(
  right: string,
  devices: readonly DeviceState[] | null | undefined,
  where: 'install' | 'installed',
): DeviceNoteKey | null {
  const parsed = elevatedKind(right);
  if (parsed.kind !== 'device') {
    return null;
  }
  const device = devices?.find((entry) => entry.path === parsed.value);
  if (device === undefined) {
    return null;
  }
  if (device.present) {
    return device.optional ? 'elevated.deviceOptional' : null;
  }
  if (where === 'install') {
    return device.optional ? 'elevated.deviceMissingOptional' : 'elevated.deviceMissing';
  }
  return device.optional ? 'elevated.deviceAbsentOptional' : 'elevated.deviceAbsent';
}
