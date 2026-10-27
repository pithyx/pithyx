import type { AppManifest } from '@pithyx/sdk';

/** The permissions of the manifest schema's `Permission`; every other name in `permissions` is an elevated right. */
export const PLAIN_PERMISSIONS: readonly string[] = [
  'files.read',
  'files.write',
  'files.shares',
  'notifications',
  'notifications.external',
  'directory.read',
  'network.internet',
  'network.lan',
];

export const DEVICE_PREFIX = 'device:';

export function isElevated(name: string): boolean {
  return !PLAIN_PERMISSIONS.includes(name);
}

/** The elevated rights the manifest asks for, in its order. */
export function elevatedRightsOf(manifest: AppManifest): string[] {
  return Object.keys(manifest.permissions).filter(isElevated);
}

/** The plain permissions the manifest asks for, in its order. */
export function permissionsOf(manifest: AppManifest): string[] {
  return Object.keys(manifest.permissions).filter((name) => !isElevated(name));
}
