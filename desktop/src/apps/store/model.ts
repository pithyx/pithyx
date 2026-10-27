import { elevated as elevatedRules } from '@pithyx/sdk/schema/compose.v1.rules.json';
import type { AppAccess, PoolList, SubjectGrant } from '../sdk/store';

/** The permissions of the manifest the Store explains; any other name is an elevated right (D388). */
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

/** How much an elevated right can do on the box, and so how much granting it asks (D388). */
export type RiskTier = 'elevated' | 'high' | 'critical';

/**
 * The tier of an elevated right from `elevated.tiers` of the compose rules, as the box and the CLI read it: a device
 * below a listed folder (`device:/dev/dri/renderD128`) has the folder's tier.
 */
export function tierOf(right: string): RiskTier {
  const rights = elevatedRules.tiers.rights as Record<string, string | undefined>;
  for (let name = right; ; name = name.slice(0, name.lastIndexOf('/'))) {
    const tier = rights[name];
    if (tier !== undefined) {
      return tier as RiskTier;
    }
    if (!name.startsWith('device:') || name.lastIndexOf('/') <= 'device:'.length) {
      return elevatedRules.tiers.default as RiskTier;
    }
  }
}

const TIER_ORDER: readonly RiskTier[] = ['elevated', 'high', 'critical'];

/** The highest tier among the rights, or null for none. */
export function highestTier(rights: readonly string[]): RiskTier | null {
  return rights.reduce<RiskTier | null>(
    (highest, right) =>
      highest === null || TIER_ORDER.indexOf(tierOf(right)) > TIER_ORDER.indexOf(highest)
        ? tierOf(right)
        : highest,
    null,
  );
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

/** What an app sends out of the box, from the manifest's `privacy` (D389). */
export type PrivacyLevel = 'none' | 'optional' | 'telemetry' | 'vendorAccount';

/** How far an app is, from the manifest's `maturity`; without it an app is `stable`. */
export type Maturity = 'stable' | 'beta' | 'experimental';

/** A picture of the app in its package (D389): its path under `frontend/` and an optional caption. */
export interface ScreenshotFact {
  file: string;
  caption: Record<string, string> | null;
}

/** The fields the Store shows next to the rights (D389); each is optional in the manifest. */
export interface StoreFacts {
  /** The manifest's `source`: a URL, `private`, or null for a manifest without it. */
  source: string | null;
  arch: string[];
  privacy: { sends: PrivacyLevel; note: Record<string, string> | null } | null;
  security: string | null;
  maturity: Maturity;
  categories: string[];
  keywords: string[];
  locales: string[];
  screenshots: ScreenshotFact[];
  releaseNotes: Record<string, string> | null;
  /** Approximate memory and disk use in MB. */
  requirements: { memory: number | null; disk: number | null };
  audience: 'everyone' | 'admins' | null;
}

/** What the Store shows of a manifest; the daemon checked it against the schema, so only shapes are read here. */
export interface ManifestFacts extends StoreFacts {
  name: Record<string, string>;
  description: Record<string, string> | null;
  author: string | null;
  homepage: string | null;
  license: string | null;
  /** The plain permissions of the manifest's `permissions` map, in its order. */
  permissions: string[];
  /**
   * The author's reason for each permission and elevated right by its name (D388). An app installed before reasons
   * were required has none.
   */
  reasons: Record<string, Record<string, string>>;
  /** The hosts `network.internet` names, if any. */
  hosts: string[];
  storage: StorageRequest[];
  /** The app brings containers. */
  backend: boolean;
  /** A container runs as root (unprivileged on the host through userns, D304). */
  root: boolean;
  /** The elevated rights of the manifest's `permissions` map (D311), each in a risk tier (D388). */
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

const isPrivacyLevel = (value: unknown): value is PrivacyLevel =>
  value === 'none' || value === 'optional' || value === 'telemetry' || value === 'vendorAccount';

const strings = (value: unknown): string[] =>
  Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string') : [];

export function manifestFacts(manifest: unknown): ManifestFacts {
  const root = isRecord(manifest) ? manifest : {};
  const backend = isRecord(root.backend) ? root.backend : null;
  const author =
    isRecord(root.author) && typeof root.author.name === 'string' ? root.author.name : null;
  const rights = isRecord(root.permissions) ? root.permissions : {};
  const names = Object.keys(rights);
  const reasons: Record<string, Record<string, string>> = {};
  for (const name of names) {
    const grant = rights[name];
    const reason = isRecord(grant) ? texts(grant.reason) : null;
    if (reason !== null && Object.keys(reason).length > 0) {
      reasons[name] = reason;
    }
  }
  const internet = rights['network.internet'];
  const privacy = isRecord(root.privacy) ? root.privacy : null;
  const requirements = isRecord(root.requirements) ? root.requirements : {};
  return {
    source: typeof root.source === 'string' ? root.source : null,
    arch: strings(root.arch),
    privacy:
      privacy !== null && isPrivacyLevel(privacy.sends)
        ? { sends: privacy.sends, note: texts(privacy.note) }
        : null,
    security: typeof root.security === 'string' ? root.security : null,
    maturity:
      root.maturity === 'beta' || root.maturity === 'experimental' ? root.maturity : 'stable',
    categories: strings(root.categories),
    keywords: strings(root.keywords),
    locales: strings(root.locales),
    screenshots: (Array.isArray(root.screenshots) ? root.screenshots : [])
      .filter(isRecord)
      .filter(
        (shot): shot is Record<string, unknown> & { file: string } => typeof shot.file === 'string',
      )
      .map((shot) => ({ file: shot.file, caption: texts(shot.caption) })),
    releaseNotes: texts(root.releaseNotes),
    requirements: {
      memory: typeof requirements.memory === 'number' ? requirements.memory : null,
      disk: typeof requirements.disk === 'number' ? requirements.disk : null,
    },
    audience: root.audience === 'everyone' || root.audience === 'admins' ? root.audience : null,
    name: texts(root.name) ?? {},
    description: texts(root.description),
    author,
    homepage: typeof root.homepage === 'string' ? root.homepage : null,
    license: typeof root.license === 'string' ? root.license : null,
    permissions: names.filter(isKnownPermission),
    reasons,
    hosts: isRecord(internet) ? strings(internet.hosts) : [],
    storage: (Array.isArray(root.storage) ? root.storage : []).filter(isRecord).map((request) => ({
      id: typeof request.id === 'string' ? request.id : '',
      name: texts(request.name) ?? {},
      access: request.access === 'readWrite' ? 'readWrite' : 'read',
      required: request.required === true,
    })),
    backend: backend !== null,
    root: backend?.root === true,
    elevated: names.filter((name) => !isKnownPermission(name)),
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

/**
 * Whether the app's interface speaks the user's language (D389): a locale of the manifest equals the language or
 * shares its base (`pt` for `pt-BR`). Null when the manifest names no locales, so the Store says nothing.
 */
export function speaksLanguage(locales: readonly string[], language: string): boolean | null {
  if (locales.length === 0) {
    return null;
  }
  const base = (tag: string) => tag.toLowerCase().split('-')[0];
  return locales.some(
    (locale) => locale.toLowerCase() === language.toLowerCase() || base(locale) === base(language),
  );
}

/** A hint of the install dialog that the box may be too small for the app; it never stops the install (D389). */
export type RequirementHint =
  | { kind: 'memory'; needed: number; available: number }
  | { kind: 'disk'; needed: number; available: number };

/**
 * The requirements of a manifest the box falls short of, in MB: memory against the box's physical memory, disk
 * against the free space of the app pool. Unknown sizes give no hint.
 */
export function requirementHints(
  requirements: StoreFacts['requirements'],
  memoryBytes: number | null,
  poolFreeBytes: number | null,
): RequirementHint[] {
  const mb = (bytes: number) => Math.floor(bytes / (1024 * 1024));
  const hints: RequirementHint[] = [];
  if (
    requirements.memory !== null &&
    memoryBytes !== null &&
    mb(memoryBytes) < requirements.memory
  ) {
    hints.push({ kind: 'memory', needed: requirements.memory, available: mb(memoryBytes) });
  }
  if (
    requirements.disk !== null &&
    poolFreeBytes !== null &&
    mb(poolFreeBytes) < requirements.disk
  ) {
    hints.push({ kind: 'disk', needed: requirements.disk, available: mb(poolFreeBytes) });
  }
  return hints;
}

/** The categories of the manifest schema, in the order the Store's filter lists them (D389). */
export const CATEGORIES = [
  'productivity',
  'office',
  'communication',
  'files',
  'backup',
  'photos',
  'media',
  'home-automation',
  'network',
  'security',
  'monitoring',
  'development',
  'education',
  'finance',
  'games',
  'utilities',
] as const;

/** One category of the manifest schema. */
export type Category = (typeof CATEGORIES)[number];

/** Whether a manifest's category is one this desktop knows. */
export const isCategory = (value: string): value is Category =>
  (CATEGORIES as readonly string[]).includes(value);

/** The catalog apps in a category, or all of them for no category. */
export function inCategory<T extends { categories: readonly string[] }>(
  apps: readonly T[],
  category: string | null,
): T[] {
  return category === null ? [...apps] : apps.filter((app) => app.categories.includes(category));
}

/** The URL an installed app serves one of its screenshots at: its frontend path below `/apps/<id>/`. */
export function screenshotUrl(appId: string, file: string): string {
  return `/apps/${appId}/${file.replace(/^frontend\//, '')}`;
}
